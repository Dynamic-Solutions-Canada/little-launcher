'use strict';
const {app, ipcMain, dialog, nativeTheme} = require('electron');
const path = require('node:path');
const readline = require('node:readline');
const {WebLauncher, validUrl} = require('./window.cjs');
const {Profiles} = require('./profiles.cjs');
const {ExtensionHost}=require('./extension_host.cjs');
const {browserUserAgent} = require('./browser_identity.cjs');
const {windowIcon} = require('./window_icon.cjs');
app.userAgentFallback = browserUserAgent();
if (process.env.XDG_SESSION_TYPE === 'wayland') app.commandLine.appendSwitch('ozone-platform', 'wayland');

const dataArgument = process.argv.find(value => value.startsWith('--launcher-data='));
if (!dataArgument) throw new Error('A private launcher data directory is required.');
app.setPath('userData', path.resolve(dataArgument.slice('--launcher-data='.length)));
app.setName('little-launcher-web');
const desktopArgument = process.argv.find(value => value.startsWith('--launcher-desktop-id='));
const desktopId = desktopArgument?.slice('--launcher-desktop-id='.length);
app.setDesktopName(desktopId && /^io\.github\.RyanEwen\.LittleLauncher\.Web\.Preview\.[a-f0-9]+$/.test(desktopId) ? desktopId + '.desktop' : 'io.github.RyanEwen.LittleLauncher.Web.desktop');
// Keep hidden messaging pages responsive while retaining real page visibility events.
// Ordinary pages are explicitly frozen/unloaded by PageLifecycle instead.
for (const flag of ['disable-background-timer-throttling', 'disable-renderer-backgrounding',
  'disable-backgrounding-occluded-windows']) app.commandLine.appendSwitch(flag);
const windows = new Map();
const replies = new Map();
let sequence = 0;
let quitting = false;
let parentConnected = true;
process.stdout.on('error', error => {
  if (error.code === 'EPIPE') parentConnected = false;
});

/** Write only protocol messages; browser diagnostics remain on stderr. */
function emit(value) {
  if (!parentConnected) return;
  process.stdout.write(JSON.stringify(value) + '\n');
}

/** Bound interactive requests; orphaned or expired permissions are always denied. */
function request(value) {
  if (!parentConnected) {
    if (value.type !== 'close-launcher') return Promise.resolve(false);
    const owner = windows.get(value.id);
    return dialog.showMessageBox(owner?.window, {type: 'warning',
      buttons: ['Keep open', 'Close page'], defaultId: 0, cancelId: 0,
      message: 'Closing this page may interrupt media or transfers.'}).then(result => result.response === 1);
  }
  return new Promise(resolve => {
    const token = String(++sequence);
    const timeout = setTimeout(() => {
      replies.delete(token);
      resolve(false);
    }, 120000);
    replies.set(token, result => {
      clearTimeout(timeout);
      resolve(result);
    });
    emit({...value, token});
  });
}

function ownerFor(contents) {
  if (!contents || contents.isDestroyed()) return;
  for (const owner of windows.values()) {
    const tab = owner.tabs.find(tab => tab.view?.webContents === contents);
    if (!tab) continue;
    const facade = Object.create(owner);
    Object.defineProperty(facade, 'lifecycle', {value: tab.lifecycle});
    Object.defineProperty(facade, 'focusNotification', {value:()=>owner.activateNotification(tab.id)});
    Object.defineProperty(facade, 'notificationClient', {value:(action,url)=>owner.notificationClient(action,url,tab.id)});
    return facade;
  }
}
const profiles = new Profiles(request, ownerFor, {onPermissionError:message=>emit({type:'warning',message})});
const extensions=new ExtensionHost(emit);profiles.extensions=extensions;
extensions.changed=()=>{for(const owner of windows.values())owner.toolbar();};

ipcMain.on('toolbar-action', (event, action, value) => {
  const owner = [...windows.values()].find(item => item.trustedToolbar(event));
  owner?.handleToolbar(event,action,value);
});

