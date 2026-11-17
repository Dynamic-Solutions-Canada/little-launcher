using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Models;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>Toast notifications: registration, activation and the one-time and update notices.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The Action Center group the app's own notices are filed under.
    /// </summary>
    /// <remarks>
    /// Separate from the per-launcher groups a web launcher's page notifications use, so a
    /// launcher clearing its backlog when it is opened leaves an unread update notice alone.
    /// Each notice also carries a fixed tag, so a repeat of the same notice replaces the earlier
    /// one instead of adding to a backlog nobody will clear by hand.
    /// </remarks>
    private const string AppNoticeGroup = "littlelaunchernotices";

    /// <summary>
    /// Registers for Windows notifications and routes a click back into the app.
    /// </summary>
    /// <remarks>
    /// Packaged builds used to skip this entirely. That was never a platform limitation: a
    /// packaged app needs a COM activator declared in its manifest so a clicked toast has
    /// somewhere to go, and without one <c>Register()</c> can throw. The MSIX manifest now
    /// declares one (<c>windows.toastNotificationActivation</c> plus its <c>com:ExeServer</c>),
    /// so both install types register identically. The CLSID there must stay stable — change it
    /// and toasts already sitting in the Action Center lose their activator.
    /// </remarks>
    private void RegisterForNotifications()
    {
        try
        {
            var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Toast notification registration failed");
        }
    }

    /// <summary>
    /// Opens the app when a notification is clicked.
    /// </summary>
    /// <remarks>
    /// A toast raised by a web launcher's page carries the launcher it came from and is routed
    /// back to that flyout, which is the only place the click means anything —
    /// <see cref="WebFlyoutWindow.HandleNotificationActivation"/> also tells the page it was
    /// clicked, so its <c>onclick</c> handler runs as it would in a browser.
    /// <para>Everything else lands on the Home page, deliberately unrouted: the app's own notices
    /// are also showing there as a banner, so the click arrives at the same action the toast was
    /// offering with no extra plumbing to keep in sync.</para>
    /// </remarks>
    private void OnNotificationInvoked(
        Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;

        // Whatever the user typed into the toast's reply box, if it had one. Read here rather than
        // in the flyout: the args do not outlive this callback.
        string reply = args.UserInput.TryGetValue("llReply", out string? typed) ? typed ?? "" : "";

        DispatcherQueue.TryEnqueue(() =>
        {
            if (WebFlyoutWindow.HandleNotificationActivation(arguments, reply))
            {
                return;
            }

            SettingsWindow.ShowInstance(this);
        });
    }

    /// <summary>
    /// Announces a just-detected upgrade as a toast, once.
    /// </summary>
    /// <remarks>
    /// The Home page banner only reaches someone who opens the app, and Store updates install
    /// silently — so for Store users this is the only notice that arrives on its own. Gated on
    /// the transient <c>UpgradeNoticesJustRaised</c>, not the persisted flag, which stays set
    /// until the banner is dismissed and would otherwise re-toast on every launch.
    /// </remarks>
    private void ShowUpgradeNoticeToast()
    {
        if (!SettingsManager.Current.UpgradeNoticesJustRaised)
        {
            return;
        }

        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(UiText.Get("home.whatsNew.title"))
                .AddText(UiText.Get("tray.whatsNew.message"));
            builder.SetTag("upgrade");
            builder.SetGroup(AppNoticeGroup);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Upgrade notice toast failed");
        }
    }

    /// <summary>The startup check's result, cached so the Home page does not check again.</summary>
    internal static WinUIAppKit.Updates.UpdateCheckResult? StartupUpdateResult { get; private set; }

    /// <summary>
    /// Checks GitHub releases on startup for portable builds and shows a toast when newer.
    /// The result is cached for the Home page to consume.
    /// </summary>
    /// <remarks>
    /// A documented exception to DIST-6, which otherwise forbids background update checks: it runs
    /// only in unpackaged (retired portable) copies, and is how their users learn that updates
    /// moved to the Microsoft Store, since the newest Release points there. Packaged installs
    /// return at once and leave updates to the Store.
    /// </remarks>
    private async Task CheckForUpdateOnStartupAsync()
    {
        if (IsPackaged)
        {
            return;
        }

        try
        {
            var result = await Classes.AboutContent.Updates.CheckGitHubAsync();
            StartupUpdateResult = result;
            if (result.Outcome != WinUIAppKit.Updates.UpdateCheckOutcome.UpdateAvailable)
            {
                return;
            }

            // Tagged, because this one fires on every launch until the update is actually taken.
            // Untagged, a user who ignores it collects one more undismissed toast per start, all
            // saying the same thing — and Windows 11 watches how often an app's notifications go
            // un-interacted-with before offering to switch that app's notifications off outright.
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(UiText.Get("about.update.availableTitle"))
                .AddText(UiText.Format("tray.update.message",
                    ("latest", result.LatestVersion),
                    ("current", result.CurrentVersion)));
            builder.SetTag("update");
            builder.SetGroup(AppNoticeGroup);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Startup update check failed");
        }
    }
}
