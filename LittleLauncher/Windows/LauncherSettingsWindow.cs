using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;
using Image = Microsoft.UI.Xaml.Controls.Image;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// Per-launcher settings (name, tray icon, view mode, density, title, taskbar pin).
/// </summary>
/// <remarks>
/// <para>A standalone window rather than a <c>ContentDialog</c>, for the same reason as
/// <see cref="ItemEditorWindow"/>: it is opened from the flyout, and a dialog cannot
/// overflow the flyout's HWND. The Launchers page opens this same window, so there is one
/// implementation rather than a dialog and a window drifting apart.</para>
/// <para>Settings apply immediately as they are changed; the name is committed on close.</para>
/// </remarks>
public sealed partial class LauncherSettingsWindow : Window
{
    private const int WindowWidthDips = 560;

    /// <summary>Fallback height, used only if the form cannot be measured.</summary>
    private const int FallbackHeightDips = 560;

    /// <summary>Chrome around the form: title bar, body padding and the button row.</summary>
    private const int ChromeHeightDips = 130;

    /// <summary>
    /// How many of these windows are open. The sync service reads it: a download while a launcher
    /// is being configured erases whatever the server has not seen yet, which for a launcher
    /// being created is all of it.
    /// </summary>
    internal static int OpenCount { get; private set; }

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;
    private readonly Launcher _launcher;
    private TextBox? _nameBox;
    private TextBox? _urlBox;
    private Button? _doneButton;
    private bool _isNewLauncher;

    /// <summary>Reports the final setup window bounds before its HWND is destroyed.</summary>
    private readonly Action<RectInt32>? _onClosing;

    /// <summary>Only one pin request may own this launcher snapshot at a time.</summary>
    private bool _pinning;

    /// <summary>Re-reads the launcher into the icon button, so an icon that arrives is shown.</summary>
    private Action? _refreshIconPreview;

    /// <summary>
    /// The site-icon fetch started by the last address change, if it has not finished.
    /// Pinning waits on it — see <see cref="PinToTaskbar_Click"/>.
    /// </summary>
    private Task _iconAdoption = Task.CompletedTask;

    /// <summary>How long pinning will wait for a site icon before going ahead without it.</summary>
    private static readonly TimeSpan IconAdoptionWait = TimeSpan.FromSeconds(8);


