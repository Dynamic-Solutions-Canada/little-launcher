using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LittleLauncher.Services;

/// <summary>
/// Opens Microsoft Store updates for packaged copies and checks GitHub Releases for portable copies.
/// </summary>
public static class UpdateService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";

    private const string Owner = "RyanEwen";
    private const string Repo = "LittleLauncher";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LittleLauncher/{GetCurrentVersion()}");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>Result of an update check.</summary>
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string? ReleaseUrl { get; init; }
        public string? ReleaseNotes { get; init; }
    }

    /// <summary>
    /// Cached result from the most recent update check (set by <see cref="CheckForUpdateAsync"/>).
    /// </summary>
    public static UpdateCheckResult? LatestResult { get; private set; }

    /// <summary>Opens the Store's Updates and downloads page for a packaged install.</summary>
    public static bool OpenStoreUpdates()
    {
        try
        {
            Process.Start(new ProcessStartInfo(StoreUpdatesUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to open Microsoft Store updates");
            return false;
        }
    }

    /// <summary>Checks GitHub releases for an unpackaged portable copy.</summary>
    public static async Task<UpdateCheckResult?> CheckForUpdateAsync()
    {
        try
        {
            var result = await CheckForGitHubUpdateAsync();
            LatestResult = result;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to check for updates");
            return null;
        }
    }

    private static async Task<UpdateCheckResult?> CheckForGitHubUpdateAsync()
    {
        var release = await Http.GetFromJsonAsync(LatestReleaseUri, GitHubJsonContext.Default.GitHubRelease);
        if (release == null || string.IsNullOrEmpty(release.TagName))
            return null;

        var current = ParseVersion(GetCurrentVersion());
        var latest = ParseVersion(release.TagName);
        if (current == null || latest == null)
            return null;

        bool updateAvailable = latest > current;

        return new UpdateCheckResult
        {
            UpdateAvailable = updateAvailable,
            CurrentVersion = $"v{current.Major}.{current.Minor}.{current.Build}",
            LatestVersion = release.TagName,
            ReleaseUrl = release.HtmlUrl,
            ReleaseNotes = release.Body,
        };
    }

    internal static string GetCurrentVersion()
    {
        var asm = typeof(UpdateService).Assembly.GetName();
        return $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";
    }

    private static Version? ParseVersion(string tag)
    {
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : null;
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}

[JsonSerializable(typeof(UpdateService.GitHubRelease))]
internal partial class GitHubJsonContext : JsonSerializerContext
{
}
