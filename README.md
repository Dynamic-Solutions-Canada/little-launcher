<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" alt="Little Launcher" width="128">
</p>

<h1 align="center">Little Launcher</h1>

<p align="center">
  A Windows launcher with system-tray and taskbar icon support, settings-sync capability, built with WinUI 3, Windows App SDK, WebView2, and SSH.NET.
</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9p3zzbdq6pjf">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download Little Launcher from the Microsoft Store" height="56">
  </a>
</p>

<p align="center">
  <img src="LittleLauncher/Resources/AppIcons/Blue.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Green.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Teal.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Red.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Orange.png" width="48">&nbsp;
  <img src="LittleLauncher/Resources/AppIcons/Purple.png" width="48">
</p>

<p align="center">
  <img src="docs/icon-launcher.png" alt="Flyout — icon view" width="300">
  &nbsp;&nbsp;
  <img src="docs/list-launcher.png" alt="Flyout — list view" width="300">
</p>

<p align="center">
  <img src="docs/web-launcher.png" alt="Web launcher — a Home Assistant dashboard in the flyout" width="270">
  &nbsp;
  <img src="docs/web-launcher-cameras.png" alt="Web launcher — camera feeds in the flyout" width="270">
  &nbsp;
  <img src="docs/web-launcher-video.png" alt="Web launcher — a video site in the flyout" width="270">
</p>

<p align="center">
  <img src="docs/icon-launcher-multiple-cols.png" alt="Flyout — icon view with multiple columns" width="720">
</p>

<p align="center">
  <img src="docs/list-launcher-multiple-cols.png" alt="Flyout — list view with multiple columns" width="720">
</p>

<p align="center">
  <img src="docs/launchers.png" alt="Launchers page" width="720">
</p>

<p align="center">
  <img src="docs/icon-launcher-settings.png" alt="Launcher settings dialog" width="720">
</p>

<p align="center">
  <img src="docs/icon-launcher-items.png" alt="Launcher items — icon mode" width="720">
</p>

<p align="center">
  <img src="docs/list-launcher-items.png" alt="Launcher items — list mode" width="720">
</p>

## Overview

Little Launcher lives in the Windows system tray and/or taskbar. Clicking its icon opens a flyout — either a grid of app and website shortcuts, or a web page in its own right. It also synchronises your settings — through OneDrive, Google Drive, WebDAV, a network file share, another folder, or your own SSH/SFTP server — so you can keep your launcher configuration in sync across machines.

**Key features:**