/** Close a removed launcher's page only after protected work is explicitly confirmed. */
async function removeOwner(owner, confirmed = false) {
  if (owner.removing) return;
  owner.removing = true;
  try {
    if (owner.busy && !confirmed && !await request({type: 'close-launcher', id: owner.id})) {
      // Keep declined work reachable as a normal taskbar window after its tray identity is gone.
      owner.detached = true;
      owner.config = {...owner.config, WebRegularWindow: true};
      owner.hide();
      owner.show(null);
      owner.toolbar();
      emit({type: 'detached', id: owner.id});
      return;
    }
    for (const popup of owner.popups || []) popup.destroy();
    for (const tab of [...owner.tabs]) {
      tab.closing = true;
      if (tab.view && !tab.view.webContents.isDestroyed()) tab.view.webContents.close({waitForBeforeUnload: false});
    }
    owner.window.destroy();
    windows.delete(owner.id);
    const profileKey=owner.config.WebSharedProfile?'shared':owner.config.Id;
    if(![...windows.values()].some(other=>(other.config.WebSharedProfile?'shared':other.config.Id)===profileKey))extensions.suspend(profiles.get(owner.config));
    emit({type: 'removed', id: owner.id});
  } finally {
    owner.removing = false;
  }
}

/** Only the trusted Qt parent can issue commands through the inherited input pipe. */
async function command(message) {
  if(message.type==='bookmark-icon'){windows.get(message.id)?.bookmarkIcons.accept(message.token,message.png);return;}
  if(message.type==='extensions'){
    try {
      const config=message.launcher;
      if(!config || typeof config.Id!=='string' || config.Kind!==1)throw Error('Choose a web launcher profile.');
      const entries=await extensions.command(message,profiles.get(config),windows.get(config.Id));
      emit({type:'extensions-state',id:config.Id,entries});
    }catch {emit({type:'extensions-state',id:message.launcher?.Id,entries:[],error:'The extension action could not complete. Check that the package is valid and open the launcher before opening its extension.'});}
    return;
  }
  if (message.type === 'theme') {
    nativeTheme.themeSource = message.theme.dark ? 'dark' : 'light';
    for (const owner of windows.values()) {
      owner.theme = message.theme;
      owner.window.setBackgroundColor(message.theme.window);
      owner.toolbar();
    }
    return;
  }
  if (message.type === 'reply') {
    const reply = replies.get(message.token);
    replies.delete(message.token);
    reply?.(message.allowed === true);
    return;
  }
  if (message.type === 'remove' && Array.isArray(message.ids)) {
    for (const identifier of message.ids) {
      const owner = windows.get(identifier);
      if (owner) await removeOwner(owner, Array.isArray(message.confirmedIds) && message.confirmedIds.includes(identifier));
    }
    return;
  }
  if (message.type === 'quit') {
    const active = [...windows.values()].some(item => item.busy);
    if (active && message.confirmed !== true) {
      emit({type: 'quit-blocked'});
      return;
    }
    quitting = true;
    app.quit();
    return;
  }
  if (message.type === 'profile-action') {
    const config = message.launcher;
    if (!config || typeof config.Id !== 'string' || !/^[a-zA-Z0-9-]+$/.test(config.Id) || message.confirmed !== true) return;
    if (message.action === 'reset-permissions') {
      profiles.resetPermissions(config);
      emit({type:'notice',message:'Site permissions reset for this sign-in profile. Pages will ask again.'});
    } else if (message.action === 'clear-data') {
      try {
        await profiles.clearData(config,[...windows.values()]);
        emit({type:'notice',message:'Browsing data cleared for this sign-in profile.'});
      } catch {
        emit({type:'warning',message:'Browsing data could not be cleared. Finish active media or transfers in this sign-in profile and try again.'});
      }
    }
    return;
  }
  if (!['show', 'configure'].includes(message.type)) return;
  const config = message.launcher;
  if (!message.presentation || typeof message.presentation.homeUrl !== 'string') return;
  if (!config || typeof config.Id !== 'string' || !/^[a-zA-Z0-9-]+$/.test(config.Id)) return;
  nativeTheme.themeSource = message.theme.dark ? 'dark' : 'light';
  let owner = windows.get(config.Id);
  if (owner && Boolean(owner.config.WebSharedProfile) !== Boolean(config.WebSharedProfile)) {
    if (owner.busy) {
      emit({type: 'error', id: config.Id, message: 'The sign-in profile is saved. This active page keeps its previous profile until activity finishes and you reopen the launcher.'});
      return;
    }
    if (owner.window.isVisible() && message.type === 'configure') {
      message = {...message, type: 'show', anchor: owner.anchor};
    }
    owner.window.destroy();
    for (const tab of [...owner.tabs]) {
      tab.closing = true;
      if (tab.view && !tab.view.webContents.isDestroyed()) tab.view.webContents.close({waitForBeforeUnload: false});
    }
    windows.delete(config.Id);
    owner = null;
  }
  if (!owner && (message.type === 'show' || config.WebHiddenPolicy === 2)) {
    owner = new WebLauncher(config, message.theme, message.anchor, message.presentation, {profiles, extensions, emit, request,
      isQuitting: () => quitting, onDetachedClose: removeOwner});
    windows.set(config.Id, owner);
  }
  if (!owner) return;
  const previousConfig = owner.config;
  owner.config = config;
  owner.applyConfig(previousConfig);
  owner.theme = message.theme;
  owner.presentation = message.presentation;
  owner.window.setIcon(windowIcon(message.presentation.windowIcon));
  for (const tab of owner.tabs) {
    if (!tab.lifecycle) continue;
    tab.lifecycle.policy = config.WebHiddenPolicy || 0;
    tab.lifecycle.idleMinutes = config.WebIdleUnloadMinutes || 5;
    void tab.lifecycle.update();
  }
  owner.updateProtocolTitle();
  owner.toolbar();
  owner.layout();
  if (message.type === 'show') {
    if (owner.window.isVisible()) owner.hide();
    else owner.show(message.anchor);
  }
  else if (!owner.window.isVisible()) for (const tab of owner.tabs) void tab.lifecycle?.visibility(true);
}

