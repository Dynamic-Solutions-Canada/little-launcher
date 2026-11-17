using WinUIAppKit.Localization;

namespace LittleLauncher.Localization;

/// <summary>
/// The interface languages Little Launcher ships, in picker order, for winui-app-kit's
/// <c>SupportedLanguages</c>. One definition for the app, the Native AOT companion and the test
/// harnesses; the Linux client keeps the same table in <c>i18n.py</c>, and
/// <c>linux/tests/test_localization.py</c> checks the two match.
/// </summary>
/// <remarks>
/// <para>Each row is the identity a user can choose, the catalog folder under
/// <c>Shared/Localization/</c> it reads, and its name in its own language, which is never
/// translated so it can be found from any interface language. The Canadian identities deliberately
/// reuse the en-US and fr-FR wording while staying separate choices.</para>
/// <para>Catalog area files are embedded as <see cref="ResourcePrefix"/><c>&lt;catalog&gt;/&lt;area&gt;.json</c>
/// and merged per catalog at first read.</para>
/// </remarks>
internal static class AppLanguages
{
    /// <summary>The manifest resource name prefix the catalogs are embedded under.</summary>
    internal const string ResourcePrefix = "LittleLauncher.Localization.";

    /// <summary>Every supported language; the default, en-US, is also the per-key fallback.</summary>
    internal static SupportedLanguages Supported { get; } = new("en-US",
    [
        ("en-US", "en-US", "English (United States)"),
        ("en-CA", "en-US", "English (Canada)"),
        ("es-ES", "es-ES", "Español (España)"),
        ("fr-FR", "fr-FR", "Français (France)"),
        ("fr-CA", "fr-FR", "Français (Canada)"),
        ("de-DE", "de-DE", "Deutsch (Deutschland)"),
        ("zh-CN", "zh-CN", "简体中文（中国）"),
        ("pt-BR", "pt-BR", "Português (Brasil)"),
        ("ja-JP", "ja-JP", "日本語（日本）"),
    ]);
}
