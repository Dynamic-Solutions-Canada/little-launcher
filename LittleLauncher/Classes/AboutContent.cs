namespace LittleLauncher.Classes;

/// <summary>
/// The one place the Windows app names its repository (BRAND-5). The update check and the About
/// page build their links from these values, so a move of the repository is a change here only.
/// </summary>
internal static class AboutContent
{
    /// <summary>The repository's current owner.</summary>
    public const string RepoOwner = "Dynamic-Solutions-Canada";

    /// <summary>The repository name.</summary>
    public const string RepoName = "little-launcher";

    /// <summary>The repository's web page, opened by the About page's source link.</summary>
    public static Uri SourceCode { get; } = new($"https://github.com/{RepoOwner}/{RepoName}");
}
