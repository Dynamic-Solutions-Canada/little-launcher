// THROWAWAY KDE/Wayland capability probe. No production settings or application launch actions.
const {app, BrowserWindow, WebContentsView, Tray, Menu, dialog, ipcMain} = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const root = __dirname;
app.setName('Little Launcher KDE PoC');
app.setDesktopName('little-launcher-kde-poc.desktop');
app.setPath('userData', path.join(root, 'electron-profile'));
app.setPath('crashDumps', path.join(root, 'crashes'));
const regularWindow = process.env.LL_POC_WINDOW_MODE === 'regular';
let quitting = false;
const entries = [];

/** Record client observations; compositor observations and captured frames are the placement authority. */
function log(event, detail = {}) {
  const row = {utc: new Date().toISOString(), event, ...detail};
  fs.appendFileSync(path.join(root, 'events.jsonl'), JSON.stringify(row) + '\n');
  console.log(JSON.stringify(row));
}

/** Request a flyout adjacent to its tray activation. Wayland is intentionally tested without an X11 fallback. */
async function toggle(entry, bounds, position, source) {
  log('activation', {kind: entry.kind, source, bounds, position, trayBounds: entry.tray.getBounds()});
  if (entry.window.isVisible()) {
    entry.window.hide();
    log('hide', {kind: entry.kind, reason: 'toggle'});
    return;
  }
  const anchor = position || {x: 1100, y: 775};
  const size = entry.window.getSize();
  const requested = {x: Math.max(8, Math.min(1280 - size[0] - 8, anchor.x - size[0] / 2)), y: Math.max(8, anchor.y - size[1] - 28)};
  if (!regularWindow) entry.window.setPosition(Math.round(requested.x), Math.round(requested.y));
  log(regularWindow ? 'regular-window-show' : 'placement-request', {kind: entry.kind, requested: regularWindow ? null : requested});
  entry.window.show();
  entry.window.focus();
  log('show', {kind: entry.kind, clientBounds: entry.window.getBounds()});
}

/** Keep the parent alive while its owned file chooser is open, then restore focus. */
ipcMain.handle('file-dialog', async event => {
  const entry = entries.find(item => item.window.webContents === event.sender);
  if (!entry) return;
  entry.dialogOpen = true;
  log('dialog-open', {kind: entry.kind});
  try {
    await dialog.showOpenDialog(entry.window, {title: 'PoC owned file chooser', properties: ['openFile']});
  } finally {
    entry.dialogOpen = false;
    entry.window.focus();
    log('dialog-close', {kind: entry.kind, visible: entry.window.isVisible()});
  }
});
ipcMain.on('hide', event => {
  const entry = entries.find(item => item.window.webContents === event.sender);
  entry?.window.hide();
});
ipcMain.on('quit', () => app.quit());

app.whenReady().then(async () => {
  Menu.setApplicationMenu(null);
  for (const kind of ['shortcuts', 'web']) {
    const web = kind === 'web';
    const window = new BrowserWindow({
      width: web ? 520 : 292, height: web ? 480 : 304,
      frame: regularWindow, useContentSize: true, show: false, resizable: false, backgroundColor: '#252a33',
      title: regularWindow ? 'Little Launcher: regular window (integration unavailable)' : (web ? 'LL PoC Web' : 'LL PoC Shortcuts'),
      webPreferences: {preload: path.join(root, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true}
    });
    const tray = new Tray(path.join(root, web ? 'web.png' : 'shortcuts.png'));
    const entry = {kind, window, tray, dialogOpen: false};
    entries.push(entry);
    tray.setToolTip('Little Launcher PoC: ' + kind);
    tray.on('click', (_event, bounds, position) => { toggle(entry, bounds, position, 'tray-click').catch(console.error); });
    tray.setContextMenu(Menu.buildFromTemplate([
      {label: 'Open ' + kind + ' launcher', click: () => { toggle(entry, undefined, undefined, 'menu').catch(console.error); }},
      {label: 'Quit PoC', click: () => app.quit()}
    ]));
    window.on('close', event => {if (!quitting) {event.preventDefault(); window.hide();}});
    window.on('blur', () => {
      log('blur', {kind, dialogOpen: entry.dialogOpen});
      if (!regularWindow && !entry.dialogOpen) window.hide();
    });
    window.on('show', () => log('window-show', {kind}));
    await window.loadFile(path.join(root, web ? 'web-shell.html' : 'shortcuts.html'));
    if (regularWindow) {
      window.setTitle('Little Launcher: regular window (integration unavailable)');
      await window.webContents.executeJavaScript(
        web
          ? `document.querySelector('header').firstChild.textContent = 'Regular window: desktop integration unavailable ';`
          : `document.querySelector('footer').textContent = 'Regular window: desktop integration unavailable';`
      );
      log('regular-window-fallback', {kind});
    }
    if (web) {
      const view = new WebContentsView({webPreferences: {contextIsolation: true, nodeIntegration: false, sandbox: true, partition: 'persist:poc-isolated-web'}});
      window.contentView.addChildView(view);
      view.setBounds({x: 4, y: 42, width: 512, height: 434});
      view.webContents.setWindowOpenHandler(() => ({action: 'deny'}));
      view.webContents.on('did-finish-load', () => log('web-loaded', {url: view.webContents.getURL()}));
      view.webContents.on('did-fail-load', (_event, code, message, url) => log('web-load-error', {code, message, url}));
      view.webContents.loadURL('https://example.com').catch(error => log('web-error', {message: error.message}));
      entry.view = view;
    }
  }
  log('ready', {electron: process.versions.electron, pid: process.pid, session: process.env.XDG_SESSION_TYPE, backend: 'explicit ozone-platform=wayland'});
});
app.on('before-quit', () => {quitting = true; log('quit');});
app.on('window-all-closed', () => {});
process.on('SIGTERM', () => app.quit());
