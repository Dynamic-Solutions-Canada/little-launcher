using LittleLauncher.Classes;
using LittleLauncher.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

namespace LittleLauncher.Services;

/// <summary>
/// Helpers for the unified add/edit-item app picker: runs the (apartment-threaded,
/// expensive) catalog enumeration off the UI thread, and streams shell icons into
/// the <see cref="AppPickerEntry"/> rows after the list is shown.
/// </summary>
public static class AppPickerService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Runs <paramref name="func"/> on a dedicated STA thread. The catalog build
    /// uses <c>Shell.Application</c>, which is apartment-threaded, so it must not run
    /// on a pooled MTA thread.
    /// </summary>
    public static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { Logger.Warn(ex, "STA app-picker work failed"); tcs.SetResult(default!); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Extracts each entry's icon on a background STA thread and assigns it on the UI
    /// thread, so the list appears instantly and icons stream in. Entries already
    /// carrying an icon are skipped (safe to call again after re-filtering).
    /// </summary>
    public static void LoadIcons(IReadOnlyList<AppPickerEntry> entries, DispatcherQueue dispatcher)
    {
        var t = new Thread(() =>
        {
            foreach (var entry in entries)
            {
                if (entry.Icon != null)
                {
                    continue;
                }

                ShellIcons.IconPixels? px;
                try { px = ShellIcons.Extract(entry.IconTarget, 32); }
                catch { px = null; }
                if (px == null)
                {
                    continue;
                }

                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        var wb = new WriteableBitmap(px.Width, px.Height);
                        using var s = wb.PixelBuffer.AsStream();
                        s.Write(px.Bgra, 0, px.Bgra.Length);
                        entry.Icon = wb;
                    }
                    catch { /* skip this icon */ }
                });
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }
}
