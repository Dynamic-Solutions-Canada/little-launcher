using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public sealed partial class LauncherSettingsWindow
{
    /// <summary>Builds the web address editor, retaining commit-on-blur and browser bookmark selection.</summary>
    private FrameworkElement BuildWebAddressRow(Launcher launcher)
    {
        // ── Address ─────────────────────────────────────────────
        var urlBox = new TextBox
        {
            PlaceholderText = "https://homeassistant.local:8123/lovelace/cameras",
            Text = launcher.WebAddress,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Without this the box stretches to whatever height the row ends up being.
            VerticalAlignment = VerticalAlignment.Center,
        };
        _urlBox = urlBox;

        // Committed as soon as the address is finished with, rather than only on close: the icon
        // is fetched from it, and until that has happened the launcher is still wearing a globe ;
        // which is what a user who pins to the taskbar from this window would get stuck with,
        // since Windows never re-reads a pinned icon.
        urlBox.LostFocus += (_, _) => CommitWebUrl();
        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitWebUrl();
        };

        // Typing a dashboard URL from memory is the worst way to enter one; it is already a
        // bookmark in the browser the user set it up in.
        //
        // Worded exactly as the button beside the bookmark list is. They open the same picker for
        // the same reason, and two labels for one action reads as two features.
        var bookmarkButton = new Button
        {
            Content = "From browser…",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        bookmarkButton.Click += async (_, _) =>
        {
            var picked = await Pages.BookmarkPicker.PickAsync(Content.XamlRoot);
            if (picked == null) return;

            urlBox.Text = picked.Url;

            // Committed immediately rather than on close: the name is worth adopting too, and
            // doing that silently at close would overwrite a name the user had just typed.
            CommitWebUrl();
            if (_nameBox != null && string.IsNullOrWhiteSpace(_nameBox.Text))
                _nameBox.Text = picked.Name;
        };

        var urlControls = new Grid();
        urlControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(urlBox, 0);
        Grid.SetColumn(bookmarkButton, 1);
        urlControls.Children.Add(urlBox);
        urlControls.Children.Add(bookmarkButton);

        // Still the first field of the form, because it is the one a launcher cannot work without
        // and the one a new launcher is opened to fill in. It edits the first bookmark; the list
        // below shows the same entry marked as the address, so neither is a second source of truth.
        var urlRow = BuildStackedRow("Web Address", "The page this launcher opens", urlControls);

        return urlRow;
    }
}
