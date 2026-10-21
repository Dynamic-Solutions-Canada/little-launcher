using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
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
using LittleLauncher.Windows;

namespace LittleLauncher.Controls;

/// <summary>
/// The shared launcher surface: templates, exact item geometry, editing and drag/drop.
/// Hosts own window policy and completion chrome; item changes retain immediate persistence.
/// </summary>
public sealed partial class LauncherItemsView : UserControl, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HashSet<LauncherItemsView> Instances = [];
    private readonly ILauncherItemsHost _host;
    private readonly Launcher _launcher;
    private bool _disposed;
    private int _sessionVersion;
    private int _lastItemsHash;
    private int _listColumnWidth = MaxListColumnWidth;
    private LauncherItem? _dragItem;
    private ObservableCollection<LauncherItem>? _dragSourceCollection;
    private ResizeGrip _leftResizeGrip = null!;
    private ResizeGrip _rightResizeGrip = null!;
    private bool _isResizingIconWidth;
    private bool _resizeFromLeft;
    private bool _resizeChangedSetting;
    private double _resizeStartCursorX;
    private int _resizeStartIconsPerRow;
    private List<ObservableCollection<LauncherItem>> _columnLists = [];
    private readonly HashSet<ListView> _loadedIconChildLists = [];
    private readonly HashSet<FrameworkElement> _loadedIconGroupRoots = [];
    private readonly HashSet<LauncherItem> _syntheticGroups = [];

    internal LauncherItemsView(Launcher launcher, ILauncherItemsHost host)
    {
        _launcher = launcher;
        _host = host;
        InitializeComponent();
        InitializeResizeGrips();
        RootGrid.Loaded += RootGrid_Loaded;
        RebuildColumnsPanel();
        InitializeEmptyPlaceholder();
        InitializeEditChrome();
        _lastItemsHash = ComputeItemsHash(launcher);
        Instances.Add(this);
    }

    internal bool IsEditMode => _isEditMode;
    internal bool IsResizing => _isResizingIconWidth;
    internal int ContentWidth => GetFlyoutWidth();
    internal double ContentHeight => MeasureContentHeight();
    internal double MinimumContentHeight => GetMinimumFlyoutHeight();
    internal FrameworkElement? Toolbar => _editToolbarHost;
    internal (int Width, int Height) ToolbarSize => EditToolbarSizeDips;

    /// <summary>Rebinds changed model state, then lets the host update its own extent.</summary>
    internal void Refresh(bool force = false)
    {
        if (_disposed) return;
        if (force) _lastItemsHash = -1;
        RebuildItemsIfNeeded();
        ResizeIfVisible();
    }

    /// <summary>Reaches every host of a launcher after imports, icon changes or sync.</summary>
    internal static void InvalidateItems(string? launcherId, bool force)
    {
        foreach (var view in Instances.ToArray())
        {
            if (launcherId == null || view._launcher.Id == launcherId)
                view.Refresh(force);
        }
    }

    /// <summary>Releases handlers and invalidates pending session work before its host closes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionVersion++;
        Instances.Remove(this);
        RootGrid.Loaded -= RootGrid_Loaded;
        CompleteIconResize(_resizeFromLeft ? _leftResizeGrip : _rightResizeGrip);
        ClearHoverWiring();
        ClearInsertionIndicator();
        ClearDragState();
        _loadedIconChildLists.Clear();
        _loadedIconGroupRoots.Clear();
        _loadedGroupChildLists.Clear();
        _editStyledContainers.Clear();
    }

    private bool IsCurrentSession(int version) => !_disposed && version == _sessionVersion;
    private void ResizeIfVisible()
    {
        if (!_disposed) _host.ContentSizeChanged(keepRightEdge: false);
    }
    private void ResizeForEditChrome() => ResizeIfVisible();
    private Task<T> RunModalAsync<T>(Func<Action<Window>, Task<T>> show) => _host.RunModalAsync(show);
    private IntPtr _hwnd => _host.DialogOwnerHwnd;
    private int CurrentViewMode => LauncherViewModes.Normalize(_launcher.ViewMode);
    private bool IsListMode => CurrentViewMode == LauncherViewModes.List;
    private bool IsSmallIconMode => CurrentViewMode == LauncherViewModes.SmallIcon;
    private bool IsIconMode => LauncherViewModes.IsIconMode(CurrentViewMode);
    private bool IsReadOnlyLauncher => _launcher is { IsShared: true, IsSharedOwner: false };

    private static int ComputeItemsHash(Launcher launcher)
    {
        var items = launcher.Items;
        if (items == null || items.Count == 0) return 0;
        var hash = new HashCode();
        hash.Add(LauncherViewModes.Normalize(launcher.ViewMode));
        hash.Add(Launcher.ClampIconModeIconsPerRow(launcher.IconModeIconsPerRow));
        hash.Add(launcher.ShowTitle);
        hash.Add(launcher.Name);
        foreach (var item in items)
        {
            HashItem(ref hash, item);
            if (item.IsGroup)
            {
                foreach (var child in item.Children)
                    HashItem(ref hash, child);
            }
        }
        return hash.ToHashCode();
    }

    private static void HashItem(ref HashCode hash, LauncherItem item)
    {
        hash.Add(item.Name);
        hash.Add(item.Path);
        hash.Add(item.IconPath);
        hash.Add(item.IconGlyph);
        hash.Add(item.IconColor);
        hash.Add(item.IsWebsite);
        hash.Add(item.OpenInAppWindow);
        hash.Add(item.AppWindowBrowser);
        hash.Add(item.AppWindowBrowserProfile);
        hash.Add(item.IsGroup);
        hash.Add(item.IsPwa);
        hash.Add(item.IsColumnBreak);
    }

    /// <summary>
    /// Rebuilds derived columns only after render-relevant model values change.
    /// </summary>
    private void RebuildItemsIfNeeded()
    {
        int currentHash = ComputeItemsHash(_launcher);
        if (currentHash != _lastItemsHash)
        {
            _lastItemsHash = currentHash;
            RebuildColumnsPanel();
        }
    }

}
