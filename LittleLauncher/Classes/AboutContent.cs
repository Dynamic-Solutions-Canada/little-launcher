using Microsoft.UI.Xaml;
using WinUIAppKit.Pages;
using WinUIAppKit.Updates;

namespace LittleLauncher.Classes;

/// <summary>
/// What winui-app-kit's shared About page shows for Little Launcher (UI-5, BRAND-8), and the one place
/// the Windows app names its repository and its other public links (BRAND-5). The update check and
/// the About page build their links from these values, so a move of the repository is a change here
/// only.
/// </summary>
internal static class AboutContent
{
    /// <summary>The repository's current owner.</summary>
    public const string RepoOwner = "Dynamic-Solutions-Canada";

    /// <summary>The repository name.</summary>
    public const string RepoName = "little-launcher";

    /// <summary>
    /// Where the generated notices live in the repo, relative to its root. `npm run check:notices`
    /// fails if this stops matching the notices output (`notices.about_source` in windows-build.json).
    /// </summary>
    public const string NoticesPath = "LittleLauncher/THIRD-PARTY-NOTICES.txt";

    /// <summary>The product's registered privacy policy (DOCS-8). Permanent once shipped (BRAND-11).</summary>
    public static Uri PrivacyPolicy { get; } = new("https://www.dynamic-solutions.ca/privacy/little-launcher");

    /// <summary>
    /// "Check for updates" (DIST-6): the Store's updates page when packaged, this repo's GitHub
    /// Releases otherwise. Built from the one definition of the repository above.
    /// </summary>
    public static UpdateService Updates { get; } = new(RepoOwner, RepoName);

    /// <summary>The About page's content; built each time the page opens, in the current language.</summary>
    public static AboutInfo Create()
    {
        var (source, support, license) = AboutInfo.GitHubLinks(RepoOwner, RepoName);
        return new AboutInfo
        {
            ProductName = "Little Launcher",
            DescriptionKey = "about.description",
            IconPngPath = App.IconPngPath,
            PrivacyPolicy = PrivacyPolicy,
            Updates = Updates,
            SourceCode = source,
            Support = support,
            License = license,

            // The generated notices ship in the package (HYG-3); the link opens the same file in the
            // repo, since a packaged app cannot hand a file inside its install folder to the browser.
            ThirdPartyNotices = new Uri(source + "/blob/main/" + NoticesPath),
        };
    }
}
