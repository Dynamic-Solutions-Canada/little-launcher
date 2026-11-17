using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using LittleLauncher.Services;

// Drives the production OAuth loopback flow with real HTTP requests on localhost: a stand-in
// "browser" sends crafted callbacks to the app's listener, and a fake token endpoint records what
// the app sends. Guards the reasoning behind dismissing CodeQL alert cs/user-controlled-bypass:
// a callback must carry this attempt's state, an error can only fail the attempt, and only a
// valid code reaches the token exchange, with the PKCE verifier that matches the challenge.

int passed = 0;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
using var tokenEndpoint = new FakeTokenEndpoint();

void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new Exception("Assertion failed: " + what);
    }
}

async Task Test(string name, Func<Task> body)
{
    tokenEndpoint.Reset();
    await body();
    passed++;
    Console.WriteLine("PASS " + name);
}

OAuthPkceClient.Endpoint MakeEndpoint() => new()
{
    AuthorizeUrl = "https://authorize.invalid/oauth2/authorize",
    TokenUrl = tokenEndpoint.Url,
    ClientId = "test-client",
    Scope = "test.scope offline_access",
};

// Runs one sign-in. `browser` receives the parsed authorize request and sends callbacks.
async Task<(OAuthPkceClient.SignInResult Result, AuthorizeRequest Request)> SignIn(Func<AuthorizeRequest, Task> browser)
{
    Task? browserTask = null;
    AuthorizeRequest? request = null;
    var result = await OAuthPkceClient.SignInAsync(MakeEndpoint(), null, url =>
    {
        request = AuthorizeRequest.Parse(url);
        // The app waits for the callback after this returns, so the browser runs concurrently.
        browserTask = Task.Run(() => browser(request));
    }, TimeSpan.FromSeconds(10), CancellationToken.None);
    await browserTask!;
    return (result, request!);
}

async Task<HttpStatusCode> Callback(AuthorizeRequest request, string query, string path = "")
{
    using var response = await http.GetAsync(request.RedirectUri + path + "?" + query);
    return response.StatusCode;
}

try
{
    await Test("a callback with another attempt's state is rejected and never exchanged", async () =>
    {
        HttpStatusCode wrongState = 0;
        var (result, _) = await SignIn(async request =>
        {
            wrongState = await Callback(request, "code=stolen&state=" + Uri.EscapeDataString("not-" + request.State));
            // End the attempt the way a user cancelling at the provider would.
            await Callback(request, "error=access_denied&state=" + Uri.EscapeDataString(request.State));
        });
        Check(wrongState == HttpStatusCode.BadRequest, "wrong state answered 400");
        Check(result.Tokens == null, "no tokens");
        Check(tokenEndpoint.Requests.Count == 0, "token endpoint never called");
    });

    await Test("a callback with a missing state is rejected", async () =>
    {
        HttpStatusCode missing = 0;
        var (result, _) = await SignIn(async request =>
        {
            missing = await Callback(request, "code=abc");
            await Callback(request, "error=access_denied&state=" + Uri.EscapeDataString(request.State));
        });
        Check(missing == HttpStatusCode.BadRequest, "missing state answered 400");
        Check(result.Tokens == null && tokenEndpoint.Requests.Count == 0, "nothing exchanged");
    });

    await Test("duplicate code parameters are rejected", async () =>
    {
        HttpStatusCode duplicate = 0;
        var (result, _) = await SignIn(async request =>
        {
            string state = Uri.EscapeDataString(request.State);
            duplicate = await Callback(request, $"code=one&code=two&state={state}");
            await Callback(request, $"error=access_denied&state={state}");
        });
        Check(duplicate == HttpStatusCode.BadRequest, "duplicate code answered 400");
        Check(result.Tokens == null && tokenEndpoint.Requests.Count == 0, "nothing exchanged");
    });

    await Test("a callback carrying both code and error, or the wrong path, is rejected", async () =>
    {
        HttpStatusCode both = 0;
        HttpStatusCode wrongPath = 0;
        var (result, _) = await SignIn(async request =>
        {
            string state = Uri.EscapeDataString(request.State);
            both = await Callback(request, $"code=abc&error=access_denied&state={state}");
            wrongPath = await Callback(request, $"code=abc&state={state}", "other");
            await Callback(request, $"error=access_denied&state={state}");
        });
        Check(both == HttpStatusCode.BadRequest, "code plus error answered 400");
        Check(wrongPath == HttpStatusCode.BadRequest, "wrong path answered 400");
        Check(result.Tokens == null && tokenEndpoint.Requests.Count == 0, "nothing exchanged");
    });

    await Test("an error callback fails the attempt without contacting the token endpoint", async () =>
    {
        HttpStatusCode status = 0;
        var (result, _) = await SignIn(async request =>
        {
            status = await Callback(request, "error=access_denied&state=" + Uri.EscapeDataString(request.State));
        });
        Check(status == HttpStatusCode.OK, "the browser gets the app's page");
        Check(result.Tokens == null, "no tokens");
        Check(!string.IsNullOrWhiteSpace(result.Message), "a message explains the failure");
        Check(tokenEndpoint.Requests.Count == 0, "token endpoint never called");
    });

    await Test("a valid code is exchanged once, with the verifier matching the S256 challenge", async () =>
    {
        var (result, request) = await SignIn(async request =>
        {
            await Callback(request, "code=good-code&state=" + Uri.EscapeDataString(request.State));
        });
        Check(result.Tokens != null, "tokens returned");
        Check(result.Tokens!.AccessToken == "test-access" && result.Tokens.RefreshToken == "test-refresh", "tokens from the endpoint");
        Check(tokenEndpoint.Requests.Count == 1, "exactly one token request");

        var form = tokenEndpoint.Requests[0];
        Check(form["grant_type"] == "authorization_code", "authorization code grant");
        Check(form["code"] == "good-code", "the callback's code");
        Check(form["client_id"] == "test-client", "the client ID");
        Check(form["redirect_uri"] == request.RedirectUri, "the same redirect URI");
        Check(request.ChallengeMethod == "S256", "S256 challenge requested");
        string expected = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
        Check(expected == request.Challenge, "the verifier matches the authorize request's challenge");
        Check(request.State.Length >= 43, "a 32-byte random state");
    });

    Console.WriteLine($"{passed} OAuth loopback cases passed");
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex.Message);
    return 1;
}

return 0;

static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

/// <summary>The parts of the app's authorize URL a browser would act on.</summary>
sealed record AuthorizeRequest(string RedirectUri, string State, string Challenge, string ChallengeMethod)
{
    public static AuthorizeRequest Parse(string url)
    {
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        return new AuthorizeRequest(query["redirect_uri"]!, query["state"]!, query["code_challenge"]!, query["code_challenge_method"]!);
    }
}

/// <summary>A local token endpoint that records each form it receives and returns fixed tokens.</summary>
sealed class FakeTokenEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<Dictionary<string, string>> _requests = [];

    public FakeTokenEndpoint()
    {
        int port = FreePort();
        Url = $"http://localhost:{port}/token";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }

    public IReadOnlyList<Dictionary<string, string>> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Reset()
    {
        lock (_requests)
        {
            _requests.Clear();
        }
    }

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var parsed = HttpUtility.ParseQueryString(await reader.ReadToEndAsync());
            var form = parsed.AllKeys.Where(key => key != null).ToDictionary(key => key!, key => parsed[key]!);
            lock (_requests)
            {
                _requests.Add(form);
            }

            byte[] body = Encoding.UTF8.GetBytes("""{"access_token":"test-access","refresh_token":"test-refresh","expires_in":3600,"scope":"test.scope offline_access"}""");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
