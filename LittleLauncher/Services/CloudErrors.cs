using WinUIAppKit.Localization;
using System.Net;
using System.Text.Json;

namespace LittleLauncher.Services;

/// <summary>
/// Turns a failed cloud API response into a message worth showing the user.
/// </summary>
/// <remarks>
/// <para><b>Prefer the service's own message over our guess.</b> This exists because the first
/// version did the opposite: it mapped status codes to hand-written text, so a Google 403 saying
/// <i>"Google Drive API has not been used in project N before or it is disabled. Enable it by
/// visiting &lt;url&gt;"</i> — a complete, actionable diagnosis with the fix URL in it — was
/// replaced with "the account may be out of space or rate-limited", which was simply wrong and
/// sent the reader looking in the wrong place entirely.</para>
/// <para>Both Microsoft Graph and Google's APIs return <c>{ "error": { "message": ... } }</c>, so
/// one extractor serves both. Status-code text is kept only for the few cases where the app really
/// does know better than the service — chiefly 401, where "sign in again" is more useful than
/// "Invalid Credentials".</para>
/// </remarks>
internal static class CloudErrors
{
    /// <summary>Longest service message to pass through; Google's can run to several hundred characters.</summary>
    private const int MaxServiceMessage = 400;

    /// <summary>
    /// Build the user-facing message for a failed request.
    /// </summary>
    /// <param name="provider">Display name, e.g. "Google Drive".</param>
    /// <param name="operation">What was being attempted, e.g. "upload".</param>
    /// <param name="body">The raw response body; may be empty.</param>
    public static string Describe(
        HttpStatusCode status, string body, string provider, string operation)
    {
        // Cases where the app knows the remedy better than the service's wording does.
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return UiText.Format("cloud.error.signInRejected", ("provider", provider));
            case HttpStatusCode.InsufficientStorage:
                return UiText.Format("cloud.error.outOfSpace", ("provider", provider));
            case HttpStatusCode.TooManyRequests:
                return UiText.Format("cloud.error.rateLimited", ("provider", provider));
        }

        string? serviceMessage = TryExtractMessage(body);
        if (!string.IsNullOrWhiteSpace(serviceMessage))
        {
            return UiText.Format("cloud.providerMessage", ("provider", provider), ("message", serviceMessage));
        }

        return DescribeStatus(provider, operation, status);
    }

    /// <summary>
    /// The plain "{provider} upload failed (500)." message, for a failure with nothing better to say.
    /// </summary>
    /// <param name="provider">Display name, e.g. "WebDAV".</param>
    /// <param name="operation">The English operation word the stores log with, e.g. "upload".
    /// Each known word has its own whole-sentence message so translations need not compose
    /// fragments; an unknown word falls back to a generic "request failed".</param>
    /// <param name="status">The HTTP status the server returned.</param>
    public static string DescribeStatus(string provider, string operation, HttpStatusCode status)
    {
        string key = operation switch
        {
            "download" => "cloud.failed.download",
            "upload" => "cloud.failed.upload",
            "lookup" => "cloud.failed.lookup",
            "create" => "cloud.failed.create",
            "metadata" => "cloud.failed.metadata",
            "publish" => "cloud.failed.publish",
            "create a share link" => "cloud.failed.shareLink",
            "read" => "cloud.failed.read",
            "check" => "cloud.failed.check",
            _ => "cloud.failed.request",
        };

        return UiText.Format(key, ("provider", provider), ("status", (int)status));
    }

    /// <summary>
    /// Pull <c>error.message</c> out of a Graph or Google error body.
    /// </summary>
    /// <remarks>
    /// Graph's <c>error.message</c> is a string. Google's is too, but its <c>error</c> object also
    /// carries an <c>errors[]</c> array whose first entry is sometimes more specific, so that is
    /// preferred when present. Anything unparseable yields null and the caller falls back.
    /// </remarks>
    private static string? TryExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            // Google sometimes nests a more specific reason here.
            if (error.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("message", out var first)
                && first.GetString() is { Length: > 0 } firstMessage)
            {
                return Trim(firstMessage);
            }

            if (error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
            {
                return Trim(text);
            }
        }
        catch (JsonException)
        {
            // An HTML error page or a proxy's response — nothing to extract, use the fallback.
        }

        return null;
    }

    private static string Trim(string message)
    {
        message = message.Trim();
        return message.Length <= MaxServiceMessage
            ? message
            : message[..MaxServiceMessage].TrimEnd() + "...";
    }
}
