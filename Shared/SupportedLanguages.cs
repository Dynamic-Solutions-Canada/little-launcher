namespace LittleLauncher.Localization;

/// <summary>UI locale and catalog identities, independent of regional formatting and saved data.</summary>
internal static class SupportedLanguages
{
    internal const string Default = "en-US";

    // Canadian locales deliberately reuse wording. Keep their identities for language selection.
    internal static IReadOnlyDictionary<string, string> Catalogs { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en-US"] = "en-US",
                ["en-CA"] = "en-US",
                ["es-ES"] = "es-ES",
                ["fr-FR"] = "fr-FR",
                ["fr-CA"] = "fr-FR",
                ["de-DE"] = "de-DE",
                ["zh-CN"] = "zh-CN",
                ["pt-BR"] = "pt-BR",
                ["ja-JP"] = "ja-JP",
            });

    /// <summary>Supported identities in picker order.</summary>
    internal static IReadOnlyList<string> Ordered { get; } =
        ["en-US", "en-CA", "es-ES", "fr-FR", "fr-CA", "de-DE", "zh-CN", "pt-BR", "ja-JP"];

    /// <summary>
    /// Each identity's name in its own language, so someone who cannot read the current
    /// interface language can still find theirs. Never translated.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> NativeNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = "English (United States)",
            ["en-CA"] = "English (Canada)",
            ["es-ES"] = "Español (España)",
            ["fr-FR"] = "Français (France)",
            ["fr-CA"] = "Français (Canada)",
            ["de-DE"] = "Deutsch (Deutschland)",
            ["zh-CN"] = "简体中文（中国）",
            ["pt-BR"] = "Português (Brasil)",
            ["ja-JP"] = "日本語（日本）",
        };

    /// <summary>Returns a canonical explicit locale, or null for automatic/unknown preferences.</summary>
    internal static string? NormalizeOverride(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        string tag = language.Trim();
        return Catalogs.Keys.FirstOrDefault(supported =>
            supported.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Chooses the first supported Windows preference, including regional relatives.
    /// Unsupported scripts remain unsupported: Traditional Chinese never selects Simplified Chinese.
    /// </summary>
    internal static string Resolve(IEnumerable<string> preferences)
    {
        foreach (string preference in preferences)
        {
            if (string.IsNullOrWhiteSpace(preference))
                continue;

            // Unicode calendar/numeral extensions belong to regional formatting, not wording identity.
            string[] parts = preference.Trim().Split('-');
            int extension = Array.FindIndex(parts, 1, part => part.Length == 1);
            string baseTag = extension < 0 ? preference.Trim() : string.Join("-", parts.Take(extension));
            string tag = NormalizeDefaultScript(baseTag);
            foreach (string supported in Catalogs.Keys)
            {
                if (string.Equals(tag, supported, StringComparison.OrdinalIgnoreCase))
                    return supported;
            }

            string family = LanguageFamily(tag);
            foreach (string supported in Catalogs.Keys)
            {
                if (family.Length > 0 && string.Equals(family, LanguageFamily(supported), StringComparison.OrdinalIgnoreCase))
                    return supported;
            }
        }

        return Default;
    }

    /// <summary>Removes an explicit default Latin script before matching a supported regional locale.</summary>
    private static string NormalizeDefaultScript(string tag)
    {
        string[] parts = tag.Split('-');
        string language = parts[0].ToLowerInvariant();
        if (parts.Length > 1 && parts[1].Equals("Latn", StringComparison.OrdinalIgnoreCase)
            && language is "en" or "es" or "fr" or "de" or "pt")
        {
            return string.Join("-", parts.Where((_, index) => index != 1));
        }

        return tag;
    }

    /// <summary>Groups regional wording without crossing an explicit Chinese script boundary.</summary>
    private static string LanguageFamily(string tag)
    {
        string[] parts = tag.Split('-');
        if (parts.Any(part => part.Length is < 1 or > 8 || !part.All(char.IsAsciiLetterOrDigit)))
            return "";

        string language = parts[0].ToLowerInvariant();
        string? script = parts.Skip(1).FirstOrDefault(part => part.Length == 4 && part.All(char.IsAsciiLetter));
        if (language == "zh")
        {
            // An explicit script is authoritative, even when the regional default differs.
            if (script is not null)
                return "zh-" + script;

            if (parts.Any(part => part.Equals("TW", StringComparison.OrdinalIgnoreCase)
                || part.Equals("HK", StringComparison.OrdinalIgnoreCase)
                || part.Equals("MO", StringComparison.OrdinalIgnoreCase)))
            {
                return "zh-Hant";
            }

            return "zh-Hans";
        }

        if (script?.Equals("Latn", StringComparison.OrdinalIgnoreCase) == true
            && language is "en" or "es" or "fr" or "de" or "pt")
        {
            script = null;
        }

        return script is null ? language : language + "-" + script;
    }
}