- **Multiple launchers**; define multiple named launchers, each with its own icon and items.
- **Web launchers**; point a launcher at a web page instead of a list of shortcuts, and its tray icon opens that page itself: a Home Assistant dashboard, webmail, Teams, WhatsApp, a status board, one click away. Each keeps its own sign-in between restarts, so two launchers can be signed in to the same site as different people; or share one profile if you would rather sign in once.
- **A bar of pages**; a web launcher holds a list of bookmarks, the first of which is the page it opens. Add a second and they appear as a browser-style bar along the bottom. Clicking one opens it just as a browser's bookmarks bar would; middle-click or Shift-click opens it in a new tab.
- **Bookmark the page you are looking at**; with the address bar showing, a star at the end of it adds the current page to the launcher's bookmark bar, or takes it back out; the **…** menu carries the same action when the address bar is off. Right-click a bookmark in the bar to open it in a new tab, rename it, change its address, copy it, make it the one that opens by default, or remove it; and drag bookmarks along the bar to reorder them. Right-click the bar's empty space for **Icons only**, which drops every label at once when the sites are familiar enough to go by their logos.
- **Costs nothing while you are not looking at it**; a dismissed web launcher stops rendering, is suspended, and is then unloaded entirely, so a hidden dashboard uses no CPU, network or memory. Launchers you want to keep receiving are the exception, and they say so.
- **Real Windows notifications, with buttons and replies**; a web launcher's notifications arrive as ordinary Windows notifications rather than being lost inside a page nobody is looking at. Action buttons work, and a site that offers a reply box gives you one in the notification itself; typing a reply there does exactly what typing it in the app does. Clicking a notification opens the launcher it came from; and a notification goes away once you have dealt with it, whether you clicked it, opened the launcher some other way, or read the message on your phone. One busy launcher cannot bury every other launcher's notifications under a pile nobody ever cleared.
- **Set up once, not every morning**; launchers set to keep running are opened quietly in the background when Little Launcher starts, so their notifications work from the moment you sign in, without you clicking each tray icon to wake it up.
- **Site permissions asked in the flyout**; a page that wants your camera, microphone or location asks in a bar inside the flyout rather than a browser-sized prompt, and the answer is remembered for that launcher alone. A launcher you trust can skip the asking entirely.
- **Sized and placed how you want it**; drag the edges to resize, pin it open, anchor it to a corner or edge instead of the tray, set a zoom level, or lock a size in so a stray drag cannot change it. The header's maximize fills the screen for as long as the flyout is open, then goes back to its usual size next time (double-clicking the header, the tab strip or the bookmark bar does the same thing, as it would on any other window), and resting on that button instead offers a little grid of places to send the window, the way Windows' own snap layouts do.
- **Whichever one you clicked is the one on top**; several launchers can be kept above everything else at once, and the one you move to comes to the front of them rather than staying buried under a launcher you have not touched in an hour.
- **Or an ordinary window, if you would rather**; turn on **Regular Window** and a web launcher stops behaving like a flyout: it gets a taskbar button that lights up while it is open, an Alt-Tab entry, and it stays put when you click elsewhere. Clicking its taskbar button minimises or closes it, whichever you prefer, and it can still be told to get out of the way on focus loss if you want both. The header gains a minimise button to match, and the taskbar button can be pinned like any other app's, straight from its own right-click menu.
- **Open them from Start, or Command Palette**; every web launcher gets a Start Menu entry, so you can reach it the same way you reach any other app: Start search, PowerToys Command Palette, or anything else that reads the Start Menu. They keep themselves in step as you add, rename and remove launchers.
- **Links open in tabs**; a link that wants a new window opens as another tab of the launcher rather than throwing you out into your browser, and a tab strip appears as soon as there is more than one. Sign-in popups work properly inside the flyout, tabs remember where you were across a dismissal, and if you would rather links just went to your browser, one menu item puts that back.
- **An address bar when you want one**; a web launcher can show the page address under its header, and type a new one. Off by default, because most launchers open one known page.
- **Settings grouped by purpose**: General, Appearance, Size and position, Behavior, and Privacy and sign-ins match between launcher settings and the flyout's **…** menu. Quick controls change bars, zoom, position, size memory, and window behavior immediately; section links open the full settings for addresses, icons, dimensions, and sign-in profiles. Expanding a section brings its heading to the top so its controls are ready to read.
- **Fullscreen videos inside the launcher**: keep page fullscreen inside a resizable launcher and optionally fit the window to the video's proportions from **… > Behavior**.
- **Fullscreen window controls**: hover at the top edge to exit fullscreen or close, maximize and restore the contained window's size, or drag to reposition it. Display fullscreen stays fixed in place. Reopening preserves fullscreen while the page remains loaded.
- **Fullscreen video button**: the header offers fullscreen when it detects a visible video. Embedded players may still need their own fullscreen control.
- **Notification positioning**: clicking a notification opens its launcher beside its tray icon, while respecting the launcher's configured placement.
- **Messaging presets**: choose WhatsApp, Google Messages, Messenger, Discord, or Teams from **Add Launcher** to open a ready-to-sign-in web launcher. These stay active while hidden for incoming messages; notification permissions are still yours to choose.
- **Application & website shortcuts**; launch any executable or URL with one click from the flyout.
- **Identify apps before adding them**: same-named apps show their executable filenames. Hover to see the full target, or right-click to open the app or its containing folder.
- **Manual icon picker**: choose launcher and item icons from Fluent glyphs, full-color emoji, bundled app icons, custom images, or the selfh.st catalog. Large emoji and Fluent catalogs create tiles as you scroll. Browse is in the first options tab, confirmation stays visible, and reopening reveals the saved choice. Web launchers can preview their saved automatic page icon, retry it from the page or directly from its site, and return to it from the same chooser. Confirm uses the image shown in the preview, even when it is smaller than the saved icon. If the site requires sign-in, the chooser explains how to retry with the launcher open.
- **View modes**: choose between list view, icon grid view, or a compact tray-sized small-icon grid with no labels. Hover an item to see its full name when the label is hidden or cut off.
- **Edit launcher items in their own window**: click **Items** in Settings to open a matching editor window. Wide or tall launchers keep their layout and scroll as needed. Add actions wrap when needed, with a compact settings icon at the top right. Changes save as you edit; **Done** closes the editor. The separate **…** button keeps import, export and bookmark import close at hand.
- **Keep setup in view**: a new shortcut launcher’s item editor opens where you just configured it, on the same monitor. Click **Done** to finish; normal tray or taskbar openings keep their usual placement.
- **Groups & columns**; organise items into groups and multi-column layouts.
- **Direct flyout reordering**; drag items or whole groups in the live flyout or regular editor. Hover marks the whole group, and a compact drag card fits the current view mode with the destination hint above it. Drop inside a tinted group to add a child, or use its outer edge to place an item before or after the whole group; the last item fading toward an overlapping inset marker distinguishes the end of a group from the full-width marker after it. Click a group's hover pencil to rename it; finishing tray editing after switching apps restores normal click-away dismissal.
- **Drag in from anywhere**; while editing a launcher, drag files, folders, shortcuts or browser links straight from File Explorer or the desktop into the flyout to add them, complete with names and icons. (Windows' own Start Menu can't act as a drag source, but its shortcuts can be dragged from `Start Menu\Programs` in Explorer.)
- **Direct flyout item actions**; right-click items in the live flyout to move, edit, or remove them without opening the settings editor.
- **System-tray icons**; a tray icon that opens a flyout popup for shortcuts.
- **Taskbar icons**: pin each launcher through Windows confirmation on supported systems, with manual pinning available elsewhere. Settings warns when a pinned shortcut's name, icon or launch details need refreshing. Pinning checks whether the old pin still exists and keeps an unchanged pin.
- **Settings sync**; keep all your launchers in sync across machines through **OneDrive**, **Google Drive**, **WebDAV** (Nextcloud, ownCloud, a NAS), a **network file share**, any other folder, or your own **SSH/SFTP** server. OneDrive and Google Drive sign in through your browser and use their own APIs, so no sync client has to be installed and an upload is confirmed by the service rather than handed to a background app. Each is granted only a private folder of its own; nothing else in your drive is visible to Little Launcher.
- **Shared launchers**; share a single launcher with someone else: create a **OneDrive link** to send them, publish it to a **WebDAV** server, put it on **SFTP**, or drop it in a shared folder. The owner chooses whether subscribers can edit it or only receive it, and that choice travels with the share rather than being something the subscriber has to guess.
- **Export & import**; back up and restore items locally via JSON.
- **Bookmark import**; import bookmarks directly from Chrome, Edge, Firefox, or any browser's exported HTML bookmarks file into a launcher.
- **Pick a web address from your bookmarks**; a web launcher's address, and the bookmarks in its bar, can be chosen from a searchable list of what you have already bookmarked in Chrome, Edge or Firefox, rather than typed from memory.
- **Update paths per install type**; Microsoft Store installs check for and apply updates through the Store from inside the app; the portable build checks GitHub Releases and takes you to the new release to download it.

