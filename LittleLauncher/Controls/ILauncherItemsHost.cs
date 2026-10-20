using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;

namespace LittleLauncher.Controls;

/// <summary>
/// Window policy around the shared launcher surface. Rendering and mutations stay in the view;
/// native placement, owned dialogs and finishing the session belong to its current host.
/// </summary>
internal interface ILauncherItemsHost
{
    bool IsRegularWindow { get; }
    IntPtr DialogOwnerHwnd { get; }
    void ContentSizeChanged(bool keepRightEdge);
    void EditModeChanged();
    void FinishEditing();
    void LaunchItem(LauncherItem item);
    void OpenAppSettings();

    /// <summary>Starts a density gesture using coordinates stable for this host's moving origin.</summary>
    void BeginIconResize();
    double GetResizePointerX(PointerRoutedEventArgs args);

    /// <summary>Tracks an owned secondary dialog until it closes, including host teardown.</summary>
    Task<T> RunModalAsync<T>(Func<Action<Window>, Task<T>> show);
}
