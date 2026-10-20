using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Controls;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml.Markup;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow : Window, ILauncherItemsHost
{
    private const double SlideDistanceDip = 36;
    private const uint ShowAnimationDurationMs = 200;
    private const uint HideAnimationDurationMs = 160;

    /// <summary>
    /// Fraction of the hide animation over which the flyout fades out, so the fade is complete
    /// before the window is taken off screen.
    /// </summary>
    /// <remarks>
    /// Deliberately less than 1. Fading right up to the final frame leaves the window still
    /// faintly visible when it parks , with ~8ms ticks the last painted alpha lands wherever the
    /// cadence happens to fall , so the content would wink out instead of finishing its fade.
    /// Completing early means the last stretch of the slide is already fully transparent and the
    /// park is guaranteed to be invisible.
    /// </remarks>
    private const double FadeOutCompleteAt = 0.8;

    /// <summary>How long a warmed-up flyout stays parked off screen to compose its first frame.</summary>
    private const int PreRenderDurationMs = 400;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly object BoundsFileLock = new();
    private static readonly ConcurrentDictionary<string, WindowBounds> CachedBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-launcher flyout window instances (key = Launcher.Id).</summary>
    private static readonly Dictionary<string, FlyoutWindow> _instances = new();

    private readonly LauncherItemsView _itemsView;
    private readonly Launcher _launcher;
    private MainWindow? _owner;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;
    private DateTime _lastDismissed = DateTime.MinValue;
    private bool _toolWindowStyleApplied;
    private bool _isPreRendering;
    private bool _isOpen;
    private bool _fadeStyleApplied;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _preRenderTimer;
    private bool _isShowing;
    private bool _isHiding;
    private int _animationVersion;
    private FlyoutEntranceEdge _lastEntranceEdge = FlyoutEntranceEdge.Bottom;
    private bool _isEditMode => _itemsView.IsEditMode;
    private bool _isResizingIconWidth => _itemsView.IsResizing;
    private readonly record struct FlyoutPlacement(
        int Left,
        int Top,
        int StartTop,
        int Width,
        int Height,
        FlyoutEntranceEdge Edge);

    private enum FlyoutEntranceEdge
    {
        Top,
        Bottom,
    }

    private static bool AreAnimationsEnabled => SettingsManager.Current.FlyoutAnimationsEnabled;

    private FlyoutWindow(MainWindow owner, Launcher launcher)
    {
        _owner = owner;
        _launcher = launcher;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Remove titlebar and make borderless, always on top so it
        // renders above the tray overflow popup.
        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        GetAppWindow().SetPresenter(presenter);

        _itemsView = new LauncherItemsView(launcher, this);
        WindowRoot.Children.Add(_itemsView);

        // Desktop Acrylic blurs whatever is behind the window (including other windows),
        // unlike Mica which only samples the wallpaper.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // OS-level rounded corners (Windows 11) + DWM shadow
        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Hook WndProc for deactivation detection
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 2, 0);
        Activated += FlyoutWindow_Activated;

        // Apply saved app theme
        ThemeManager.ApplySavedTheme(this);
    }

    private void FlyoutWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Edit mode (and any modal it owns) pins the flyout open , otherwise opening the
        // item editor or a file picker would dismiss the very window being edited.
        if (_isShowing || _isResizingIconWidth || SuppressDismiss) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            HideFlyout();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (msg == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN + VK_ESCAPE
        {
            // Two-stage: the first Escape leaves edit mode, the second dismisses.
            if (_isEditMode)
            {
                ExitEditMode();
                return IntPtr.Zero;
            }

            HideFlyout();
            _lastDismissed = DateTime.UtcNow;
            return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    internal static FlyoutWindow? GetCurrent(string? launcherId = null)
    {
        if (launcherId != null)
            return _instances.TryGetValue(launcherId, out var fw) ? fw : null;
        return _instances.Values.FirstOrDefault();
    }

    public static void Toggle(MainWindow owner, int screenX, int screenY, string launcherId) =>
        Toggle(owner, screenX, screenY, launcherId, setupBounds: null);

    /// <summary>Opens normally, or with a transient anchor for a new launcher's first edit session.</summary>
    private static void Toggle(
        MainWindow owner,
        int screenX,
        int screenY,
        string launcherId,
        global::Windows.Graphics.RectInt32? setupBounds)
    {
        if (LauncherItemsEditorWindow.TryFocus(launcherId)) return;
        if (!_instances.TryGetValue(launcherId, out var instance) || instance == null)
        {
            // Find the launcher
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher == null) return;
            instance = new FlyoutWindow(owner, launcher);
            _instances[launcherId] = instance;
        }

        // Ends the warm-up render, if one is still running, so the window is free to be placed.
        instance.EndPreRender();

        if (instance._isOpen && instance._hwnd != IntPtr.Zero && IsWindow(instance._hwnd))
        {
            if (!instance._isHiding)
                instance.HideFlyout();
            return;
        }

        if (!instance._isHiding && (DateTime.UtcNow - instance._lastDismissed).TotalMilliseconds < 300)
            return;

        instance._owner = owner;
        instance._setupEditBounds = setupBounds;
        instance.RebuildItemsIfNeeded();

        // Calculate DPI-aware dimensions
        double dpiScale = GetDpiForWindow(instance._hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int flyoutWidthPx = (int)Math.Ceiling(instance.GetFlyoutWidth() * dpiScale);
        int flyoutHeightPx = (int)Math.Ceiling(instance.MeasureContentHeight() * dpiScale);

        instance._isShowing = true;
        var appWindow = instance.GetAppWindow();
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(flyoutWidthPx, flyoutHeightPx));
        if (!instance._toolWindowStyleApplied)
        {
            int exStyle = GetWindowLong(instance._hwnd, GWL_EXSTYLE);
            SetWindowLong(instance._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            instance._toolWindowStyleApplied = true;
        }

        var placement = instance.CalculatePlacement(screenX, screenY);
        instance._lastEntranceEdge = placement.Edge;
        if (AreAnimationsEnabled)
            instance.ShowAnimated(placement);
        else
            instance.ShowWithoutAnimation(placement);
    }

    public static void DismissIfOpen()
    {
        foreach (var fw in _instances.Values)
            fw.HideFlyout();
    }

    public static void WarmUp(MainWindow owner, IEnumerable<Launcher> launchers)
    {
        foreach (var launcher in launchers)
        {
            if (!_instances.ContainsKey(launcher.Id))
            {
                var fw = new FlyoutWindow(owner, launcher);
                fw._itemsView.Refresh();
                int exStyle = GetWindowLong(fw._hwnd, GWL_EXSTYLE);
                SetWindowLong(fw._hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
                fw._toolWindowStyleApplied = true;
                fw.PreRenderOffScreen();
                _instances[launcher.Id] = fw;
            }
        }
    }

    /// <summary>
    /// Composes the window's first frame while it is parked outside the desktop.
    /// </summary>
    /// <remarks>
    /// Constructing a window is not enough to give it a rendered surface: WinUI only draws a
    /// window that has actually been visible. Until then the DWM has nothing to present, so the
    /// first show flashed the window's extended frame , a black rectangle , for the few frames
    /// XAML took to lay out and paint. That first paint got slower as the flyout gained content
    /// (per-column containers, hover and edit chrome), which is when the flash became visible.
    ///
    /// Parked at the virtual screen's origin minus the window's own size, so no part of it can
    /// land on a real monitor whatever the display arrangement. The window then *stays* parked
    /// there for the life of the app whenever the flyout is dismissed , see
    /// <see cref="ParkOffScreen"/> , so this warm-up is the only time it has to compose from
    /// nothing.
    /// </remarks>
    private void PreRenderOffScreen()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;
        int width = (int)Math.Ceiling(GetFlyoutWidth() * dpiScale);
        int height = (int)Math.Ceiling(MeasureContentHeight() * dpiScale);

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        _isPreRendering = true;
        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _preRenderTimer = DispatcherQueue.CreateTimer();
        _preRenderTimer.Interval = TimeSpan.FromMilliseconds(PreRenderDurationMs);
        _preRenderTimer.IsRepeating = false;
        _preRenderTimer.Tick += (_, _) => EndPreRender();
        _preRenderTimer.Start();
    }

    /// <summary>
    /// Ends the warm-up render. The window stays parked off screen rather than being hidden ,
    /// see <see cref="ParkOffScreen"/>.
    /// </summary>
    private void EndPreRender()
    {
        if (!_isPreRendering) return;
        _isPreRendering = false;

        _preRenderTimer?.Stop();
        _preRenderTimer = null;
    }

    /// <summary>
    /// Moves the window outside the virtual screen instead of hiding it, and marks it closed.
    /// </summary>
    /// <remarks>
    /// The flyout used to be dismissed with <c>ShowWindow(SW_HIDE)</c>. Hiding a WinUI window
    /// lets its composition surfaces be released, so the next show had to re-rasterise the whole
    /// visual tree , every text run and every item icon , before anything could be presented.
    /// Measured on a cold open, that took ~100ms, during which the window was already on screen
    /// and sliding: the flyout slid in as an empty rectangle and the items all popped in at the
    /// end. Re-opening within a second or so was fine, because the surfaces were still resident;
    /// that is why this only ever looked broken in normal use, where opens are seconds apart.
    ///
    /// Keeping the window visible but off the virtual screen keeps those surfaces alive, so a
    /// show is a pure move and the first on-screen frame is already fully painted.
    ///
    /// <c>_isOpen</c> , not <c>WS_VISIBLE</c> , is now what "the flyout is open" means, since the
    /// window is visible in the Win32 sense for the whole life of the app.
    /// </remarks>
    private void ParkOffScreen()
    {
        _isOpen = false;

        // The hide fade leaves the window transparent; clearing it here means every path back on
        // screen starts opaque, without each having to remember.
        ClearFade();

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        GetWindowRect(_hwnd, out var rect);
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN) - width - 64;
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN) - height - 64;

        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Destroys the flyout instance for a launcher that has been deleted.</summary>
    public static void DisposeLauncher(string launcherId)
    {
        LauncherItemsEditorWindow.CloseForLauncher(launcherId);
        if (_instances.TryGetValue(launcherId, out var fw))
        {
            // The edit chrome lives in its own top-level windows, which closing the flyout
            // does not touch , without this they outlive the launcher they belong to.
            fw.CloseEditChrome();
            fw.Close();
            _instances.Remove(launcherId);
        }
    }

    private void HideFlyout()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        // Ends a warm-up render rather than animating it off screen; the check below then
        // returns, since a warming window was never on screen to dismiss.
        EndPreRender();

        if (!_isOpen)
            return;

        _setupEditBounds = null;
        // Closing ends editing, so the flyout never reopens mid-edit and the off-screen
        // measure path never has to account for edit chrome.
        if (_isEditMode)
        {
            _itemsView.SetEditing(false, notifyHost: false);
            CloseOpenModal();
            _editToolbarBar?.HideBar();
            SetEditModeBorder(false);
        }

        _lastDismissed = DateTime.UtcNow;
        _animationVersion++;

        if (!AreAnimationsEnabled)
        {
            _isShowing = false;
            _isHiding = false;
            ParkOffScreen();
            return;
        }

        _isHiding = true;
        _isShowing = false;

        GetWindowRect(_hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int exitOffset = GetSlideDistancePx();
        int endTop = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top - exitOffset : rect.Top + exitOffset;
        int animationVersion = ++_animationVersion;

        AnimateWindowPosition(animationVersion, rect.Left, rect.Top, endTop, width, height, HideAnimationDurationMs, hideAtEnd: true);
    }

    private void ShowWithoutAnimation(FlyoutPlacement placement)
    {
        _animationVersion++;
        _isHiding = false;
        _isShowing = true;
        _isOpen = true;

        // A hide interrupted mid-fade never reached ParkOffScreen, so the window can still be
        // part-transparent here.
        ClearFade();

        SetWindowPos(_hwnd, 0, placement.Left, placement.Top, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        _isShowing = false;
    }

    private void ShowAnimated(FlyoutPlacement placement)
    {
        _isHiding = false;
        _isShowing = true;

        // Only an already-open flyout continues from where it is; a parked one starts from the
        // placement's off-edge position, since its parked rect is nowhere near the screen.
        int startTop = placement.StartTop;
        if (_isOpen && GetWindowRect(_hwnd, out var rect))
            startTop = rect.Top;

        _isOpen = true;

        // Re-opening while a hide is still fading out would otherwise slide a part-transparent
        // (or fully invisible) window back in , the animation version bump below only stops the
        // fade, it does not undo it.
        ClearFade();

        int animationVersion = ++_animationVersion;

        SetWindowPos(_hwnd, 0, placement.Left, startTop, placement.Width, placement.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);

        if (startTop == placement.Top)
        {
            _isShowing = false;
            return;
        }

        AnimateWindowPosition(animationVersion, placement.Left, startTop, placement.Top,
            placement.Width, placement.Height, ShowAnimationDurationMs, hideAtEnd: false);
    }

    private void AnimateWindowPosition(int animationVersion, int left, int startTop, int endTop, int width, int height, uint durationMs, bool hideAtEnd)
    {
        if (startTop == endTop)
        {
            CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                CompositionTarget.Rendering -= handler;
                return;
            }

            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            double eased = hideAtEnd ? EaseOutExit(progress) : EaseOutCubic(progress);
            int currentTop = (int)Math.Round(Lerp(startTop, endTop, eased));

            if (hideAtEnd)
                SetFadeAlpha(1 - Math.Min(1, progress / FadeOutCompleteAt));

            SetWindowPos(_hwnd, 0, left, currentTop, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

            if (progress >= 1)
            {
                CompositionTarget.Rendering -= handler;
                CompleteWindowAnimation(animationVersion, left, endTop, width, height, hideAtEnd);
            }
        };

        CompositionTarget.Rendering += handler;
    }

    private void CompleteWindowAnimation(int animationVersion, int left, int top, int width, int height, bool hideAtEnd)
    {
        if (animationVersion != _animationVersion || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        SetWindowPos(_hwnd, 0, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

        if (hideAtEnd)
        {
            ParkOffScreen();
            _isHiding = false;
        }
        else
        {
            _isShowing = false;
        }
    }

    /// <summary>
    /// Sets the window's overall opacity for the hide animation, so it fades as it slides.
    /// </summary>
    /// <remarks>
    /// Without this the flyout was fully opaque at the end of its travel and then simply stopped
    /// existing , the eye is still tracking a moving object when it is cut, which reads as an
    /// abrupt snap however smooth the slide itself is.
    ///
    /// Per-window alpha (<c>WS_EX_LAYERED</c> + <c>LWA_ALPHA</c>) rather than XAML opacity: the
    /// flyout's acrylic comes from a <c>SystemBackdrop</c>, which sits behind the XAML tree and
    /// so is untouched by <c>RootGrid.Opacity</c> , fading the content alone would leave the
    /// backdrop pane behind as a solid rectangle, which is the same abrupt cut one layer down.
    ///
    /// <c>ClearFade</c> must run before the window is shown again: the alpha is a window-level
    /// property that survives being parked, so a flyout re-opened after a fade would come back
    /// invisible.
    /// </remarks>
    private void SetFadeAlpha(double opacity)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        if (!_fadeStyleApplied)
        {
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            _fadeStyleApplied = true;
        }

        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Restores full opacity and drops the layered style. See <see cref="SetFadeAlpha"/>.</summary>
    private void ClearFade()
    {
        if (!_fadeStyleApplied) return;
        _fadeStyleApplied = false;

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
    }

    private int GetSlideDistancePx()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        return Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));
    }

    private static double Lerp(int start, int end, double progress)
    {
        return start + ((end - start) * progress);
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1 - progress;
        return 1 - (inverse * inverse * inverse);
    }

    /// <summary>
    /// Exit curve for the hide slide: accelerates away, but with a linear floor so that even the
    /// first frames move.
    /// </summary>
    /// <remarks>
    /// This was a pure cubic ease-in, which stalls at the start , and the flyout only travels
    /// <see cref="SlideDistanceDip"/> (~54px at 150% scale) in <see cref="HideAnimationDurationMs"/>.
    /// Over that little distance a cubic quantises to nothing: measured, the first five frames
    /// (~30ms) all rounded to the same pixel, so the close sat still and then lurched away at
    /// ~7px per frame before the window cut out. That read as choppy even though the animation
    /// loop itself was ticking cleanly at ~8ms.
    ///
    /// The linear term keeps every frame's step above a pixel; the quadratic term keeps the
    /// accelerating-away character an exit wants. Any easing used here has to be checked against
    /// the *pixel* deltas, not just the curve shape , a curve that looks smooth mathematically
    /// can still round to a stationary window over a short slide.
    /// </remarks>
    private static double EaseOutExit(double progress)
    {
        return (0.35 * progress) + (0.65 * progress * progress);
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>Places the native popup at its invocation anchor, clamped to that monitor.</summary>
    private FlyoutPlacement CalculatePlacement(int screenX, int screenY)
    {
        if (_setupEditBounds is { } setupBounds)
            return CalculateSetupPlacement(setupBounds);

        var pt = new POINT { X = screenX, Y = screenY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        int flyoutWidth = (int)Math.Ceiling(GetFlyoutWidth() * scale);
        int flyoutHeight = (int)Math.Ceiling(MeasureContentHeight() * scale);
        int gap = Math.Max(4, (int)Math.Round(8 * scale));
        int slideDistance = Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));

        int left = screenX - flyoutWidth / 2;
        int top;

        // Detect whether the click is near a taskbar edge or from the tray
        // overflow popup (which floats well inside the work area).
        int edgeThreshold = (int)(16 * scale);
        bool nearBottom = screenY >= workArea.Bottom - edgeThreshold;
        bool nearTop = screenY <= workArea.Top + edgeThreshold;
        // Require a reserved side strip so a bottom/top tray icon at a corner is
        // not mistaken for a vertical taskbar. Test sides first at their corners.
        bool nearLeft = workArea.Left > monitorInfo.rcMonitor.Left
            && screenX <= workArea.Left + edgeThreshold;
        bool nearRight = workArea.Right < monitorInfo.rcMonitor.Right
            && screenX >= workArea.Right - edgeThreshold;

        if (nearLeft || nearRight)
        {
            left = nearLeft ? workArea.Left + gap : workArea.Right - flyoutWidth - gap;
            top = screenY - flyoutHeight / 2;
        }
        else if (nearBottom)
        {
            // Taskbar at bottom (common case): position just above taskbar
            top = workArea.Bottom - flyoutHeight - gap;
        }
        else if (nearTop)
        {
            // Taskbar at top: position just below taskbar
            top = workArea.Top + gap;
        }
        else
        {
            // Tray overflow or other mid-screen click: place flyout above the
            // cursor so it doesn't cover the overflow popup the user clicked on.
            top = screenY - flyoutHeight - gap;
        }

        (left, top) = ClampFlyoutPosition(left, top, flyoutWidth, flyoutHeight, workArea);

        var edge = nearTop ? FlyoutEntranceEdge.Top : FlyoutEntranceEdge.Bottom;
        int startTop = edge == FlyoutEntranceEdge.Top ? top - slideDistance : top + slideDistance;
        return new FlyoutPlacement(left, top, startTop, flyoutWidth, flyoutHeight, edge);
    }

    private void ResizeWindowToCurrentContent(bool keepRightEdge, double? explicitHeightDips = null)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        if (_setupEditBounds is { } setupBounds)
        {
            var placement = CalculateSetupPlacement(setupBounds);
            SetWindowPos(_hwnd, 0, placement.Left, placement.Top, placement.Width, placement.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            return;
        }

        GetWindowRect(_hwnd, out var rect);
        double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;

        int newWidth = (int)Math.Ceiling(GetFlyoutWidth() * dpiScale);
        int newHeight = (int)Math.Ceiling((explicitHeightDips ?? MeasureContentHeight()) * dpiScale);
        int left = keepRightEdge ? rect.Right - newWidth : rect.Left;
        int top = _lastEntranceEdge == FlyoutEntranceEdge.Top ? rect.Top : rect.Bottom - newHeight;

        var centerPoint = new POINT
        {
            X = rect.Left + ((rect.Right - rect.Left) / 2),
            Y = rect.Top + ((rect.Bottom - rect.Top) / 2)
        };
        IntPtr hMonitor = MonitorFromPoint(centerPoint, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        var workArea = monitorInfo.rcWork;

        (left, top) = ClampFlyoutPosition(left, top, newWidth, newHeight, workArea);

        SetWindowPos(_hwnd, 0, left, top, newWidth, newHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// Runs one item, whatever kind it is, with no flyout involved.
    /// </summary>
    /// <remarks>
    /// Static because the flyout is not always the caller: a task on a pinned taskbar button
    /// launches an item without anything being on screen, and duplicating this switch is how the
    /// two would come to disagree about, say, what a <c>.bat</c> or a folder means.
    /// </remarks>
    internal static void Launch(LauncherItem item)
    {
        try
        {
            if (item.IsWebsite || item.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               || item.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                LaunchWebsite(item);
            }
            else if (item.IsPwa)
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"shell:AppsFolder\\{item.Path}",
                    UseShellExecute = false
                });
            }
            else if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(item.Path)
                {
                    UseShellExecute = true,
                    Arguments = item.Arguments ?? ""
                });
            }
            else
            {
                var args = item.Arguments ?? "";
                var path = item.Path;
                ProcessStartInfo psi;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo(path) { UseShellExecute = true };
                }
                else if (Directory.Exists(path))
                {
                    // A folder isn't a process , hand it to Explorer.
                    psi = new ProcessStartInfo("explorer.exe")
                    {
                        Arguments = $"\"{path}\"",
                        UseShellExecute = false
                    };
                }
                else if (path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/c \"{path}\" {args}",
                        UseShellExecute = false
                    };
                }
                else if (IsDocumentPath(path))
                {
                    // A document has no launch semantics of its own , the shell picks the
                    // handler. CreateProcess would just fail with "not a valid application".
                    psi = new ProcessStartInfo(path) { UseShellExecute = true };
                }
                else
                {
                    psi = new ProcessStartInfo(path)
                    {
                        Arguments = args,
                        UseShellExecute = false
                    };
                }
                Process.Start(psi);
            }
            Logger.Info($"Launched item: {item.Name} ({item.Path})");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to launch item: {item.Name} ({item.Path})");
        }
    }

    /// <summary>
    /// Runs the item a taskbar jump list task names, and reports whether it found one.
    /// </summary>
    /// <remarks>
    /// <para>The position is only a hint. It is where the item sat when the list was published,
    /// which is right almost every time and wrong exactly when it matters - after an edit, a
    /// drag, or a sync that brought another machine's items in. So the token has the final say:
    /// it identifies the item by what it launches, and the position merely saves searching for
    /// it.</para>
    /// <para>Returning false rather than guessing is deliberate. Launching whatever now occupies
    /// that position would be the one outcome worse than doing nothing, and the caller has a
    /// better answer anyway - see <see cref="LauncherPanels.LaunchFromJumpList"/>.</para>
    /// </remarks>
    internal static bool LaunchItemFromJumpList(Launcher launcher, int index, int token)
    {
        var target = ResolveJumpListItem(launcher, index, token);
        if (target == null)
        {
            Logger.Info($"Jump list task no longer matches an item in {launcher.Name}; opening the launcher instead");
            return false;
        }

        Launch(target);
        return true;
    }

    /// <summary>
    /// The item a jump list entry stands for, or null when the launcher no longer has it.
    /// </summary>
    /// <remarks>
    /// The position is only a hint. It is where the item sat when the list was published, which is
    /// right almost every time and wrong exactly when it matters - after an edit, a drag, or a sync
    /// that brought another machine's items in. So the token has the final say: it identifies the
    /// item by what it launches, and the position merely saves searching for it. Guessing when
    /// neither matches would mean launching, or deleting, whatever now occupies that position.
    /// </remarks>
    private static LauncherItem? ResolveJumpListItem(Launcher launcher, int index, int token)
    {
        var items = new List<LauncherItem>();
        MainWindow.CollectLaunchableItems(launcher.Items, items, int.MaxValue);

        if (index >= 0 && index < items.Count && JumpListService.ItemToken(items[index]) == token)
            return items[index];

        return items.FirstOrDefault(i => JumpListService.ItemToken(i) == token);
    }

    /// <summary>
    /// True for an existing file the OS wouldn't accept as an executable image. A bare name
    /// like <c>notepad</c> is left alone , it resolves through PATH, not the filesystem.
    /// </summary>
    private static bool IsDocumentPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return false;

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(path);
    }

    private static void LaunchWebsite(LauncherItem item)
    {
        if (!item.OpenInAppWindow)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        if (TryLaunchInAppWindow(item.Path, item.AppWindowBrowser, item.AppWindowBrowserProfile))
            return;

        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private static bool TryLaunchInAppWindow(string url, string browserPath, string browserProfile)
    {
        string profileId = GetAppWindowProfileId(url);
        string browserExe = ResolveBrowserExe(browserPath);
        if (browserExe == "") return false;

        var engine = BrowserCatalog.DetectEngine(browserExe);
        var existingWindows = GetBrowserWindows(engine);

        try
        {
            string args = engine == BrowserEngine.Gecko
                ? BuildGeckoArgs(url, profileId)
                : BuildChromiumArgs(url, browserProfile, profileId);

            Process.Start(new ProcessStartInfo { FileName = browserExe, Arguments = args, UseShellExecute = false });
            _ = RestoreAndTrackWindowBoundsAsync(existingWindows, profileId, engine);
            return true;
        }
        catch { return false; }
    }

    private static string BuildChromiumArgs(string url, string browserProfile, string profileId)
    {
        string args = $"--app=\"{url}\"";
        if (string.IsNullOrEmpty(browserProfile))
        {
            string appProfileDir = GetAppWindowProfileDirectory(profileId);
            Directory.CreateDirectory(appProfileDir);
            args += $" --user-data-dir=\"{appProfileDir}\"";
        }
        else if (browserProfile != "__default__")
        {
            args += $" --profile-directory=\"{browserProfile}\"";
        }
        return args;
    }

    private static string BuildGeckoArgs(string url, string profileId)
    {
        string appProfileDir = GetAppWindowProfileDirectory(profileId);
        Directory.CreateDirectory(appProfileDir);
        EnsureGeckoAppWindowProfile(appProfileDir);
        return $"--new-window \"{url}\" --profile \"{appProfileDir}\" --no-remote";
    }

    private static void EnsureGeckoAppWindowProfile(string profileDir)
    {
        string chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);

        string userChromePath = Path.Combine(chromeDir, "userChrome.css");
        if (!File.Exists(userChromePath))
        {
            File.WriteAllText(userChromePath,
                "@namespace url(\"http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul\"); #navigator-toolbox { visibility: collapse !important; }");
        }

        string userJsPath = Path.Combine(profileDir, "user.js");
        if (!File.Exists(userJsPath))
        {
            File.WriteAllText(userJsPath,
                "user_pref(\"toolkit.legacyUserProfileCustomizations.stylesheets\", true);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n");
        }
    }

    private static string ResolveBrowserExe(string browserPath)
    {
        if (!string.IsNullOrEmpty(browserPath))
            return File.Exists(browserPath) ? browserPath : "";
        return BrowserCatalog.GetDefaultBrowserExePath() ?? "";
    }

    private static string GetAppWindowProfileId(string url)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string GetAppWindowProfileDirectory(string profileId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "AppWindowProfiles", profileId);
    }

    private static string GetBoundsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LittleLauncher", "edge-window-bounds.json");
    }

    private static async Task RestoreAndTrackWindowBoundsAsync(HashSet<IntPtr> existingWindows, string profileId, BrowserEngine engine)
    {
        IntPtr hwnd = await WaitForNewBrowserWindowAsync(existingWindows, engine, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero) return;

        if (TryGetSavedBounds(profileId, out var savedBounds))
        {
            SetWindowPos(hwnd, 0, savedBounds.Left, savedBounds.Top, savedBounds.Width, savedBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (savedBounds.IsMaximized) ShowWindow(hwnd, SW_MAXIMIZE);
        }

        InstallBoundsTrackingHooks(hwnd, profileId);
    }

    private static readonly HashSet<WinEventProc> ActiveHookDelegates = new();

    private static void InstallBoundsTrackingHooks(IntPtr hwnd, string profileId)
    {
        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        WindowBounds? lastBounds = null;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookDestroy = IntPtr.Zero;
        WinEventProc? handler = null;

        handler = (hHook, eventType, eventHwnd, idObject, idChild, eventThread, time) =>
        {
            if (eventHwnd != hwnd || idObject != OBJID_WINDOW) return;
            if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    bool maximized = IsZoomed(hwnd);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w >= 320 && h >= 240)
                        lastBounds = new WindowBounds(rect.Left, rect.Top, w, h, maximized);
                }
            }
            else if (eventType == EVENT_OBJECT_DESTROY)
            {
                if (lastBounds is not null) SaveBounds(profileId, lastBounds);
                if (hookLocation != IntPtr.Zero) UnhookWinEvent(hookLocation);
                if (hookDestroy != IntPtr.Zero) UnhookWinEvent(hookDestroy);
                lock (ActiveHookDelegates) ActiveHookDelegates.Remove(handler!);
            }
        };

        lock (ActiveHookDelegates) ActiveHookDelegates.Add(handler);

        hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
        hookDestroy = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, handler, processId, threadId, WINEVENT_OUTOFCONTEXT);
    }

    private static async Task<IntPtr> WaitForNewBrowserWindowAsync(HashSet<IntPtr> existingWindows, BrowserEngine engine, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var currentWindows = GetBrowserWindows(engine);
            foreach (var hwnd in currentWindows)
            {
                if (existingWindows.Contains(hwnd)) continue;
                if (!GetWindowRect(hwnd, out RECT rect)) continue;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 200 && height >= 120) return hwnd;
            }
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static readonly string[] ChromiumWindowClasses = { "Chrome_WidgetWin_1" };
    private static readonly string[] GeckoWindowClasses = { "MozillaWindowClass", "MozillaDialogClass" };

    private static HashSet<IntPtr> GetBrowserWindows(BrowserEngine engine)
    {
        var windowClasses = engine == BrowserEngine.Gecko ? GeckoWindowClasses : ChromiumWindowClasses;
        var windows = new HashSet<IntPtr>();
        var className = new StringBuilder(256);
        EnumWindows((hWnd, _) =>
        {
            className.Clear();
            GetClassName(hWnd, className, className.Capacity);
            string cls = className.ToString();
            foreach (string target in windowClasses)
            {
                if (cls == target) { windows.Add(hWnd); break; }
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryGetSavedBounds(string profileId, out WindowBounds bounds)
    {
        bounds = default!;
        lock (BoundsFileLock)
        {
            if (CachedBounds.TryGetValue(profileId, out var cachedBounds)) { bounds = cachedBounds; return true; }
            var all = LoadAllBounds();
            foreach (var kv in all) CachedBounds[kv.Key] = kv.Value;
            if (CachedBounds.TryGetValue(profileId, out var loadedBounds)) { bounds = loadedBounds; return true; }
            return false;
        }
    }

    private static void SaveBounds(string profileId, WindowBounds bounds)
    {
        lock (BoundsFileLock)
        {
            CachedBounds[profileId] = bounds;
            var all = LoadAllBounds();
            all[profileId] = bounds;
            string filePath = GetBoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    private static Dictionary<string, WindowBounds> LoadAllBounds()
    {
        string filePath = GetBoundsFilePath();
        if (!File.Exists(filePath)) return new Dictionary<string, WindowBounds>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(json) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private sealed record WindowBounds(int Left, int Top, int Width, int Height, bool IsMaximized = false);

    /// <summary>
    /// Enters edit mode. Launcher settings are reached from the edit toolbar rather than
    /// from here, so there is a single obvious entry point into editing.
    /// </summary>
    private double GetWorkAreaHeightDips()
    {
        var pt = new POINT();
        if (_setupEditBounds is { } setupBounds)
        {
            var (x, y) = GetSetupAnchorPoint(setupBounds);
            pt = new POINT { X = x, Y = y };
        }
        else
        {
            GetCursorPos(out pt);
        }
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiY, out _);
        double scale = dpiY / 96.0;
        if (scale <= 0) scale = 1.0;

        var monitorInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMonitor, ref monitorInfo);
        int workAreaHeightPx = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        // Keep room above the setup session for its floating toolbar, even after growth.
        double toolbarHeight = _setupEditBounds.HasValue ? SetupToolbarClearanceDips : 0;
        return (workAreaHeightPx / scale) - toolbarHeight;
    }

}
