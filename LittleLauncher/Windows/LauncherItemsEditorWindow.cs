using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Controls;
using LittleLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>
/// The regular Settings-origin item editor. It hosts the same launcher view as the tray popup;
/// edits persist immediately and Done only closes the window.
/// </summary>
internal sealed class LauncherItemsEditorWindow : Window, ILauncherItemsHost
{
    private const int WindowWidthDips = 560;
    private const int WindowHeightDips = 620;
    private static readonly Dictionary<string, LauncherItemsEditorWindow> Instances = [];
    private readonly MainWindow _owner;
    private readonly Launcher _launcher;
    private readonly Window _windowOwner;
    private readonly IntPtr _hwnd;
    private readonly TaskCompletionSource _completion = new();
    private LauncherItemsView _itemsView = null!;
    private Grid _body = null!;
    private Border _launcherPreview = null!;
    private ContentControl _bodyInputScope = null!;
    private Window? _openModal;
    private bool _isModalOpen;
    private bool _closing;

    internal static bool IsAnyOpen => Instances.Count > 0;

    /// <summary>
    /// Opens one editor per launcher, reusing an existing window or its active owned dialog.
    /// Setup bounds are physical pixels captured before the preceding settings window closes.
    /// </summary>
    internal static Task ShowAsync(MainWindow owner, string launcherId, Window windowOwner, RectInt32? setupBounds = null)
    {
        if (TryFocus(launcherId)) return Instances[launcherId]._completion.Task;
        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null || launcher.IsWebLauncher || launcher is { IsShared: true, IsSharedOwner: false })
            return Task.CompletedTask;