app.on('window-all-closed', () => {
  if (!parentConnected) app.quit();
});
app.whenReady().then(() => {
  emit({type: 'ready', electron: process.versions.electron, chromium: process.versions.chrome});
  const input = readline.createInterface({input: process.stdin, crlfDelay: Infinity});
  let queued = Promise.resolve();
  input.on('line', line => {
    if (Buffer.byteLength(line) > 1048576) return;
    try {
      const message = JSON.parse(line);
      // Permission/dialog replies must bypass the queue that may be awaiting them.
      if (message.type === 'reply') void command(message);
      else queued = queued.then(() => command(message)).catch(() => emit({type: 'error', message: 'Browser command failed.'}));
    } catch {
      emit({type: 'error', message: 'Invalid browser command.'});
    }
  });
  input.on('close', async () => {
    parentConnected = false;
    for (const reply of replies.values()) reply(false);
    replies.clear();
    // Parent failure must not silently discard protected work.
    const active = [...windows.values()].some(item => item.busy);
    if (active) {
      for (const owner of windows.values()) {
        owner.detached = true;
        owner.detachReason = 'App connection lost: finish activity here';
        owner.config = {...owner.config, WebRegularWindow: true};
        owner.hide();
        owner.show(null);
        owner.toolbar();
      }
      const result = await dialog.showMessageBox({type: 'warning', buttons: ['Keep running', 'Quit'],
        defaultId: 0, cancelId: 0, message: 'Little Launcher disconnected. Quitting may interrupt media or downloads.'});
      if (result.response !== 1) return;
    }
    quitting = true;
    app.quit();
  });
  setInterval(() => { for (const owner of windows.values()) owner.tick(); }, 1000);
});
