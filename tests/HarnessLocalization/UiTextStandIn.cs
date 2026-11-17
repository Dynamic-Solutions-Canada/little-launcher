// The stand-in sits in the kit's namespace on purpose, so production files compiled into a harness
// find it where they find the real UiText; without this, dotnet format's namespace-matches-folder
// fix (IDE0130) tries to move the file and crashes.
#pragma warning disable IDE0130
using LittleLauncher.Localization;

namespace WinUIAppKit.Localization;

/// <summary>
/// Portable stand-in for winui-app-kit's <c>UiText</c>, whose Windows language list and logging
/// cannot run in a harness on Linux. Every lookup goes through the kit's own <see cref="TextLookup"/>
/// over the real embedded catalogs, so text, fallback, placeholders and plurals are production
/// behaviour; only how the language is chosen is adapted.
/// </summary>
public static class UiText
{
    private static string _locale = AppLanguages.Supported.Default;

    private static readonly TextLookup Lookup = new(
        new TextCatalogs(typeof(UiText).Assembly, AppLanguages.ResourcePrefix),
        AppLanguages.Supported,
        () => _locale);

    /// <summary>The identity in use; English unless a test chooses another.</summary>
    public static string Locale => _locale;

    /// <summary>
    /// Read every lookup in <paramref name="identity"/> from now on, as a saved choice would.
    /// An unsupported identity falls back to the default, as the app does.
    /// </summary>
    public static void UseLanguage(string identity) =>
        _locale = AppLanguages.Supported.NormalizeOverride(identity) ?? AppLanguages.Supported.Default;

    /// <summary>See the kit's <c>UiText.Get</c>.</summary>
    public static string Get(string key) => Lookup.Get(key);

    /// <summary>See the kit's <c>UiText.Source</c>.</summary>
    public static string Source(string key) => Lookup.Source(key);

    /// <summary>See the kit's <c>UiText.Format</c>.</summary>
    public static string Format(string key, params (string Name, object? Value)[] values) => Lookup.Format(key, values);

    /// <summary>See the kit's <c>UiText.Plural</c>.</summary>
    public static string Plural(string key, long count, params (string Name, object? Value)[] values) => Lookup.Plural(key, count, values);

    /// <summary>See the kit's <c>UiText.Catalog</c>.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(string identity) => Lookup.Catalog(identity);
}
#pragma warning restore IDE0130
