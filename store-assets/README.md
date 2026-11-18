# Store assets

| Folder | Holds |
| --- | --- |
| `microsoft/listings.json`, `listings.md` | The Store listing text in every locale, and the claims it makes (see `listings.md`) |
| `manifest.json` | Every Store screenshot in listing order: its raw source, theme, crop, en-US caption and alt text (LIST-8) |
| `raw/microsoft/` | The raw captures: the test VM's whole desktop at 1:1, each with the `.json` sidecar the capture helper writes |
| `microsoft/screenshots/` | The listing screenshots, rendered from the raw captures |
| `web/screenshots/` | The README's screenshots; a separate set, never uploaded to the Store |

## Screenshots

Captured from the real installed app with synthetic settings, never from a personal machine (LIST-8). Every shot is in dark mode on the Windows 11 dark wallpaper (`img19.jpg`). The captions in `manifest.json` are the en-US `screenshotCaptions` in `listings.json`, in the same order and count; each other locale has its own translation there. A new or changed screenshot is approved by the maintainer before it is committed.

The synthetic settings hold themed launchers, each with its own coloured glyph tray icon: Everyday (Store apps), Admin tools, Creative (two groups), Web (two groups of sites) and a YouTube web launcher showing Big Buck Bunny (CC BY, on Blender's official channel). Shots 00 to 05 show a tray flyout or the taskbar and use the `tray` crop; 06 and 07 are Settings windows and use the default `window` crop.

1. **Prepare** the Windows 11 test VM with the windows-build-tools app-test helpers, from this repo's root: install the package, run `screenshot-desktop.ps1 -Mode Prepare` (dark mode, 1920 x 1080, the dark wallpaper, window effects on, notifications off), hide the desktop icons, tidy the taskbar and turn search and Widgets off. Set the guest clock to Eastern time and wait for the taskbar clock to update.
2. **Capture** in the desktop session: write the synthetic settings once, then restart the app between shots so state it saves (such as the web launcher's page icon) carries through. Open each flyout or window, click a neutral spot so no focus rectangle shows, and save the whole desktop with `Save-DesktopCapture`. Pinning a launcher to the taskbar raises a Windows prompt that UI Automation cannot see; answer it before the next shot. Copy each `.png` and `.json` into `raw/microsoft/` under the manifest's names, then end the visit with `app_test_clean`.
3. **Render** with `npm run shots:render` (winui-app-kit's renderer; its README has the rules): each capture is cropped to 1366 x 768, with nothing composited, shaved or scaled, and written without an alpha channel to `microsoft/screenshots/` (ASSET-9). Recaptioning needs no recapture; a new look does.
4. **Check** with `npm run check:screenshots` (part of `npm run validate`): every capture has its sidecar and was taken dark, at 100% scale, with effects on and the dark wallpaper; every screenshot was written by the renderer at exactly 1366 x 768; the captions match the en-US `screenshotCaptions`; no stray file is present.
5. **Sync** to Partner Center with the next release's submission: the files in order and each locale's captions from `listings.json`, in the same upload archive as that submission's package, never a separate upload.

A UI change that shows in a screenshot means recapturing that shot.
