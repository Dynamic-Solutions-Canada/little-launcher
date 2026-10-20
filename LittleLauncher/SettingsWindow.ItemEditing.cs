using LittleLauncher.Windows;
using System;
using System.Threading.Tasks;

namespace LittleLauncher;

public sealed partial class SettingsWindow
{
    /// <summary>
    /// Opens the shared item editor in a normal owned window. Repeated entry focuses the
    /// existing editor for this launcher; Settings navigation and its original view stay intact.
    /// </summary>
    internal async Task ShowItemEditor(MainWindow owner, string launcherId,
        global::Windows.Graphics.RectInt32? setupBounds = null)
    {
        try
        {
            await LauncherItemsEditorWindow.ShowAsync(owner, launcherId, this, setupBounds);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not open the launcher item editor.");
        }
    }
}
