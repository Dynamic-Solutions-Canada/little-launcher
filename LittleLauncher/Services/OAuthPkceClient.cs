using LittleLauncher.Localization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// OAuth 2.0 Authorization Code flow with PKCE and a loopback redirect, the flow both Microsoft
/// and Google define for native desktop apps.
/// </summary>
/// <remarks>
/// <para><b>Hand-rolled rather than MSAL + the Google SDK, deliberately.</b> Both providers are
/// plain OAuth 2.0 here and the app needs exactly one small file from each, so two large SDKs
/// with two different token-cache and threading idioms would be more surface, not less. One flow
/// serving both keeps the refresh and revocation logic in a single place, and keeps the
/// dependency list, which this project keeps short on purpose, unchanged.</para>
/// <para><b>Why loopback and not a custom URI scheme:</b> a registered scheme is global to the
/// machine, so any other app can claim it and receive the authorization code. A loopback listener
/// can only be reached by something already running locally, and the port is chosen per attempt.
/// PKCE then makes an intercepted code useless without the verifier, which never leaves this
/// process.</para>
/// </remarks>
public static class OAuthPkceClient
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Everything that differs between the two providers.</summary>
    public sealed class Endpoint
    {
        public required string AuthorizeUrl { get; init; }
        public required string TokenUrl { get; init; }
        public required string ClientId { get; init; }
        public required string Scope { get; init; }

        /// <summary>Included for the deployed Google Desktop registration; Microsoft omits it.</summary>
        public string ClientSecret { get; init; } = "";

        /// <summary>Extra authorize-request parameters, e.g. Google's offline access.</summary>
        public IReadOnlyDictionary<string, string> ExtraAuthorizeParams { get; init; }
            = new Dictionary<string, string>();
    }

    /// <summary>The tokens received, or a safe explanation of why authorization failed.</summary>
    /// <remarks>Messages contain no raw provider response, authorization code, or token.</remarks>
    public sealed record SignInResult(CloudTokens? Tokens, string Message);

    /// <summary>
    /// Run an interactive sign-in: open the system browser, validate its redirect, and exchange
    /// the code. The caller saves tokens only after authorization succeeds.
    /// </summary>
    /// <remarks>
    /// The system browser, not an embedded WebView, because that is what both providers
    /// require for native apps, and it is what lets the user see the real address bar and reuse
    /// sessions and password managers they already trust. This app never sees the credentials.
    /// </remarks>
    /// <param name="scopeOverride">
    /// Ask for something other than the endpoint's default scope, used for incremental consent,
    /// where the wider sharing permission is requested only when the user first needs it rather
    /// than being demanded of everyone at first sign-in.
    /// </param>
    public static Task<SignInResult> SignInAsync(
        Endpoint endpoint, string? scopeOverride = null, CancellationToken ct = default) =>
        SignInAsync(endpoint, scopeOverride, OpenInBrowser, TimeSpan.FromMinutes(5), ct);

    /// <summary>
    /// Run the same flow with a supplied browser opener and callback deadline. Tests can drive
    /// real loopback HTTP requests without opening a browser or contacting a live account.
    /// </summary>
    internal static async Task<SignInResult> SignInAsync(
        Endpoint endpoint, string? scopeOverride, Action<string> openBrowser,
        TimeSpan callbackTimeout, CancellationToken ct)
    {
        using var listener = new HttpListener();
        HttpListenerContext? callback = null;
        var result = new SignInResult(null, UiText.Get("cloud.signIn.notCompleted"));
        var phase = SignInPhase.Listener;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (StartOnFreePort(listener, out string redirectUri) == 0)
            {
                return new SignInResult(null, UiText.Get("cloud.signIn.listenerUnavailable"));
            }

            string verifier = CreateCodeVerifier();
            string challenge = CreateCodeChallenge(verifier);
            string state = Base64Url(RandomNumberGenerator.GetBytes(32));
            string scope = string.IsNullOrWhiteSpace(scopeOverride) ? endpoint.Scope : scopeOverride;
            string authorizeUrl = BuildAuthorizeUrl(endpoint, redirectUri, challenge, scope, state);

            phase = SignInPhase.Browser;
            openBrowser(authorizeUrl);

            phase = SignInPhase.Callback;
            callback = await WaitForCodeAsync(listener, state, callbackTimeout, ct);

            // CodeQL cs/user-controlled-bypass flags the branch below; it is reviewed and not a bypass.
            // The callback is already authenticated by WaitForCodeAsync: it must carry this attempt's
            // 32-byte random state, which exists only in the URL just opened, and exactly one of code
            // or error. A request that sets error can only turn this attempt into a failed sign-in;
            // it cannot reach the token exchange, which also needs the PKCE verifier that never leaves
            // this process, and the provider validates the code itself.
            string? error = callback.Request.QueryString["error"];
            if (error != null)
            {
                result = new SignInResult(null, DescribeAuthorizationError(error));
            }
            else
            {
                phase = SignInPhase.Exchange;
                result = await ExchangeCodeAsync(
                    endpoint, callback.Request.QueryString["code"]!, verifier, redirectUri, scope, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new SignInResult(null, UiText.Get("cloud.signIn.cancelled"));
        }
        catch (TimeoutException)
        {
            result = new SignInResult(null, UiText.Get("cloud.signIn.browserTimeout"));
        }
        catch (OperationCanceledException)
        {
            result = new SignInResult(null, DescribePhaseFailure(PhaseFailure.TimedOut, phase));
        }
        catch (HttpRequestException)
        {
            result = new SignInResult(null, DescribePhaseFailure(PhaseFailure.Unreachable, phase));
        }
        catch (JsonException)
        {
            result = new SignInResult(null, UiText.Get("cloud.signIn.unreadableToken"));
        }
        catch (Exception ex)
        {
            // Exception messages can include request URLs or provider content. Only the phase
            // and exception type are safe diagnostics for this credential-bearing flow.
            Logger.Warn($"OAuth sign-in failed while {DescribePhaseForLog(phase)} ({ex.GetType().Name})");
            result = new SignInResult(null, DescribePhaseFailure(PhaseFailure.Failed, phase));
        }
        finally
        {
            if (callback != null)
            {
                // Token exchange may still fail after the provider redirects. Persistence is
                // owned by the caller, so even a valid exchange asks the user to confirm in-app.
                await RespondAsync(callback, result.Tokens != null
                    ? UiText.Get("cloud.browserPage.authorized")
                    : UiText.Get("cloud.browserPage.notCompleted"));
            }

            try { listener.Stop(); } catch { }
        }

        if (result.Tokens == null)
        {
            Logger.Warn($"OAuth sign-in: {result.Message}");
        }

        return result;
    }

    /// <summary>
    /// Return a usable access token, refreshing it first if needed. Returns null when the refresh
    /// token is no longer accepted, which means the user must sign in again.
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync(
        string provider, Endpoint endpoint, CancellationToken ct = default)
    {
        var tokens = CloudTokenStore.Load(provider);
        if (tokens == null)
        {
            return null;
        }

        if (tokens.IsAccessTokenUsable)
        {
            return tokens.AccessToken;
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            return null;
        }

        var refreshed = await RefreshAsync(endpoint, tokens, ct);
        if (refreshed == null)
        {
            // A rejected refresh token is terminal, revoked, expired, or the password changed.
            // Clearing it turns every later call into a clean "signed out" rather than a retry
            // storm against an endpoint that will keep saying no.
            Logger.Warn($"{provider} refresh token rejected; signing out locally");
            CloudTokenStore.Clear(provider);
            return null;
        }

        CloudTokenStore.Save(provider, refreshed);
        return refreshed.AccessToken;
    }

    // ── Flow steps ──────────────────────────────────────────────────

    private static string BuildAuthorizeUrl(
        Endpoint endpoint, string redirectUri, string challenge, string scope, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        foreach (var (key, value) in endpoint.ExtraAuthorizeParams)
        {
            query[key] = value;
        }

        // Correlation belongs to this attempt and cannot be overridden by provider options.
        query["state"] = state;

        return endpoint.AuthorizeUrl + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>
    /// Bind a listener to an OS-assigned free port on loopback.
    /// </summary>
    /// <remarks>
    /// The port cannot be fixed: another app may already hold it, and two sign-ins could overlap.
    /// Both providers allow any port on <c>http://localhost</c> for native clients precisely
    /// because of this. A few attempts covers the race between finding a free port and binding it.
    /// </remarks>
    private static int StartOnFreePort(HttpListener listener, out string redirectUri)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = GetFreePort();

            // Keep the host used by this project's Entra registration. Google Desktop clients
            // also support localhost; changing to a numeric address needs separate registration
            // verification and is not a remedy for a failed token exchange.
            string prefix = $"http://localhost:{port}/";
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();
                redirectUri = prefix;
                return port;
            }
            catch (HttpListenerException ex)
            {
                Logger.Debug(ex, $"Loopback port {port} unavailable; retrying");
            }
        }

        redirectUri = "";
        return 0;
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Wait for one well-formed callback for this attempt, ignoring unrelated local requests.
    /// The deadline covers the entire wait and is never restarted by invalid requests.
    /// </summary>
    private static async Task<HttpListenerContext> WaitForCodeAsync(
        HttpListener listener, string state, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        // HttpListener has no cancellable GetContextAsync. Stopping it releases the pending
        // accept and observes that operation before this method disposes its cancellation state.
        using var registration = linked.Token.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var context = await listener.GetContextAsync();
                string? code = SingleQueryValue(context.Request, "code");
                string? error = SingleQueryValue(context.Request, "error");
                bool hasCode = context.Request.QueryString.GetValues("code") != null;
                bool hasError = context.Request.QueryString.GetValues("error") != null;

                if (context.Request.HttpMethod == "GET"
                    && context.Request.Url?.AbsolutePath == "/"
                    && string.Equals(SingleQueryValue(context.Request, "state"), state, StringComparison.Ordinal)
                    && ((code != null && !hasError) || (error != null && !hasCode)))
                {
                    return context;
                }

                await RespondAsync(context,
                    UiText.Get("cloud.browserPage.unrelatedRequest"),
                    HttpStatusCode.BadRequest);
            }
        }
        catch (Exception ex) when (linked.IsCancellationRequested
                                   && ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The browser callback deadline elapsed.");
        }
    }

    /// <summary>Read a single non-empty query value, rejecting duplicate or blank parameters.</summary>
    private static string? SingleQueryValue(HttpListenerRequest request, string name)
    {
        string[]? values = request.QueryString.GetValues(name);
        return values is { Length: 1 } && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
    }

    /// <summary>Send fixed browser copy without allowing a disconnected tab to fail sign-in.</summary>
    private static async Task RespondAsync(
        HttpListenerContext context, string message, HttpStatusCode status = HttpStatusCode.OK)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\">" +
                "<title>Little Launcher</title>" +
                "<body style=\"font-family:Segoe UI,system-ui,sans-serif;padding:3rem;\">" +
                $"<p>{WebUtility.HtmlEncode(message)}</p></body>");

            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = body.Length;

            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await context.Response.OutputStream.WriteAsync(body, responseTimeout.Token);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not write the OAuth callback response ({ex.GetType().Name})");
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    /// <summary>Describe only known OAuth errors; never echo provider-controlled text.</summary>
    private static string DescribeAuthorizationError(string error) => error switch
    {
        "access_denied" => UiText.Get("cloud.authError.accessDenied"),
        "admin_policy_enforced" => UiText.Get("cloud.authError.adminPolicy"),
        "org_internal" => UiText.Get("cloud.authError.orgInternal"),
        "temporarily_unavailable" => UiText.Get("cloud.authError.temporarilyUnavailable"),
        "server_error" => UiText.Get("cloud.authError.serverError"),
        _ => UiText.Get("cloud.authError.notAuthorized"),
    };

    private static async Task<SignInResult> ExchangeCodeAsync(
        Endpoint endpoint, string code, string verifier, string redirectUri, string scope,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(endpoint.ClientSecret))
        {
            form["client_secret"] = endpoint.ClientSecret;
        }

        return await PostTokenRequestAsync(endpoint, form, existingRefreshToken: null, scope, ct);
    }

    private static async Task<CloudTokens?> RefreshAsync(
        Endpoint endpoint, CloudTokens current, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = endpoint.ClientId,
            ["refresh_token"] = current.RefreshToken,
            ["grant_type"] = "refresh_token",

            // Ask for exactly what is already held, never the endpoint default: a token that was
            // widened for sharing must not be quietly narrowed back on the next refresh.
            ["scope"] = current.GrantedScope.Length > 0 ? current.GrantedScope : endpoint.Scope,
        };
        if (!string.IsNullOrEmpty(endpoint.ClientSecret))
        {
            form["client_secret"] = endpoint.ClientSecret;
        }

        var refreshed = await PostTokenRequestAsync(
            endpoint, form, current.RefreshToken, current.GrantedScope, ct);
        if (refreshed.Tokens != null)
        {
            refreshed.Tokens.AccountName = current.AccountName;
        }

        return refreshed.Tokens;
    }

    /// <param name="existingRefreshToken">
    /// Carried forward when the response omits one. Google only returns a refresh token on the
    /// first consent, so a refresh response that replaced it with empty would sign the user out
    /// an hour later.
    /// </param>
    /// <param name="requestedScope">
    /// Fallback for providers that omit <c>scope</c> from the token response, Microsoft returns
    /// it, Google does not always. Recording what was asked for is better than recording nothing,
    /// since the request only succeeds if the user consented to it.
    /// </param>
    private static async Task<SignInResult> PostTokenRequestAsync(
        Endpoint endpoint, Dictionary<string, string> form, string? existingRefreshToken,
        string requestedScope, CancellationToken ct)
    {
        using var response = await Http.PostAsync(
            endpoint.TokenUrl, new FormUrlEncodedContent(form), ct);

        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // OAuth error descriptions can echo credentials. Report only the HTTP status and
            // an allowlisted protocol error; never log the raw body, form, or description.
            string message = DescribeTokenError(response.StatusCode, body);
            Logger.Warn($"OAuth token request: {message}");
            return new SignInResult(null, message);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(access))
        {
            return new SignInResult(null, UiText.Get("cloud.signIn.noAccessToken"));
        }

        string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
        if (refresh.Length == 0 && existingRefreshToken != null)
        {
            refresh = existingRefreshToken;
        }

        int expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int secs)
            ? secs
            : 3600;

        string grantedScope = root.TryGetProperty("scope", out var sc) && sc.GetString() is { Length: > 0 } s
            ? s
            : requestedScope;

        return new SignInResult(new CloudTokens
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            GrantedScope = grantedScope,
        }, "");
    }

    /// <summary>
    /// Describe a token rejection using only fixed protocol labels. Unknown errors and malformed
    /// error bodies keep the HTTP status without reflecting arbitrary response content.
    /// </summary>
    private static string DescribeTokenError(HttpStatusCode status, string body)
    {
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                error = value.GetString();
            }
        }
        catch (JsonException) { }

        string detail = error switch
        {
            "invalid_client" => UiText.Get("cloud.tokenError.invalidClient"),
            "invalid_grant" => UiText.Get("cloud.tokenError.invalidGrant"),
            "invalid_request" => UiText.Get("cloud.tokenError.invalidRequest"),
            "unauthorized_client" => UiText.Get("cloud.tokenError.unauthorizedClient"),
            "unsupported_grant_type" => UiText.Get("cloud.tokenError.unsupportedGrantType"),
            "invalid_scope" => UiText.Get("cloud.tokenError.invalidScope"),
            "access_denied" => UiText.Get("cloud.tokenError.accessDenied"),
            "temporarily_unavailable" => UiText.Get("cloud.tokenError.temporarilyUnavailable"),
            "server_error" => UiText.Get("cloud.tokenError.serverError"),
            _ => UiText.Get("cloud.tokenError.rejected"),
        };

        return UiText.Format("cloud.tokenError.withStatus", ("detail", detail), ("status", (int)status));
    }

    // ── Sign-in phases ─────────────────────────────────────────────

    /// <summary>Which step of the browser sign-in was running when it stopped.</summary>
    private enum SignInPhase { Listener, Browser, Callback, Exchange }

    /// <summary>How a sign-in phase ended without tokens.</summary>
    private enum PhaseFailure { TimedOut, Unreachable, Failed }

    /// <summary>
    /// The user-facing message for a phase that timed out, could not reach the provider or failed
    /// otherwise. Each combination is a whole sentence so translations never assemble fragments.
    /// </summary>
    private static string DescribePhaseFailure(PhaseFailure failure, SignInPhase phase)
    {
        string key = (failure, phase) switch
        {
            (PhaseFailure.TimedOut, SignInPhase.Listener) => "cloud.signInTimedOut.listener",
            (PhaseFailure.TimedOut, SignInPhase.Browser) => "cloud.signInTimedOut.browser",
            (PhaseFailure.TimedOut, SignInPhase.Callback) => "cloud.signInTimedOut.callback",
            (PhaseFailure.TimedOut, _) => "cloud.signInTimedOut.exchange",

            (PhaseFailure.Unreachable, SignInPhase.Listener) => "cloud.signInUnreachable.listener",
            (PhaseFailure.Unreachable, SignInPhase.Browser) => "cloud.signInUnreachable.browser",
            (PhaseFailure.Unreachable, SignInPhase.Callback) => "cloud.signInUnreachable.callback",
            (PhaseFailure.Unreachable, _) => "cloud.signInUnreachable.exchange",

            (_, SignInPhase.Listener) => "cloud.signInFailed.listener",
            (_, SignInPhase.Browser) => "cloud.signInFailed.browser",
            (_, SignInPhase.Callback) => "cloud.signInFailed.callback",
            _ => "cloud.signInFailed.exchange",
        };

        return UiText.Get(key);
    }

    /// <summary>The phase in English for the diagnostic log, which is not translated.</summary>
    private static string DescribePhaseForLog(SignInPhase phase) => phase switch
    {
        SignInPhase.Listener => "opening the local callback listener",
        SignInPhase.Browser => "opening the system browser",
        SignInPhase.Callback => "waiting for the browser callback",
        _ => "exchanging the authorization code",
    };

    // ── PKCE ────────────────────────────────────────────────────────

    private static string CreateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
