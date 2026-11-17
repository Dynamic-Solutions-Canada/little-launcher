using LittleLauncher.Localization;
using WinUIAppKit.Localization;
using static LauncherShortcut.NativeMethods;

namespace LauncherShortcut;

/// <summary>
/// Connects the companion's wording to the language the user chose in the main app.
/// </summary>
/// <remarks>
/// <para>The main app writes the identity in effect (for example <c>fr-CA</c>) to
/// <c>language.txt</c> beside this exe, the same folder it writes <c>main-exe-path.txt</c> to, so
/// this process never has to locate the app's settings inside an MSIX package. It is read once, on
/// the first lookup. A missing, unreadable or unrecognised value means Match device.</para>
/// <para>The OS language list comes from <c>GetUserPreferredUILanguages</c>, not
/// <c>CultureInfo.CurrentUICulture</c>: the companion builds with invariant globalization, so its
/// culture is always the invariant one and would always resolve to English.</para>
/// </remarks>
internal static class CompanionLanguage
{
    /// <summary>The language the main app reported, read once on first use.</summary>
    private static readonly Lazy<string?> Saved = new(ReadAppLanguage);

    /// <summary>Windows' display languages, read once on first use.</summary>
    private static readonly Lazy<string> Device = new(() => AppLanguages.Supported.Resolve(ReadPreferredUiLanguages()));

    /// <summary>
    /// The companion's interface text: winui-app-kit's dependency-free <see cref="TextLookup"/>
    /// over the catalogs embedded in this exe. Nothing is read until the first lookup.
    /// </summary>
    internal static TextLookup Text { get; } = new(
        new TextCatalogs(typeof(CompanionLanguage).Assembly, AppLanguages.ResourcePrefix,
            (message, ex) => TaskbarPinning.Log($"language: {message}; {ex.Message}")),
        AppLanguages.Supported,
        CurrentLocale);

    /// <summary>The identity in use: the app's choice when it names a supported language, else Windows'.</summary>
    private static string CurrentLocale() =>
        AppLanguages.Supported.NormalizeOverride(Saved.Value) ?? Device.Value;

    /// <summary>
    /// The language the main app last reported, or null when it has not written one yet.
    /// </summary>
    private static string? ReadAppLanguage()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "language.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            TaskbarPinning.Log($"language: breadcrumb unreadable, using device language; {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The user's ordered Windows display languages, such as <c>["fr-CA", "en-US"]</c>, or an
    /// empty list (English) when Windows cannot report them.
    /// </summary>
    private static unsafe IEnumerable<string> ReadPreferredUiLanguages()
    {
        uint length = 0;
        if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref length) || length == 0)
        {
            return [];
        }

        char[] buffer = new char[length];
        fixed (char* pointer = buffer)
        {
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, pointer, ref length))
            {
                return [];
            }
        }

        // Tags are separated by single nulls and the list ends with two.
        int used = (int)Math.Min(length, (uint)buffer.Length);
        return new string(buffer, 0, used).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
