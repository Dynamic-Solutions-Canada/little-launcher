<p align="center">
  <img src="assets/generated/icon-256.png" alt="Little Launcher" width="128">
</p>

<h1 align="center">Little Launcher</h1>

<p align="center">
  A tray launcher for apps and websites, with settings sync.
</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9p3zzbdq6pjf">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download Little Launcher from the Microsoft Store" height="56">
  </a>
</p>

<p align="center">
  <img src="store-assets/web/screenshots/01-icon-flyout-dark.png" alt="A launcher's flyout in icon view, opened from the taskbar" width="250">
  &nbsp;
  <img src="store-assets/web/screenshots/04-list-flyout-columns-dark.png" alt="A launcher's flyout in list view, with groups in several columns" width="250">
  &nbsp;
  <img src="store-assets/web/screenshots/05-launchers-page-dark.png" alt="The Launchers page in Settings" width="250">
</p>

<p align="center">
  <img src="store-assets/web/screenshots/09-web-launcher-dashboard-dark.png" alt="Web launcher: a Home Assistant dashboard in the flyout" width="250">
  &nbsp;
  <img src="store-assets/web/screenshots/10-web-launcher-cameras-dark.png" alt="Web launcher: camera feeds in the flyout" width="250">
  &nbsp;
  <img src="store-assets/web/screenshots/11-web-launcher-video-dark.png" alt="Web launcher: a video site in the flyout" width="250">
</p>

Little Launcher puts named launchers in your system tray. Click one and a flyout opens with your app and website shortcuts. A launcher can also open a web page of its own, such as a dashboard, webmail or a chat app, already signed in. It is for anyone who reaches for the same handful of apps and sites every day and wants them one click away on every PC. For Windows and Linux.

## Features

- **Several launchers:** each has its own tray icon, name, icon and items, in a list, an icon grid or a compact small-icon grid, with groups and side-by-side columns.
- **Web launchers:** point a launcher at a web page and its tray icon opens that page in a flyout, with tabs, a bookmark bar, its own sign-in, real notifications and site permissions asked inside the flyout. Hidden pages are suspended so they cost nothing while you are not looking.
- **Edit in place:** drag items and groups to reorder them, drag files, folders and links in from anywhere, and right-click to move, edit or remove.
- **Pick any icon:** Fluent glyphs, emoji, bundled app icons, the selfh.st catalog or your own images.
- **Sync between devices:** keep launchers in step across PCs through OneDrive, Google Drive, WebDAV, a shared folder or your own SSH/SFTP server, and between Windows and Linux through OneDrive.
- **Share one launcher:** send someone a OneDrive link, or publish it to WebDAV, SFTP or a shared folder, and choose whether they can edit it or only receive it.
- **Bring your bookmarks:** import from Chrome, Edge, Firefox or an exported bookmarks file.
- **Fits Windows:** pin launchers to the taskbar, open web launchers from Start, and get Windows notifications with buttons and replies.

The [full feature list](docs/features.md) covers every option.

## Get it

### Windows

**Microsoft Store (recommended):** [Get Little Launcher from the Microsoft Store](https://apps.microsoft.com/detail/9p3zzbdq6pjf). It installs and updates through the Store. US$0.99, with a free trial.

**Have a portable copy?** Versions up to 1.43.0 also came as a portable ZIP, and yours keeps working. To remove it, close the app, run `cleanup-uninstall.ps1` from its folder, then delete the folder.

**Requirements:** Windows 11.

### Linux

Download the **x64 AppImage** from the [Releases page](https://github.com/Dynamic-Solutions-Canada/little-launcher/releases/latest), make it executable, and run it. It targets **Debian 12 / Ubuntu 24.04 LTS or newer**. It is built for KDE Plasma, and works on GNOME with a tray extension.

```bash
chmod +x LittleLauncher-VERSION-linux-x64.AppImage
./LittleLauncher-VERSION-linux-x64.AppImage
```

Replace `VERSION` with the release number, and keep the AppImage where you want it to stay. To remove it, quit the app, then delete the AppImage and the files it wrote under your home folder; [the uninstall guide](docs/installer.md#uninstall-cleanup) lists them.

Little Launcher collects no telemetry or crash reports.

## Languages

Available in English, Spanish, French, German, Simplified Chinese, Brazilian Portuguese and Japanese. On both Windows and Linux, Little Launcher follows the system language unless you pick one in Settings. Translations are machine-generated.

## Linux desktop support

The Linux app has the same launchers, flyouts and in-place editing, plus web launchers with tabs, bookmarks and browser extensions. Sync on Linux uses OneDrive: web launchers are shared between Windows and Linux, and shortcut launchers stay with the system that created them. To share a launcher with someone else, use the Windows app. AppImage updates keep your settings and sign-ins. The [Linux guide](linux/README.md) has the details.

## Developers

- [Development guide](docs/development.md): prerequisites, the Windows build, the tech stack and the project layout.
- [Linux guide](linux/README.md): Linux development, isolated verification and AppImage builds.
- [UI parity review](docs/linux-ui-parity.md): how the Linux interface differs from Windows.
- [Documentation index](docs/README.md): architecture, implementation guides and design decisions.
- [Agent instructions](AGENTS.md): conventions and the guidance map.

## License

Copyright (c) 2024-2026 Dynamic Solutions. Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE), free for personal and other noncommercial use.
