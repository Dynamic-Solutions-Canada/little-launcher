using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LittleLauncher.Localization;

/// <summary>
/// Application wording in the chosen interface language, with English fallback.
/// </summary>
/// <remarks>
/// <para>Catalogs are the shared JSON files under <c>Shared/Localization/&lt;locale&gt;/</c>, the
/// same ones the Linux app reads. Each assembly that uses this class embeds them under the logical
/// name <c>LittleLauncher.Localization.&lt;locale&gt;/&lt;area&gt;.json</c> (either separator);
/// every area file of a locale merges into one flat key space.</para>
/// <para>Only application-owned keys are looked up. Launcher names, URLs, error text from a server
/// and anything else the user or a provider supplied are passed as placeholder values, never as
/// keys, so they are shown verbatim.</para>
/// <para>Dependency-free and AOT-safe so the Native AOT companion can compile it too.</para>
/// </remarks>
internal static partial class UiText
{
    private const string ResourcePrefix = "LittleLauncher.Localization.";

    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new(StringComparer.Ordinal);
    private static Func<string?> _override = () => null;
    private static Func<IEnumerable<string>> _devicePreferences = () => [CultureInfo.CurrentUICulture.Name];
    private static string? _deviceLocale;

    /// <summary>Raised after the interface language changes, so open surfaces can relabel.</summary>
    internal static event Action? Changed;

    /// <summary>
    /// Connect the language choice to its owner.
    /// </summary>
    /// <param name="languageOverride">Reads the saved choice: a supported identity, or empty/null
    /// for Match device. Read on every lookup, so a replaced settings object is honoured.</param>
    /// <param name="devicePreferences">The user's ordered OS display languages. Resolved once and
    /// cached; a change to the OS list takes effect on the next start.</param>
    internal static void Configure(Func<string?> languageOverride, Func<IEnumerable<string>>? devicePreferences = null)
    {
        _override = languageOverride;
        if (devicePreferences != null)
        {
            _devicePreferences = devicePreferences;
        }

        _deviceLocale = null;
    }

    /// <summary>Tell listeners the saved choice changed. Call after persisting it.</summary>
    internal static void NotifyChanged() => Changed?.Invoke();

    /// <summary>The supported identity currently in effect.</summary>
    internal static string Locale =>
        SupportedLanguages.NormalizeOverride(_override()) ?? DeviceLocale;

    /// <summary>The identity Match device resolves to.</summary>
    internal static string DeviceLocale => _deviceLocale ??= ResolveDevice();

    /// <summary>
    /// The current culture for formatting numbers inside translated text. Regional formatting
    /// follows the OS, not the interface language, the same split Persistent makes.
    /// </summary>
    private static CultureInfo FormatCulture => CultureInfo.CurrentCulture;

    /// <summary>
    /// The text for <paramref name="key"/> in the current language, falling back to English and
    /// then to the key itself so a missing entry is visible rather than blank.
    /// </summary>
    internal static string Get(string key)
    {
        string catalog = SupportedLanguages.Catalogs[Locale];
        if (ReadCatalog(catalog).TryGetValue(key, out string? text))
        {
            return text;
        }

        if (ReadCatalog(SupportedLanguages.Default).TryGetValue(key, out text))
        {
            return text;
        }

        return key;
    }

    /// <summary>
    /// The text for <paramref name="key"/> with each <c>{{name}}</c> placeholder replaced once.
    /// </summary>
    /// <remarks>
    /// Replacement is a single pass over the template, so a value that itself contains
    /// <c>{{something}}</c> (a launcher called "{{name}}", say) is inserted literally and never
    /// expanded. An unknown placeholder is left as written.
    /// </remarks>
    internal static string Format(string key, params (string Name, object? Value)[] values)
    {
        return Fill(Get(key), values);
    }

    /// <summary>
    /// Count-dependent text: reads <c>key.one</c> or <c>key.other</c> by the current language's
    /// plural rule and supplies <c>{{count}}</c> along with any other values.
    /// </summary>
    /// <remarks>
    /// The two-form rule covers every supported language: English, Spanish and German use "one"
    /// only for 1; French and Portuguese (Brazil) also for 0; Chinese and Japanese never inflect,
    /// so their catalogs give both forms the same text.
    /// </remarks>
    internal static string Plural(string key, long count, params (string Name, object? Value)[] values)
    {
        string form = UsesOne(Locale, count) ? ".one" : ".other";
        var all = new (string Name, object? Value)[values.Length + 1];
        all[0] = ("count", count);
        values.CopyTo(all, 1);
        return Fill(Get(key + form), all);
    }

    /// <summary>The merged catalog of one locale, for completeness checks.</summary>
    internal static IReadOnlyDictionary<string, string> Catalog(string locale) => ReadCatalog(locale);

    // ── Internals ───────────────────────────────────────────────────

    private static bool UsesOne(string locale, long count)
    {
        string language = locale.Split('-')[0];
        return language switch
        {
            "fr" or "pt" => count is 0 or 1,
            "zh" or "ja" => false,
            _ => count == 1,
        };
    }

    private static string Fill(string template, (string Name, object? Value)[] values)
    {
        if (values.Length == 0)
        {
            return template;
        }

        return PlaceholderPattern().Replace(template, match =>
        {
            string name = match.Groups[1].Value;
            foreach (var (candidate, value) in values)
            {
                if (candidate == name)
                {
                    return Convert.ToString(value, FormatCulture) ?? "";
                }
            }

            return match.Value;
        });
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex PlaceholderPattern();

    private static string ResolveDevice()
    {
        IEnumerable<string> preferences;
        try
        {
            preferences = _devicePreferences().ToArray();
        }
        catch (Exception)
        {
            preferences = [CultureInfo.CurrentUICulture.Name];
        }

        return SupportedLanguages.Resolve(preferences);
    }

    /// <summary>
    /// Load and merge every embedded area file of one locale. A missing or corrupt file is
    /// skipped so the rest of the interface still has its wording; lookups fall back to English.
    /// </summary>
    private static Dictionary<string, string> ReadCatalog(string locale)
    {
        lock (Catalogs)
        {
            if (Catalogs.TryGetValue(locale, out var cached))
            {
                return cached;
            }

            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            Assembly assembly = typeof(UiText).Assembly;
            string prefix = ResourcePrefix + locale + ".";

            foreach (string name in assembly.GetManifestResourceNames())
            {
                // MSBuild's RecursiveDir keeps the folder separator in the logical name.
                string normalized = name.Replace('\\', '.').Replace('/', '.');
                if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(name);
                    if (stream == null)
                    {
                        continue;
                    }

                    var entries = JsonSerializer.Deserialize(stream, UiTextJsonContext.Default.DictionaryStringString);
                    if (entries == null)
                    {
                        continue;
                    }

                    foreach (var (key, value) in entries)
                    {
                        merged[key] = value;
                    }
                }
                catch (JsonException)
                {
                    // Keep going: one broken area must not blank every other screen.
                }
            }

            Catalogs[locale] = merged;
            return merged;
        }
    }
}

/// <summary>Source-generated reader for catalog files, so Native AOT needs no reflection.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class UiTextJsonContext : JsonSerializerContext;