    /// <summary>
    /// Opens launcher settings. Completes when the window closes.
    /// <paramref name="ownerHwnd"/> keeps it above an always-on-top flyout.
    /// </summary>
    /// <param name="isNewLauncher">
    /// Changes the accept button from "Done" to wording that signals a further step, since a
    /// launcher created this way goes straight on to item editing.
    /// </param>
    /// <param name="onClosing">
    /// Receives the actual window bounds in physical screen pixels before closing, for a
    /// following setup step. Closing still applies changes and completes with true as before.
    /// </param>
    /// <param name="section">Optional section to expand and reveal after the form has loaded.</param>
    public static Task<bool> ShowAsync(
        Launcher launcher,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null,
        bool isNewLauncher = false,
        Action<RectInt32>? onClosing = null,
        LauncherSettingsSection? section = null)
    {
        var window = new LauncherSettingsWindow(launcher, ownerHwnd, isNewLauncher, onClosing);
        window._requestedSection = section;
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private LauncherSettingsWindow(
        Launcher launcher,
        IntPtr ownerHwnd,
        bool isNewLauncher,
        Action<RectInt32>? onClosing)
    {
        _launcher = launcher;
        _onClosing = onClosing;
        OpenCount++;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Launcher Settings";
        SystemBackdrop = new MicaBackdrop();

        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var titleBar = WindowChrome.BuildTitleBar(Title);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var doneButton = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        doneButton.Click += (_, _) => CloseWithBounds();

        _isNewLauncher = isNewLauncher;
        _doneButton = doneButton;
        UpdateAcceptButton();

        var body = new Grid { Padding = new Thickness(24, 8, 24, 24) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var form = BuildForm(launcher);
        var scroller = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(doneButton, 1);
        body.Children.Add(scroller);
        body.Children.Add(doneButton);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        Content = root;
        ThemeManager.ApplySavedTheme(this);
        WindowChrome.ApplyIcon(_hwnd);

        SizeAndCentre();

        // Real sizing happens once layout has produced an ActualHeight.
        form.Loaded += (_, _) =>
        {
            ResizeToContent(form);
            _formLoaded = true;
            if (_requestedSection is { } section)
                ShowSection(section);

            // A brand-new web launcher needs exactly one thing: an address. Put the caret there
            // rather than making the user find the field the window was opened for.
            if (isNewLauncher && launcher.IsWebLauncher)
                _urlBox?.Focus(FocusState.Programmatic);
        };

        // Hidden: WinUI otherwise pops an "Esc" accelerator tooltip over the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; CloseWithBounds(); };
        root.KeyboardAccelerators.Add(escape);

        // Closing covers the system close button and Alt+F4. Programmatic Close does not
        // raise that event, so Done and Escape report their bounds through CloseWithBounds.
        AppWindow.Closing += (_, _) => ReportClosingBounds();

        InitializePinStatus();
        Closed += (_, _) =>
        {
            _settingsClosed = true;
            DisposePinStatus();
            OpenCount = Math.Max(0, OpenCount - 1);
            CommitName();
            CommitWebUrl();

            // Whatever changed in here is a launcher change, so the next periodic sync must
            // upload rather than download over it.
            Services.AutoSyncService.NotifyLaunchersChanged();
            _completion.TrySetResult(true);
        };
    }

    /// <summary>Closes from an in-window action after capturing the window's actual position.</summary>
    private void CloseWithBounds()
    {
        ReportClosingBounds();
        Close();
    }

    /// <summary>Reports physical screen bounds while the native settings window still exists.</summary>
    private void ReportClosingBounds()
    {
        if (_onClosing == null || !NativeMethods.GetWindowRect(_hwnd, out var rect)) return;

        _onClosing(new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    /// <summary>
    /// Labels the accept button for what actually happens next.
    /// </summary>
    /// <remarks>
    /// A new shortcut launcher goes straight on to item editing, so "Done" would be a lie — the
    /// user has one more step. A web launcher has no items and nothing follows, so the same
    /// wording is the opposite lie: it promises a step that does not exist, and
    /// <c>LaunchersPage</c> deliberately does not open edit mode for it. Re-run whenever the type
    /// changes, since that decides which of the two this is.
    /// </remarks>
    private void UpdateAcceptButton()
    {
        if (_doneButton == null) return;
        _doneButton.Content = _isNewLauncher && !_launcher.IsWebLauncher ? "Next: Add Items" : "Done";
    }

    /// <summary>The two text fields are the deferred edits; everything else applies as it changes.</summary>
    private void CommitName()
    {
        if (_nameBox == null) return;
        string name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == _launcher.Name) return;

        _launcher.Name = name;
        SettingsManager.SaveSettings();
        Services.AutoSyncService.NotifyLaunchersChanged();
        MainWindow.Current?.RefreshTrayIcons();
        FlyoutWindow.InvalidateItems(_launcher.Id);
        WebFlyoutWindow.ApplyLauncherChanges(_launcher.Id);
    }

    /// <summary>
    /// Commits the web address and, unless the user has chosen an icon, adopts the site's icon —
    /// a composite is built from item icons, so a web launcher would otherwise sit in the tray as
    /// the generic app icon.
    /// </summary>
    /// <remarks>
    /// <para>This is the *provisional* icon: an unauthenticated favicon fetch, which gives the
    /// launcher something immediately but comes back empty-handed for a page behind a login. The
    /// real one is whatever the page declares once it loads in the flyout
    /// (<see cref="WebFlyoutWindow.AdoptPageIconAsync"/>). Both write the same managed path, so
    /// the page icon can replace this one — and neither touches an icon the user picked.</para>
    /// <para>Called as soon as the address is entered — on Enter or when the field is left — and
    /// not only when the window closes. The icon is derived from the address, so anything the user
    /// might do next in this window (look at the icon, pin to the taskbar) is wrong until the
    /// fetch has run.</para>
    /// </remarks>
    private void CommitWebUrl()
    {
        if (_urlBox == null) return;
        CommitWebAddress(_urlBox.Text.Trim());
    }

    /// <summary>
    /// Points the launcher at the page it opens.
    /// </summary>
    /// <remarks>
    /// <para>A setting of the launcher's own, not a position in the bar. Editing it never rewrites
    /// a bookmark: the two are independent, and a bookmark holding the same URL is an ordinary
    /// bookmark that happens to agree with it.</para>
    /// <para>The exception is a launcher that has no bookmarks at all, which is one being set up.
    /// It gets a bookmark for the address as well, so a new launcher arrives with something in the
    /// bar rather than an empty one.</para>
    /// </remarks>
    private void CommitWebAddress(string url)
    {
        url = (url ?? "").Trim();
        if (url == _launcher.WebAddress) return;

        // Only ever for a launcher being set up: no bookmarks *and* no home page yet. The bar is
        // the user's after that, and a launcher whose last bookmark was just deleted must not have
        // one put back the moment this field is left - the address box commits on lost focus, so
        // clicking anywhere after the deletion would have re-created it, which reads as a bookmark
        // that refuses to be removed.
        WebBookmark? seeded = null;
        if (_launcher.WebBookmarks.Count == 0 && string.IsNullOrWhiteSpace(_launcher.WebHomeUrl))
        {
            if (string.IsNullOrEmpty(url)) return;

            seeded = new WebBookmark(HostOf(url), url);
            _launcher.WebBookmarks.Add(seeded);
        }

        _launcher.WebHomeUrl = url;

        SettingsManager.SaveSettings();
        WebFlyoutWindow.ApplyLauncherChanges(_launcher.Id);

        if (string.IsNullOrEmpty(url)) return;

        // Only the bookmark this just made. The others are the user's and already have their icons;
        // re-fetching one because the *address* changed would be fetching for the wrong reason.
        if (seeded != null)
            _ = WebFlyoutWindow.FetchBookmarkIconAsync(_launcher, seeded);

        if (WebFlyoutWindow.MayAdoptPageIcon(_launcher))
            _iconAdoption = AdoptAndShowSiteIconAsync(_launcher, WebFlyoutWindow.NormalizeUrl(url));
    }

    /// <summary>Puts the launcher's address back in the address field.</summary>
    private void SyncAddressBox()
    {
        if (_urlBox != null) _urlBox.Text = _launcher.WebAddress;
    }

    /// <summary>Fetches the site icon and shows it in this window's icon row once it lands.</summary>
    private async Task AdoptAndShowSiteIconAsync(Launcher launcher, string url)
    {
        await AdoptSiteIconAsync(launcher, url);

        // The window may have been closed while the fetch was in flight.
        DispatcherQueue?.TryEnqueue(() => _refreshIconPreview?.Invoke());
    }

    private static async Task AdoptSiteIconAsync(Launcher launcher, string url)
    {
        try
        {
            string? iconPath = await Services.FaviconService.FetchAndCacheAsync(url);
            if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return;

            // Copied out of the favicon cache (which is pruned on the item-icon schedule) to the
            // managed page-icon path — the distinct name is what lets the flyout tell an icon we
            // adopted from one the user chose, and so upgrade it later.
            string destPath = WebFlyoutWindow.GetPageIconPath(launcher.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(iconPath, destPath, overwrite: true);

            launcher.CustomTrayIconPath = destPath;
            launcher.TrayIconMode = TrayIconModes.Custom;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            MainWindow.Current?.UpdateTrayIcon(launcher);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not adopt the site icon for {Name}", launcher.Name);
        }
    }

    /// <summary>
    /// Resizes to the form's real height once layout has run.
    /// </summary>
    /// <remarks>
    /// Must happen on <c>Loaded</c>, not during construction. Before the window is shown the
    /// form has never been laid out, so its <c>DesiredSize</c> is meaningless — feeding that
    /// into the work-area clamp produced a full-height window. <c>ActualHeight</c> after layout
    /// is the real number. The form's height varies (the "Icons Per Row" row only exists in
    /// icon mode), which is why a single constant does not do.
    /// </remarks>
    private void ResizeToContent(FrameworkElement form)
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double contentHeight = form.ActualHeight > 0 ? form.ActualHeight : form.DesiredSize.Height;
        if (contentHeight <= 0) return;   // keep the fallback size

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        double heightDips = contentHeight + ChromeHeightDips;

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (area != null)
            heightDips = Math.Min(heightDips, (area.WorkArea.Height / scale) - 40);

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(heightDips * scale);
        appWindow.Resize(new SizeInt32(width, height));

        if (area != null)
        {
            appWindow.Move(new PointInt32(
                area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
        }
    }

    /// <summary>Initial size, replaced by <see cref="ResizeToContent"/> once laid out.</summary>
    private void SizeAndCentre()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(FallbackHeightDips * scale);
        appWindow.Resize(new SizeInt32(width, height));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (area != null)
        {
            appWindow.Move(new PointInt32(
                area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private FrameworkElement BuildForm(Launcher launcher)
    {
        // ── Name row ────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            PlaceholderText = "Launcher name",
            Text = launcher.Name,
            MinWidth = 160,
            MaxWidth = 280,
        };
        _nameBox = nameBox;

        // Committed as soon as the field is finished with, like the address below it. Both used to
        // wait for the window to close, which meant every other button in the window — Pin to
        // Taskbar above all, since a pinned name and icon are baked once and never re-read — acted
        // on the name the user had just replaced.
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitName();
        };

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameLabel.Children.Add(new TextBlock { Text = "Name", FontSize = 14 });
        nameLabel.Children.Add(new TextBlock { Text = "Display name in tray icon tooltip", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(nameLabel, 0);
        Grid.SetColumn(nameBox, 1);
        nameRow.Children.Add(nameLabel);
        nameRow.Children.Add(nameBox);

        // ── Icon chooser ─────────────────────────────────────────
        var (iconButton, customIconRow, refreshIconPreview) = BuildIconChooser(launcher);
        _refreshIconPreview = refreshIconPreview;

        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconLabel.Children.Add(new TextBlock { Text = "Icon", FontSize = 14 });
        // Text set by UpdateKindVisibility — a web launcher's icon arrives on its own.
        var iconSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };
        iconLabel.Children.Add(iconSubtitle);
        Grid.SetColumn(iconLabel, 0);
        Grid.SetColumn(iconButton, 1);
        iconRow.Children.Add(iconLabel);
        iconRow.Children.Add(iconButton);

        // ── View mode combo ──────────────────────────────────────
        var viewModeCombo = new ComboBox { MinWidth = 160 };
        var viewModes = new[]
        {
            (Label: "Icons", Value: LauncherViewModes.Icon),
            (Label: "Small Icons", Value: LauncherViewModes.SmallIcon),
            (Label: "List", Value: LauncherViewModes.List),
        };

        foreach (var viewMode in viewModes)
        {
            viewModeCombo.Items.Add(new ComboBoxItem
            {
                Content = viewMode.Label,
                Tag = viewMode.Value,
            });
        }

        int selectedViewMode = LauncherViewModes.Normalize(launcher.ViewMode);
        viewModeCombo.SelectedIndex = Array.FindIndex(viewModes, vm => vm.Value == selectedViewMode);
        if (viewModeCombo.SelectedIndex < 0)
            viewModeCombo.SelectedIndex = 0;

        var viewModeRow = new Grid();
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        viewModeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var viewModeLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        viewModeLabel.Children.Add(new TextBlock { Text = "View Mode", FontSize = 14 });
        viewModeLabel.Children.Add(new TextBlock { Text = "How items appear in the flyout popup", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(viewModeLabel, 0);
        Grid.SetColumn(viewModeCombo, 1);
        viewModeRow.Children.Add(viewModeLabel);
        viewModeRow.Children.Add(viewModeCombo);

        // ── Icon mode density ───────────────────────────────────
        var iconsPerRowCombo = new ComboBox { MinWidth = 100 };
        for (int iconsPerRow = Launcher.MinIconModeIconsPerRow; iconsPerRow <= Launcher.MaxIconModeIconsPerRow; iconsPerRow++)
            iconsPerRowCombo.Items.Add(iconsPerRow.ToString());
        iconsPerRowCombo.SelectedItem = Launcher.ClampIconModeIconsPerRow(launcher.IconModeIconsPerRow).ToString();
        iconsPerRowCombo.SelectionChanged += (s, e) =>
        {
            if (iconsPerRowCombo.SelectedItem is string selected && int.TryParse(selected, out int iconsPerRow))
            {
                launcher.IconModeIconsPerRow = Launcher.ClampIconModeIconsPerRow(iconsPerRow);
                SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
                FlyoutWindow.InvalidateItems(launcher.Id);
            }
        };

        var iconsPerRowRow = new Grid();
        iconsPerRowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconsPerRowRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconsPerRowLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        iconsPerRowLabel.Children.Add(new TextBlock { Text = "Icons Per Row", FontSize = 14 });
        iconsPerRowLabel.Children.Add(new TextBlock { Text = "How many icons fit across each icon-mode column", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(iconsPerRowLabel, 0);
        Grid.SetColumn(iconsPerRowCombo, 1);
        iconsPerRowRow.Children.Add(iconsPerRowLabel);
        iconsPerRowRow.Children.Add(iconsPerRowCombo);

        void UpdateIconModeControls()
        {
            int selectedMode = viewModeCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is int value
                ? value
                : LauncherViewModes.Icon;
            iconsPerRowRow.Visibility = LauncherViewModes.IsIconMode(selectedMode) ? Visibility.Visible : Visibility.Collapsed;
        }

        viewModeCombo.SelectionChanged += (s, e) =>
        {
            if (viewModeCombo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not int viewMode)
                return;

            launcher.ViewMode = viewMode;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            FlyoutWindow.InvalidateItems(launcher.Id);
            UpdateIconModeControls();
        };

        UpdateIconModeControls();

        // ── Show title toggle ────────────────────────────────────
        var showTitleToggle = new ToggleSwitch
        {
            IsOn = launcher.ShowTitle,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        showTitleToggle.Toggled += (s, e) =>
        {
            launcher.ShowTitle = showTitleToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            FlyoutWindow.InvalidateItems(launcher.Id);
        };

        var showTitleRow = new Grid();
        showTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        showTitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var showTitleLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        showTitleLabel.Children.Add(new TextBlock { Text = "Show Title", FontSize = 14 });
        showTitleLabel.Children.Add(new TextBlock { Text = "Show the launcher name at the top of the flyout", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(showTitleLabel, 0);
        Grid.SetColumn(showTitleToggle, 1);
        showTitleRow.Children.Add(showTitleLabel);
        showTitleRow.Children.Add(showTitleToggle);

        // ── Show in tray toggle ──────────────────────────────────
        var showToggle = new ToggleSwitch
        {
            IsOn = !launcher.NIconHide,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        showToggle.Toggled += (s, e) =>
        {
            launcher.NIconHide = !showToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };

        var hideRow = new Grid();
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hideRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hideLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hideLabel.Children.Add(new TextBlock { Text = "Show in Tray", FontSize = 14 });
        hideLabel.Children.Add(new TextBlock { Text = "Show this launcher's icon in the system tray", FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(hideLabel, 0);
        Grid.SetColumn(showToggle, 1);
        hideRow.Children.Add(hideLabel);
        hideRow.Children.Add(showToggle);

        // ── Pin to taskbar row ──────────────────────────────────
        var pinBtn = new Button
        {
            Content = (string)Application.Current.Resources["TaskbarPinAction"],
            Tag = launcher,
        };
        _pinButton = pinBtn;
        pinBtn.Click += PinToTaskbar_Click;

        _pinStatus = new TextBlock
        {
            Text = (string)Application.Current.Resources["TaskbarPinDescription"],
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var taskbarRow = BuildRow("Show in Taskbar", _pinStatus, pinBtn);

        // ── Web launcher rows ───────────────────────────────────
        var webAddressRow = BuildWebAddressRow(launcher);
        var webAppearanceRows = BuildWebAppearanceRows(launcher);
        var (webSizeRows, refreshSizeRows) = BuildWebSizeRows(launcher);
        var (webBehaviorRows, refreshBehaviorVisibility) = BuildWebBehaviorRows(launcher);
        var webPrivacyRows = BuildWebPrivacyRows(launcher);

        // ── Type ─────────────────────────────────────────────────
        // Last to be built, first to be shown: it decides which of the two sets of rows above
        // is relevant, so it needs both of them to exist.
        var typeCombo = new ComboBox { MinWidth = 160 };
        var kinds = new[]
        {
            (Label: "Shortcuts", Value: LauncherKinds.Items),
            (Label: "Web page", Value: LauncherKinds.Web),
        };
        foreach (var kind in kinds)
            typeCombo.Items.Add(new ComboBoxItem { Content = kind.Label, Tag = kind.Value });
        typeCombo.SelectedIndex = Array.FindIndex(kinds, k => k.Value == LauncherKinds.Normalize(launcher.Kind));

        var itemRows = new[] { viewModeRow, iconsPerRowRow, showTitleRow };

        void UpdateKindVisibility()
        {
            bool isWeb = launcher.IsWebLauncher;
            foreach (var row in itemRows)
                row.Visibility = isWeb ? Visibility.Collapsed : Visibility.Visible;
            if (!isWeb)
                UpdateIconModeControls();   // the icons-per-row row has its own condition
            webAddressRow.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in webAppearanceRows)
                row.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
            foreach (var definition in LauncherSettingsSections.All)
            {
                if (_sectionExpanders.TryGetValue(definition.Id, out var expander))
                    expander.Visibility = !definition.WebOnly || isWeb ? Visibility.Visible : Visibility.Collapsed;
            }
            refreshBehaviorVisibility();
            refreshSizeRows();
            SyncAddressBox();
            iconSubtitle.Text = isWeb
                ? "Taken from the page automatically — or pick one here"
                : "Icon style for this launcher";
            refreshIconPreview();
            UpdateAcceptButton();
        }

        typeCombo.SelectionChanged += (s, e) =>
        {
            if (typeCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int kind)
                return;
            if (kind == LauncherKinds.Normalize(launcher.Kind))
                return;

            launcher.Kind = kind;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            // Releases the panel the launcher no longer uses and warms up the one it now does.
            MainWindow.Current?.RefreshTrayIcons();
            UpdateKindVisibility();
        };

        var typeRow = BuildRow("Type", "What the tray icon opens", typeCombo);

        // Shared definitions determine both this form and the flyout menu's section order.
        var sectionRows = new Dictionary<LauncherSettingsSection, IReadOnlyList<FrameworkElement>>
        {
            [LauncherSettingsSection.General] = [nameRow, typeRow, webAddressRow],
            [LauncherSettingsSection.Appearance] = [iconRow, customIconRow, hideRow, taskbarRow,
                viewModeRow, iconsPerRowRow, showTitleRow, .. webAppearanceRows],
            [LauncherSettingsSection.SizeAndPosition] = webSizeRows,
            [LauncherSettingsSection.Behavior] = webBehaviorRows,
            [LauncherSettingsSection.PrivacyAndSignIns] = webPrivacyRows,
        };
        var panel = BuildSettingsSections(sectionRows);
        UpdateKindVisibility();
        return panel;
    }

    /// <summary>
    /// Builds a settings row with the control on its own line, full width, under the label.
    /// </summary>
    /// <remarks>
    /// For controls whose content is long and open-ended — a URL, a path. The side-by-side
    /// <see cref="BuildRow"/> sizes its control column to <c>Auto</c>, so a wide control starves
    /// the label column: a long address squeezed "Web Address" into a three-character ribbon of
    /// wrapped text and stretched the box down the full height of it. Giving the control the whole
    /// width sidesteps the competition rather than trying to balance it.
    /// </remarks>
    private static FrameworkElement BuildStackedRow(string title, string subtitle, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>Builds one label/control settings row in the same shape as the rows above.</summary>
    private static Grid BuildRow(string title, string subtitle, FrameworkElement control) =>
        BuildRow(title, new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        }, control);

    /// <summary>
    /// As above, for a row whose subtitle is rewritten later — the caller keeps the
    /// <see cref="TextBlock"/> and sets its text.
    /// </summary>
    private static Grid BuildRow(string title, TextBlock subtitle, FrameworkElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        label.Children.Add(subtitle);

        // Centred, never stretched. A control with the default vertical alignment grows to the
        // row's height, and the row is as tall as its label — so a subtitle that wraps to two
        // lines silently inflates the input beside it. That is what made the "Unload After"
        // number box look oversized while the identical box on the (single-line) row above
        // looked normal.
        control.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// Builds the bookmark editor: the list, reordering, removal, and the two ways to add one.
    /// </summary>
    /// <remarks>
    /// <para>This is the launcher's content, not an extra: the first bookmark is the page the tray
    /// icon opens, and the rest are the bar. That is stated on the first row rather than left for
    /// the user to infer, because reordering the list changes what the launcher opens.</para>
    /// <para>A second bookmark is what makes the bar appear, which the row's subtitle says for the
    /// same reason — it is a visible change the user is one click away from causing.</para>
    /// </remarks>
    /// <summary>
    /// The bookmark bar's on/off switch, which is all this window says about bookmarks now.
    /// </summary>
    /// <remarks>
    /// <para><b>The list that used to be here is gone.</b> It was a second editor for something the
    /// bar itself does better and in front of the pages being bookmarked: adding by address or from
    /// a browser, renaming, re-addressing, foldering, reordering by drag, and removing all happen on
    /// the bar. Keeping both meant two places to learn, two to keep in step, and a folder tree
    /// rendered twice - and this one was the copy that could not see what page you were on.</para>
    /// <para>What is left is the one thing the bar cannot do for itself, because it is the thing
    /// that decides whether the bar is there at all.</para>
    /// </remarks>
    private FrameworkElement BuildBookmarksRow(Launcher launcher)
    {
        // MinWidth = 0 is not optional: a ToggleSwitch has a wide default that pushes it out of
        // the Auto column BuildRow lays it in, so the row comes out misaligned against every other
        // toggle in the dialog. Every other one here sets it; this one did not.
        var toggle = new ToggleSwitch
        {
            IsOn = launcher.ShowsBookmarkBar,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };

        toggle.Toggled += (_, _) =>
        {
            launcher.WebShowBookmarkBar = toggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };

        return BuildRow("Bookmarks Bar",
            "Shows this launcher's bookmarks along the bottom of the flyout, where they are added, "
            + "renamed, put into folders and reordered.",
            toggle);
    }

    /// <summary>
    /// The launcher-wide "icons only" switch for the bar.
    /// </summary>
    /// <remarks>
    /// It came back out of the bookmark list that was removed, rather than going with it: it is a
    /// property of the <em>bar</em> rather than of any bookmark, so the bar's own per-bookmark
    /// "Icon only" cannot express it - that one collapses one awkward name and leaves the rest
    /// readable, while this one is "I know these sites by sight". The per-bookmark flag only ever
    /// adds to what this collapses; see ShowsIconOnly.
    /// </remarks>
    private FrameworkElement BuildBookmarkIconsOnlyRow(Launcher launcher)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = launcher.WebBookmarkIconsOnly,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };

        toggle.Toggled += (_, _) =>
        {
            launcher.WebBookmarkIconsOnly = toggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };

        return BuildRow("Bookmark Icons Only",
            "Hides every bookmark's label, leaving just its icon.", toggle);
    }

    /// <summary>
    /// Host of a URL, used as a bookmark's name until the page offers a better one.
    /// </summary>
    /// <remarks>
    /// Both of these live on <see cref="WebFlyoutWindow"/> because the flyout's own bookmark
    /// editing needs them too, and a bookmark named one way here and another way there would be a
    /// difference with no reason behind it.
    /// </remarks>
    private static string HostOf(string url) => WebFlyoutWindow.HostOf(url);

    /// <inheritdoc cref="WebFlyoutWindow.FetchBookmarkIconAsync"/>
    private static Task FetchBookmarkIconAsync(Launcher launcher, WebBookmark bookmark) =>
        WebFlyoutWindow.FetchBookmarkIconAsync(launcher, bookmark);

    private (Button Button, Grid CustomRow, Action Refresh) BuildIconChooser(Launcher launcher)
    {
        // ── Preview elements for the button content ──
        var previewIcon = new FontIcon { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var previewImage = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var previewEmoji = new TextBlock
        {
            FontSize = 18, FontFamily = new FontFamily("Segoe UI Emoji"),
            IsColorFontEnabled = true, VerticalAlignment = VerticalAlignment.Center
        };
        var previewLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var chevron = new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        var buttonContent = new StackPanel { Orientation = Orientation.Horizontal };
        buttonContent.Children.Add(previewIcon);
        buttonContent.Children.Add(previewImage);
        buttonContent.Children.Add(previewEmoji);
        buttonContent.Children.Add(previewLabel);
        buttonContent.Children.Add(chevron);

        // ── Custom icon path row ──
        // Keep the saved filename informational; image browsing belongs to the chooser.
        Grid customIconRow = null!;
        Action refreshCustomPath = static () => { };
        (customIconRow, refreshCustomPath) = BuildCustomIconRow(launcher);
        customIconRow.Visibility = launcher.TrayIconMode == TrayIconModes.Custom ? Visibility.Visible : Visibility.Collapsed;

        void UpdatePreview()
        {
            string mode = launcher.TrayIconMode;
            refreshCustomPath();
            previewIcon.Visibility = Visibility.Collapsed;
            previewImage.Visibility = Visibility.Collapsed;
            previewEmoji.Visibility = Visibility.Collapsed;
            // Clear any custom color from a previous glyph selection
            previewIcon.ClearValue(FontIcon.ForegroundProperty);
            previewEmoji.ClearValue(TextBlock.ForegroundProperty);

            // A web launcher wears the icon of the page it opens until someone picks one instead
            // (WebFlyoutWindow.MayAdoptPageIcon). Neither of the two modes that state describes
            // reads as that: "Composite" composes item icons it hasn't got, and "Custom" names a
            // file the user never chose. Both are shown as what they actually are.
            if (launcher.IsWebLauncher && WebFlyoutWindow.MayAdoptPageIcon(launcher))
            {
                string adopted = WebFlyoutWindow.GetPageIconPath(launcher.Id);
                if (mode == TrayIconModes.Custom && File.Exists(adopted))
                {
                    var bitmap = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache };
                    bitmap.UriSource = new Uri(adopted);
                    previewImage.Source = bitmap;
                    previewImage.Visibility = Visibility.Visible;
                }
                else
                {
                    previewIcon.Glyph = "";   // globe — nothing has loaded yet
                    previewIcon.Visibility = Visibility.Visible;
                }
                previewLabel.Text = "From the page";
                customIconRow.Visibility = Visibility.Collapsed;
                return;
            }

            if (mode == TrayIconModes.Composite)
            {
                previewIcon.Glyph = "\uF0E2";
                previewIcon.Visibility = Visibility.Visible;
                previewLabel.Text = "Composite";
            }
            else if (mode == TrayIconModes.Custom)
            {
                if (File.Exists(launcher.CustomTrayIconPath))
                {
                    previewImage.Source = new BitmapImage
                    {
                        CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                        UriSource = new Uri(launcher.CustomTrayIconPath)
                    };
                    previewImage.Visibility = Visibility.Visible;
                }
                else
                {
                    previewIcon.Glyph = "\uE8B9";
                    previewIcon.Visibility = Visibility.Visible;
                }
                previewLabel.Text = "Custom";
            }
            else if (TrayIconModes.IsGlyphMode(mode))
            {
                string glyph = TrayIconModes.GetGlyphCharacter(mode) ?? "";
                string? colorHex = TrayIconModes.GetGlyphColor(mode);
                SolidColorBrush? colorBrush = null;
                if (!string.IsNullOrEmpty(colorHex))
                {
                    try
                    {
                        string h = colorHex.TrimStart('#');
                        if (h.Length == 6)
                        {
                            byte cr = Convert.ToByte(h[..2], 16);
                            byte cg = Convert.ToByte(h[2..4], 16);
                            byte cb = Convert.ToByte(h[4..6], 16);
                            colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, cr, cg, cb));
                        }
                    }
                    catch { /* ignore */ }
                }

                if (IconGallery.IsFluentGlyph(glyph))
                {
                    previewIcon.Glyph = glyph;
                    if (colorBrush != null)
                        previewIcon.Foreground = colorBrush;
                    else
                        previewIcon.ClearValue(FontIcon.ForegroundProperty);
                    previewIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    previewEmoji.Text = glyph;
                    if (colorBrush != null)
                        previewEmoji.Foreground = colorBrush;
                    else
                        previewEmoji.ClearValue(TextBlock.ForegroundProperty);
                    previewEmoji.Visibility = Visibility.Visible;
                }
                previewLabel.Text = "";
            }
            else
            {
                // Known preset: color or glyph
                string iconsDir = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons");
                string[] colorNames = ["Blue", "Green", "Teal", "Red", "Orange", "Purple"];
                if (colorNames.Contains(mode))
                {
                    string pngPath = Path.Combine(iconsDir, $"{mode}.png");
                    if (File.Exists(pngPath))
                        previewImage.Source = new BitmapImage(new Uri(pngPath));
                    previewImage.Visibility = Visibility.Visible;
                    previewLabel.Text = mode;
                }
                else
                {
                    // Glyph preset (Pin, Star, Heart, etc.)
                    (string glyph, string label)[] glyphs = [
                        ("\uE840", "Pin"), ("\uE734", "Star"), ("\uEB51", "Heart"),
                        ("\uE945", "Lightning"), ("\uE721", "Search"), ("\uE774", "Globe"),
                    ];
                    var match = glyphs.FirstOrDefault(g => g.label == mode);
                    if (match.glyph != null)
                    {
                        previewIcon.Glyph = match.glyph;
                        previewIcon.Visibility = Visibility.Visible;
                        previewLabel.Text = match.label;
                    }
                    else
                    {
                        previewIcon.Glyph = "\uE774";
                        previewIcon.Visibility = Visibility.Visible;
                        previewLabel.Text = mode;
                    }
                }
            }

            customIconRow.Visibility = mode == TrayIconModes.Custom
                ? Visibility.Visible : Visibility.Collapsed;
        }

        var button = new Button { Content = buttonContent, Padding = new Thickness(10, 6, 10, 6) };

        // Gallery image browsing and other selections use the same commit path.
        async Task ApplySelectionAsync(IconGallery.IconResult result)
        {
            try
            {
                if (result.UsePageIcon)
                {
                    if (!launcher.IsWebLauncher || result.PageIconPreview is not { } pagePreview ||
                        !string.Equals(launcher.WebAddress, pagePreview.Url, StringComparison.Ordinal))
                    {
                        await ShowErrorAsync((string)Application.Current.Resources["PageIconAddressChanged"]);
                        return;
                    }
                    // Keep the managed favicon path itself: copying it into a custom file
                    // would stop the page from updating its own icon on later visits.
                    launcher.CustomTrayIconPath = WebFlyoutWindow.StorePageIconPreview(launcher.Id, pagePreview);
                    launcher.TrayIconMode = string.IsNullOrEmpty(launcher.CustomTrayIconPath)
                        ? TrayIconModes.Composite
                        : TrayIconModes.Custom;
                    // Replacing bytes at the same managed path changes neither observable
                    // property. Refresh derived icons even when the page option was already set.
                    MainWindow.Current?.UpdateTrayIcon(launcher);
                }
                else if (result.Glyph != null)
                {
                    launcher.TrayIconMode = TrayIconModes.ToGlyphMode(result.Glyph, result.Color);
                    launcher.CustomTrayIconPath = "";
                }
                else if (result.ImagePath != null)
                {
                    string destination = Path.Combine(MainWindow.GetPhysicalAppDataDir(),
                        $"custom-tray-icon-{launcher.Id}{Path.GetExtension(result.ImagePath)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // Re-confirming an existing custom image must not copy the file onto itself.
                    if (!string.Equals(Path.GetFullPath(result.ImagePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(result.ImagePath, destination, overwrite: true);
                    }
                    launcher.CustomTrayIconPath = destination;
                    launcher.TrayIconMode = TrayIconModes.Custom;
                }
                else if (result.PresetMode != null)
                {
                    launcher.TrayIconMode = result.PresetMode;
                    launcher.CustomTrayIconPath = "";
                }
                SettingsManager.SaveSettings();
                Services.AutoSyncService.NotifyLaunchersChanged();
                UpdatePreview();
            }
            catch (Exception)
            {
                await ShowErrorAsync((string)Application.Current.Resources["IconSelectionFailed"]);
            }
        }

        async Task BrowseAsync()
        {
            try
            {
                string? path = await IconGallery.PickImageAsync(_hwnd);
                if (path != null)
                {
                    await ApplySelectionAsync(new IconGallery.IconResult(null, path));
                }
            }
            catch (Exception)
            {
                await ShowErrorAsync((string)Application.Current.Resources["IconSelectionFailed"]);
            }
        }

        var flyout = IconGallery.CreateLauncherIconFlyout(
            currentMode: launcher.TrayIconMode,
            onSelected: async result => await ApplySelectionAsync(result),
            onBrowseRequested: async () => await BrowseAsync(),
            ownerHwnd: _hwnd,
            getCurrentSelection: () => launcher.IsWebLauncher && WebFlyoutWindow.MayAdoptPageIcon(launcher)
                ? new IconGallery.IconResult(null, null, UsePageIcon: true)
                : IconGallery.LauncherSelection(launcher.TrayIconMode, launcher.CustomTrayIconPath),
            canUsePageIcon: () => launcher.IsWebLauncher,
            getPageIconPreview: () =>
            {
                string url = launcher.WebAddress;
                bool IsCurrent() => launcher.IsWebLauncher && string.Equals(launcher.WebAddress, url, StringComparison.Ordinal);
                return new PageIconPreviewSource(url, WebFlyoutWindow.GetPageIconPath(launcher.Id),
                    token => WebFlyoutWindow.FetchPageIconPreviewAsync(launcher.Id, url, IsCurrent, token),
                    IsCurrent);
            });

        button.Flyout = flyout;
        UpdatePreview();

        return (button, customIconRow, UpdatePreview);
    }

    /// <summary>Shows the current custom file without a second selection entry point.</summary>
    private (Grid Row, Action Refresh) BuildCustomIconRow(Launcher launcher)
    {
        var pathText = new TextBlock
        {
            Text = string.IsNullOrEmpty(launcher.CustomTrayIconPath)
                ? "No file selected"
                : System.IO.Path.GetFileName(launcher.CustomTrayIconPath),
            FontSize = 12,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        };

        void RefreshPath()
        {
            pathText.Text = string.IsNullOrEmpty(launcher.CustomTrayIconPath)
                ? "No file selected"
                : Path.GetFileName(launcher.CustomTrayIconPath);
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = "Custom Icon", FontSize = 14 });
        label.Children.Add(pathText);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        return (row, RefreshPath);
    }

    /// <summary>
    /// Waits out an in-flight site-icon fetch, saying so on the button that is waiting.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="IconAdoptionWait"/>: a host that does not answer must not leave the
    /// button dead. Pinning then goes ahead with whatever icon the launcher has, which is the
    /// behaviour this had before it waited at all.
    /// </remarks>
    private async Task WaitForIconAdoptionAsync(Button? pinButton)
    {
        if (_iconAdoption.IsCompleted) return;

        object? content = pinButton?.Content;
        if (pinButton != null)
        {
            pinButton.IsEnabled = false;
            pinButton.Content = "Fetching icon…";
        }

        try
        {
            await Task.WhenAny(_iconAdoption, Task.Delay(IconAdoptionWait));
        }
        finally
        {
            if (pinButton != null)
            {
                pinButton.Content = content;
                pinButton.IsEnabled = true;
            }
        }
    }
}
