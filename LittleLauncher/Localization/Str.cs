using Microsoft.UI.Xaml.Markup;

namespace LittleLauncher.Localization;

/// <summary>
/// XAML markup extension for translated text: <c>Text="{l:Str Key=sync.title}"</c>, with
/// <c>xmlns:l="using:LittleLauncher.Localization"</c> on the root element.
/// </summary>
/// <remarks>
/// The value is read when the element is created, in the language in effect at that moment.
/// Windows and pages opened after a language change show the new language; one already open keeps
/// its text until it is rebuilt, which is why the Settings window rebuilds itself on a change.
/// </remarks>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Str : MarkupExtension
{
    /// <summary>The catalog key, for example <c>sync.title</c>.</summary>
    public string Key { get; set; } = "";

    /// <inheritdoc/>
    protected override object ProvideValue() => UiText.Get(Key);
}
