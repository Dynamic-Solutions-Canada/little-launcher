using WinUIAppKit.Updates;

namespace LittleLauncher.Classes;

/// <summary>
/// The one place the Windows app names its repository and its other public links (BRAND-5). The
/// update check and the About page build their links from these values, so a move of the
/// repository is a change here only.
/// </summary>
internal static class AboutContent
{
    /// <summary>The repository's current owner.</summary>
    public const string RepoOwner = "Dynamic-Solutions-Canada";

    /// <summary>The repository name.</summary>
    public const string RepoName = "little-launcher";

    /// <summary>The repository's web page, opened by the About page's source link.</summary>
    public static Uri SourceCode { get; } = new($"https://github.com/{RepoOwner}/{RepoName}");

    /// <summary>
    /// Where users report problems: the repository's GitHub issues, whose template reminds them what
    /// a log may contain (D-086). Permanent once shipped, since installed copies keep it.
    /// </summary>
    public static Uri Support { get; } = new($"https://github.com/{RepoOwner}/{RepoName}/issues");

    /// <summary>The product's registered privacy policy (DOCS-8). Permanent once shipped (BRAND-11).</summary>
    public static Uri PrivacyPolicy { get; } = new("https://www.dynamic-solutions.ca/privacy/little-launcher");

    /// <summary>
    /// "Check for updates" (DIST-6): the Store's updates page when packaged, this repo's GitHub
    /// Releases otherwise. Built from the one definition of the repository above.
    /// </summary>
    public static UpdateService Updates { get; } = new(RepoOwner, RepoName);
}