## Install

**Microsoft Store (recommended):** [Get Little Launcher from the Microsoft Store](https://apps.microsoft.com/detail/9p3zzbdq6pjf) — installs and updates automatically through the Store.

**Portable:** Grab the portable ZIP for your architecture (x64 or ARM64) from the [Releases page](https://github.com/RyanEwen/LittleLauncher/releases/latest) and unzip it wherever you like — nothing is installed and no admin rights are needed. It tells you when a new version is out and links you to it; you replace the folder yourself. To remove it, run `cleanup-uninstall.ps1` from the folder with the app closed (it clears settings, the startup entry and any shortcuts), then delete the folder.

Little Launcher used to ship an MSI installer as well. It has been retired in favour of these two: the Store handles installing, updating and uninstalling properly, and the portable build is for anyone who would rather not use the Store. Older releases keep their `.msi` files, and an existing MSI install keeps working — it will simply point you here when a new version appears.

**Requirements:** Windows 10 or 11 (build 22000 or later).

## Building from source

Little Launcher is a .NET 10 WinUI 3 app. See **[DEVELOPMENT.md](DEVELOPMENT.md)** for build prerequisites, the architecture overview, and the project layout.

Launcher editing has a [cross-platform policy regression suite](tests/LauncherItemReorder.Tests/README.md) and a guarded [native Windows drag suite](tests/native-drag/README.md) for a dedicated test desktop.

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): free for any
**personal and other noncommercial use**, including modifying and redistributing it.
**Commercial use is not permitted.** Copyright © 2024-2026 Ryan Ewen.