        FlyoutWindow.ParkForItemEditor(launcherId);
        var window = new LauncherItemsEditorWindow(owner, launcher, windowOwner, setupBounds);
        Instances[launcherId] = window;
        try
        {
            window.Activate();
            window._itemsView.EnterEditMode();
            return window._completion.Task;
        }
        catch
        {
            window.Cleanup();
            window.Close();
            throw;
        }
    }

    private LauncherItemsEditorWindow(MainWindow owner, Launcher launcher, Window windowOwner, RectInt32? setupBounds)
    {
        _owner = owner;
        _launcher = launcher;
        _windowOwner = windowOwner;
        IntPtr ownerHwnd = WindowNative.GetWindowHandle(windowOwner);
        _hwnd = WindowNative.GetWindowHandle(this);
        try
        {
            Title = ResourceText("LauncherItemsTitle");
            SystemBackdrop = new MicaBackdrop();
            if (ownerHwnd != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, ownerHwnd);

            var titleBar = WindowChrome.BuildTitleBar(Title);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBar);

            _itemsView = new LauncherItemsView(launcher, this);
            var toolbar = new ContentPresenter { Content = _itemsView.Toolbar, HorizontalAlignment = HorizontalAlignment.Stretch };

            _launcherPreview = CreateLauncherPreview(_itemsView);
            var viewport = new ScrollViewer
            {
                Content = _launcherPreview,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var done = new Button
            {
                Content = ResourceText("LauncherItemsDone"),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            done.Click += (_, _) => Close();

            _body = new Grid { Padding = new Thickness(24, 8, 24, 24) };
            _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(viewport, 1);
            Grid.SetRow(done, 2);
            _body.Children.Add(toolbar);
            _body.Children.Add(viewport);
            _body.Children.Add(done);
            _body.SizeChanged += (_, _) => _itemsView.UpdateToolbarWidth(Math.Max(1, _body.ActualWidth - 48));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _bodyInputScope = new ContentControl
            {
                Content = _body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                IsTabStop = false,
            };
            Grid.SetRow(_bodyInputScope, 1);
            root.Children.Add(titleBar);
            root.Children.Add(_bodyInputScope);
            root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
            var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, args) => { args.Handled = true; Close(); };
            root.KeyboardAccelerators.Add(escape);

            Content = root;
            ThemeManager.ApplySavedTheme(this);
            WindowChrome.ApplyIcon(_hwnd);
            SizeAndCentre(ownerHwnd, setupBounds);
            UpdateContentExtent();
            _windowOwner.AppWindow.Closing += Owner_Closing;
            _windowOwner.Closed += Owner_Closed;
            AppWindow.Closing += (_, _) => Cleanup();
            Closed += (_, _) => Cleanup();
            Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated && _openModal != null)
                    FocusEditor();
            };
        }
        catch
        {
            Cleanup();
            Close();
            throw;
        }
    }

    /// <summary>Matches other settings windows, centring setup continuation on its captured monitor.</summary>
    private void SizeAndCentre(IntPtr ownerHwnd, RectInt32? setupBounds)
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        IntPtr referenceHwnd = ownerHwnd != IntPtr.Zero ? ownerHwnd : _hwnd;
        if (setupBounds is { } bounds)
        {
            var point = new POINT { X = bounds.X + (bounds.Width / 2), Y = bounds.Y + (bounds.Height / 2) };
            IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            GetDpiForMonitor(monitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpi, out _);
            PlaceInWorkArea(appWindow, new RectInt32(info.rcWork.Left, info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top),
                (dpi > 0 ? dpi : 96) / 96.0, point.X, point.Y);
            return;
        }

        var area = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(referenceHwnd), DisplayAreaFallback.Nearest);
        if (area == null) return;
        double scale = GetDpiForWindow(referenceHwnd) / 96.0;
        if (scale <= 0) scale = 1;
        PlaceInWorkArea(appWindow, area.WorkArea, scale,
            area.WorkArea.X + (area.WorkArea.Width / 2), area.WorkArea.Y + (area.WorkArea.Height / 2));
    }

    /// <summary>Clamps only the regular window; the item's intrinsic layout scrolls unchanged.</summary>
    private static void PlaceInWorkArea(AppWindow window, RectInt32 work, double scale, int centerX, int centerY)
    {
        int width = Math.Min(work.Width, (int)Math.Ceiling(WindowWidthDips * scale));
        int height = Math.Min(work.Height, (int)Math.Ceiling(WindowHeightDips * scale));
        int left = Math.Clamp(centerX - (width / 2), work.X, Math.Max(work.X, work.X + work.Width - width));
        int top = Math.Clamp(centerY - (height / 2), work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        window.Resize(new SizeInt32(width, height));
        window.Move(new PointInt32(left, top));
    }

    private static string ResourceText(string key) => (string)Application.Current.Resources[key];

    /// <summary>Uses the existing themed card surface around the preview, outside item geometry.</summary>
    private static Border CreateLauncherPreview(LauncherItemsView view)
    {
        // ThemeResource references update with light, dark and high-contrast themes. Resolving
        // brushes from Application.Resources once would freeze the initial theme's selection.
        var preview = (Border)XamlReader.Load(
            """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                    BorderThickness="1" CornerRadius="4" />
            """);
        preview.Child = view;
        return preview;
    }

    /// <summary>Updates the scrollable content extent without resizing/reflowing the window.</summary>
    private void UpdateContentExtent()
    {
        if (_closing) return;
        if (_launcher.IsWebLauncher) { Close(); return; }
        _itemsView.Width = _itemsView.ContentWidth;
        _itemsView.Height = _itemsView.ContentHeight;
        // Keep the surface at intrinsic bounds, never stretched over the scrolling viewport.
        // The stroke is outside the view, so it cannot inset/reflow items or alter local drops.
        _launcherPreview.Width = _itemsView.Width + _launcherPreview.BorderThickness.Left + _launcherPreview.BorderThickness.Right;
        _launcherPreview.Height = _itemsView.Height + _launcherPreview.BorderThickness.Top + _launcherPreview.BorderThickness.Bottom;
    }

    internal static bool TryFocus(string launcherId)
    {
        if (!Instances.TryGetValue(launcherId, out var window) || window._closing) return false;
        window.FocusEditor();
        return true;
    }

    /// <summary>Returning from tray/pin targets this editor's owned modal before its main view.</summary>
    private void FocusEditor()
    {
        if (_closing) return;
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
        IntPtr target = _openModal != null ? WindowNative.GetWindowHandle(_openModal) : _hwnd;
        if (IsWindow(target)) SetForegroundWindow(target);
    }

    internal static void CloseForLauncher(string launcherId)
    {
        if (Instances.TryGetValue(launcherId, out var window)) window.Close();
    }

    internal static void ApplyTheme(ElementTheme theme)
    {
        foreach (var window in Instances.Values)
        {
            if (window.Content is FrameworkElement root) root.RequestedTheme = theme;
        }
    }

    /// <summary>Cleanup runs before native destruction and is safe for programmatic Close too.</summary>
    private void Cleanup()
    {
        if (_closing) return;
        _closing = true;
        _windowOwner.AppWindow.Closing -= Owner_Closing;
        _windowOwner.Closed -= Owner_Closed;
        if (Instances.TryGetValue(_launcher.Id, out var current) && ReferenceEquals(current, this))
            Instances.Remove(_launcher.Id);
        _itemsView?.SetEditing(false, notifyHost: false);
        var modal = _openModal;
        _openModal = null;
        try { modal?.Close(); }
        finally
        {
            _itemsView?.Dispose();
            _completion.TrySetResult();
        }
    }

    private void Owner_Closing(AppWindow sender, AppWindowClosingEventArgs args) => Close();

    // WinUI raises Closed before native owner destruction, including programmatic Close.
    private void Owner_Closed(object sender, WindowEventArgs args) => Close();

    bool ILauncherItemsHost.IsRegularWindow => true;
    IntPtr ILauncherItemsHost.DialogOwnerHwnd => _hwnd;
    void ILauncherItemsHost.ContentSizeChanged(bool keepRightEdge) => UpdateContentExtent();
    void ILauncherItemsHost.EditModeChanged() => UpdateContentExtent();
    void ILauncherItemsHost.FinishEditing() => Close();
    void ILauncherItemsHost.LaunchItem(LauncherItem item) => FlyoutWindow.Launch(item);
    void ILauncherItemsHost.OpenAppSettings() => SettingsWindow.ShowInstance(_owner);
    void ILauncherItemsHost.BeginIconResize() { }
    double ILauncherItemsHost.GetResizePointerX(PointerRoutedEventArgs args) =>
        args.GetCurrentPoint((UIElement)Content).Position.X;

    /// <summary>Prevents overlapping dialogs, keeps owner chrome normal, and never refocuses after close.</summary>
    async Task<T> ILauncherItemsHost.RunModalAsync<T>(Func<Action<Window>, Task<T>> show)
    {
        if (_closing || _isModalOpen) return default!;
        _isModalOpen = true;
        var previousFocus = FocusManager.GetFocusedElement(_body.XamlRoot) as Control;
        bool wasEnabled = _bodyInputScope.IsEnabled;
        // Control.IsEnabled disables its descendants for pointer, keyboard and accessibility
        // interaction together. The Grid still owns layout, and the normal caption stays usable.
        _bodyInputScope.IsEnabled = false;
        try
        {
            return await show(window => _openModal = window);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;
            if (!_closing)
            {
                _bodyInputScope.IsEnabled = wasEnabled;
                if (GetForegroundWindow() == _hwnd)
                {
                    bool restored = previousFocus != null
                        && previousFocus.XamlRoot == _body.XamlRoot
                        && previousFocus.IsEnabled
                        && previousFocus.Focus(FocusState.Programmatic);
                    if (!restored) _itemsView.Focus(FocusState.Programmatic);
                }
            }
        }
    }
}
