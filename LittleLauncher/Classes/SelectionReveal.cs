using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LittleLauncher.Classes;

/// <summary>Reveals a restored selection after layout, without allowing old requests to move a newer view.</summary>
internal static class SelectionReveal
{
    /// <summary>
    /// Schedules one reveal after this host has a live viewport and a subsequent layout pass.
    /// The returned action cancels both the layout subscription and any queued work. The caller's
    /// current-request predicate must also reject a changed source, tab, query, selection or close.
    /// </summary>
    internal static Action AfterLayout(FrameworkElement host, Func<bool> isCurrent, Action reveal)
    {
        bool cancelled = false;
        EventHandler<object>? layoutUpdated = null;

        void Cancel()
        {
            cancelled = true;
            host.LayoutUpdated -= layoutUpdated;
        }

        layoutUpdated = (_, _) =>
        {
            if (cancelled || !isCurrent())
            {
                Cancel();
                return;
            }
            if (!host.IsLoaded || host.ActualWidth <= 0 || host.ActualHeight <= 0)
            {
                return;
            }

            host.LayoutUpdated -= layoutUpdated;
            host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (cancelled || !isCurrent() || !host.IsLoaded)
                {
                    return;
                }
                try
                {
                    reveal();
                }
                catch (Exception ex)
                {
                    NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Revealing the restored icon selection failed");
                }
            });
        };
        host.LayoutUpdated += layoutUpdated;
        host.InvalidateMeasure();
        return Cancel;
    }
}
