using LittleLauncher.Models;
using LittleLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;

namespace LittleLauncher.Windows;

/// <summary>Attaches inspection and launch actions to recycled app-picker row containers.</summary>
internal static class AppPickerRowActions
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Configures the entire row for hover, right-click and the keyboard context-menu gesture.
    /// Arguments are read at click time so changes in the editor are respected. Neither action
    /// changes the picker selection or saves an item.
    /// </summary>
    internal static void Configure(FrameworkElement container, AppPickerEntry entry, Func<string> getArguments)
    {
        ToolTipService.SetToolTip(container, entry.LaunchTarget);

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem
        {
            Text = (string)Application.Current.Resources["AppPickerOpen"],
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        open.Click += (_, _) => FlyoutWindow.Launch(new LauncherItem
        {
            Name = entry.Name,
            Path = entry.LaunchPath,
            IsPwa = entry.IsPwa,
            Arguments = getArguments()
        });
        menu.Items.Add(open);

        var folder = entry.ContainingFolder;
        var openFolder = new MenuFlyoutItem
        {
            Text = (string)Application.Current.Resources["AppPickerOpenContainingFolder"],
            Icon = new SymbolIcon(Symbol.Folder),
            IsEnabled = folder != null && Directory.Exists(folder)
        };
        openFolder.Click += (_, _) => OpenContainingFolder(folder);
        menu.Items.Add(openFolder);
        container.ContextFlyout = menu;
    }

    /// <summary>Opens an existing filesystem parent through the shell; failures are logged.</summary>
    private static void OpenContainingFolder(string? folder)
    {
        if (folder == null) return;

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not open app-picker containing folder: {0}", folder);
        }
    }
}
