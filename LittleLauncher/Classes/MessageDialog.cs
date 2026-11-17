using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIAppKit.Localization;

namespace LittleLauncher.Classes;

/// <summary>
/// The one-button notice ("could not import", "no browsers found"): a title, a message and OK.
/// Questions with a yes and a no go through winui-app-kit's <c>ConfirmDialog</c> instead (UI-6).
/// </summary>
internal static class MessageDialog
{
    /// <summary>Shows the notice over <paramref name="xamlRoot"/> and waits until it is dismissed.</summary>
    /// <param name="xamlRoot">The window content to show it on; nothing is shown when null.</param>
    /// <param name="title">The notice's title, in the current language.</param>
    /// <param name="message">What happened and, where it helps, what to do next.</param>
    public static async Task ShowAsync(XamlRoot? xamlRoot, string title, string message)
    {
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = UiText.Get("common.ok"),
        };
        await dialog.ShowAsync();
    }

    /// <summary>Shows an error notice titled "Error".</summary>
    public static Task ShowErrorAsync(XamlRoot? xamlRoot, string message) =>
        ShowAsync(xamlRoot, UiText.Get("common.error"), message);
}
