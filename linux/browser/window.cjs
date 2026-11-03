'use strict';
const {BrowserWindow, WebContentsView, Menu, screen, dialog, clipboard, shell} = require('electron');
const path = require('node:path');
const {PageLifecycle} = require('./lifecycle.cjs');
const {validUrl,allowedPageRedirect} = require('./urls.cjs');
const {BookmarkIcons} = require('./bookmark_icons.cjs');
const {browserUserAgent} = require('./browser_identity.cjs');
const {windowIcon} = require('./window_icon.cjs');
const {BookmarkOverlay} = require('./bookmark_overlay.cjs');
const {PageFullscreen} = require('./fullscreen.cjs');
const extensionRegistry=require('./extension_registry.cjs');
const {installNotificationFrame}=require('./notification_frame.cjs');
const {projectBookmarks, bookmarkActions, bookmarkVersion, resolveBookmark} = require('./bookmarks.cjs');

/** Own native frame and isolated remote view; remote content cannot reach toolbar IPC. */
class WebLauncher {
  constructor(config, theme, anchor, presentation, services) {
    this.services = services;
    this.profiles = services.profiles;
    this.emit = services.emit;
    this.id = config.Id;
    this.config = config;
    this.theme = theme;
    this.presentation = presentation;
    this.tabs = [];
    this.closedTabs = [];
    this.tabSequence = 0;
    this.dialogs = new Set();
    this.bookmarkIcons = new BookmarkIcons(this.profiles.get(config), () => this.toolbar(), (bytes,token)=>this.emit({type:'bookmark-icon',id:this.id,token,imageBase64:bytes.toString('base64')}));
    this.createWindow(anchor);
    this.restoringSession = true;
    const saved = presentation.session;
    if (saved && Array.isArray(saved.tabs) && saved.tabs.length && saved.tabs.length <= 64 && saved.tabs.every(validUrl)) {
      this.tabs = saved.tabs.map(url => ({id: ++this.tabSequence, url, title: '', view: null, lifecycle: null}));
      const active = Number.isInteger(saved.active) ? Math.max(0, Math.min(this.tabs.length - 1, saved.active)) : 0;
      this.selectTab(this.tabs[active].id);
      if (config.WebHiddenPolicy === 2) {
        for (const tab of this.tabs) if (!tab.view) this.createView(tab.url, false, {}, tab);
      }
    } else this.createView();
    this.restoringSession = false;
    this.presented = false;
    this.publishSession();
  }

  /** Map a fresh native host so KWin positions it once before its first painted frame. */
  createWindow(anchor) {
    const config = this.config;
    const theme = this.theme;
    this.anchor = anchor;
    const anchored = Array.isArray(anchor) && !config.WebRegularWindow;
    this.anchored = anchored;
    const fixed = Number.isInteger(config.WebAnchor) && config.WebAnchor >= 1 && config.WebAnchor <= 9 ? ':at' + config.WebAnchor : this.savedPositionSuffix();
    const positioned = Array.isArray(anchor);
    const title = positioned ? `LittleLauncher${config.WebRegularWindow ? 'Window' : 'Flyout'}:${this.id}:${anchor[0]}:${anchor[1]}${anchor[2] === 'native' ? ':native' : ''}${config.WebPinFlyout ? ":pinned" : ""}${fixed}` :
      config.Name;
    const display = this.displayForAnchor(anchor);
    const area = display.workAreaSize;
    this.window = new BrowserWindow({show: false, title, frame: !anchored,
      icon: windowIcon(this.presentation.windowIcon),
      width: Math.min(config.WebFlyoutWidth || 480, area.width - 16),
      height: Math.min(config.WebFlyoutHeight || 720, area.height - 16),
      backgroundColor: theme.window, autoHideMenuBar: true,
      webPreferences: {preload: path.join(__dirname, 'preload.cjs'), sandbox: true,
        contextIsolation: true, nodeIntegration: false}});
    // KWin receives the placement caption before activating a regular native window.
    // Restore its human title after that first mapping; flyouts retain their ownership protocol.
    const placedWindow = this.window;
    if (positioned && config.WebRegularWindow) placedWindow.once('focus', () => {
      if (!placedWindow.isDestroyed()) placedWindow.setTitle(this.config.Name);
    });
    this.window.webContents.on('will-navigate', event => event.preventDefault());
    this.window.webContents.setWindowOpenHandler(() => ({action: 'deny'}));
    this.window.webContents.on('page-title-updated', event => event.preventDefault());
    this.window.on('page-title-updated', event => event.preventDefault());
    this.bookmarkOverlay = new BookmarkOverlay(this);
    this.fullscreen = new PageFullscreen(this);
    this.window.loadFile(path.join(__dirname, 'chrome.html'));
    this.window.webContents.on('did-finish-load', () => {this.window.webContents.setZoomMode('disabled');this.toolbar();});
    this.window.webContents.on('before-input-event', (event, input) => {
      if (input.type === 'keyDown' && this.shortcut(input)) event.preventDefault();
    });
    this.canRememberSize = false;
    const mappedWindow = this.window;
    this.window.on('show', () => setTimeout(() => {if (this.window === mappedWindow && !mappedWindow.isDestroyed()) this.canRememberSize = true;}, 350));
    this.window.on('resize', () => {if(this.fullscreen?.state)this.fullscreen.version++;this.layout(); this.rememberSize();this.bookmarkOverlay.update();});
    this.window.on('blur', () => {
      setTimeout(() => {
        if (!this.window.isDestroyed() && this.config.WebRegularWindow && this.config.WebWindowAutoHide &&
            !this.dialogs.size && !this.window.isFocused()) this.hide();
      }, 50);
    });
    this.window.on('maximize', () => this.toolbar());
    this.window.on('unmaximize', () => this.toolbar());
    this.window.on('close', event => {
      if (!this.services.isQuitting()) {
        event.preventDefault();
        if (this.detached) void this.services.onDetachedClose(this);
        else this.hide();
      }
    });
  }

  /** Mark host-owned prompts before focus moves to a portal or the native parent process. */
  beginDialog() {
    const ticket = {};
    this.dialogs.add(ticket);
    this.updateProtocolTitle();
    return ticket;
  }

  endDialog(ticket) {
    this.dialogs.delete(ticket);
    this.updateProtocolTitle();
  }

  savedPositionSuffix() {
    const value=this.presentation.position;
    return this.config.WebAnchor===10 && this.presentation.positionSupported && Array.isArray(value) && value.length===2 && value.every(part=>Number.isInteger(part) && Math.abs(part)<=1000000) ? ':last:'+value.join(':') : '';
  }

  updateProtocolTitle() {
    if (!this.anchored || this.window.isDestroyed()) return;
    const fixed = Number.isInteger(this.config.WebAnchor) && this.config.WebAnchor >= 1 && this.config.WebAnchor <= 9 ? ':at' + this.config.WebAnchor : this.savedPositionSuffix();
    const suffix = (this.config.WebPinFlyout ? ':pinned' : '') + (this.dialogs.size ? ':owned' : '') + fixed;
    this.window.setTitle(`LittleLauncherFlyout:${this.id}:${this.anchor[0]}:${this.anchor[1]}${this.anchor[2] === 'native' ? ':native' : ''}${suffix}`);
  }

  bookmarks() {
    const decorate = items => items.map(item => item.IsFolder ? {...item,Children:decorate(item.Children)} :
      {...item, favicon: this.presentation.showBookmarkBar && this.window.isVisible() ? this.bookmarkIcons.get(item.Url) : null});
    return decorate(projectBookmarks(this.config.WebBookmarks || []));
  }

  /** Prevent detached or read-only content from offering mutations that cannot be saved. */
  canEditBookmarks() {
    return !this.detached && !(this.config.IsShared && !this.config.IsSharedOwner && !this.config.SharedTwoWay);
  }

  /** Keep native form ownership until its reply, then return focus without remapping the host. */
  async bookmarkRequest(message) {
    const ticket = this.beginDialog();
    try {
      const replied = await this.services.request({...message, id: this.id});
      if (replied && !this.detached && !this.window.isDestroyed() && this.window.isVisible()) {
        this.window.focus();
        this.view?.webContents.focus();
      }
    } finally {
      this.endDialog(ticket);
    }
  }

  /** Native menu callbacks retain the saved tree preimage even if settings change while open. */
  bookmarkContext(index, version) {
    if (version !== bookmarkVersion(this.config.WebBookmarks || [])) {
      this.toolbar();
      return;
    }
    const expected = structuredClone(this.config.WebBookmarks || []);
    const expectedHome = this.config.WebHomeUrl ?? null;
    const sourcePath = index === -1 ? [] : this.bookmarkPath(index);
    if (!sourcePath) return;
    const invoke = (action, path, extra = {}) => {
      if (bookmarkVersion(this.config.WebBookmarks || []) !== version) {this.toolbar();return;}
      const item = resolveBookmark(expected, path)?.item;
      if (['open', 'open-tab', 'copy', 'external'].includes(action)) {
        if (!item || item.IsFolder || !validUrl(item.Url)) return;
        if (action === 'open') this.action('navigate', item.Url);
        if (action === 'open-tab') this.createView(item.Url);
        if (action === 'copy') void clipboard.writeText(item.Url).catch(() => this.emit({type:'warning',message:'The address could not be copied.'}));
        if (action === 'external') void shell.openExternal(item.Url).catch(() => {});
        return;
      }
      if (!this.canEditBookmarks()) return;
      void this.bookmarkRequest({type: 'bookmark-action', action, path, expected,
        expectedHome, ...extra});
    };
    const template = bookmarkActions(expected, sourcePath, invoke, !!this.config.WebBookmarkIconsOnly,
      {editable: this.canEditBookmarks(), homeUrl: this.presentation.homeUrl});
    if (index === -1) template.push({type: 'separator'}, {label: 'Icons only', type: 'checkbox',
      checked: !!this.config.WebBookmarkIconsOnly, enabled: this.canEditBookmarks(), click: item => void this.preference('WebBookmarkIconsOnly', item.checked)});
    this.menu(template);
  }

  openBookmark(index, version, newTab = false) {
    if (version !== bookmarkVersion(this.config.WebBookmarks || [])) {this.toolbar();return;}
    const sourcePath = this.bookmarkPath(index);
    const item = sourcePath && resolveBookmark(this.config.WebBookmarks || [],sourcePath)?.item;
    if (!item || item.IsFolder || !validUrl(item.Url)) return;
    this.bookmarkOverlay.close();
    if (newTab) this.createView(item.Url);
    else this.action('navigate', item.Url);
  }

  bookmarkPath(target) {
    if (Number.isInteger(target)) return this.bookmarks()[target]?.sourcePath;
    if (!Array.isArray(target) || !target.length || target.length > 32 || !target.every(part=>Number.isInteger(part) && part>=0)) return null;
    const item=resolveBookmark(this.config.WebBookmarks || [],target)?.item;
    return item && (item.IsFolder || validUrl(item.Url)) ? target : null;
  }

  bookmarkMenu(index, version, anchor) {
    const path=this.bookmarkPath(index);
    if (version !== bookmarkVersion(this.config.WebBookmarks || []) || !path) {this.toolbar();return;}
    if (resolveBookmark(this.config.WebBookmarks || [],path)?.item.IsFolder) this.bookmarkOverlay.open(path,anchor);
  }

  bookmarkOverflow(index, version, anchor) {
    if (version !== bookmarkVersion(this.config.WebBookmarks || []) || !Number.isInteger(index) || index<0) return;
    this.bookmarkOverlay.open(null,anchor,index);
  }

  handleToolbar(event,action,value) {
    if(!this.trustedToolbar(event))return;
    if(this.bookmarkOverlay.action(action,value))return;
    if(this.fullscreen.action(action))return;
    const target=value && (Number.isInteger(value.index) || Array.isArray(value.path)) && typeof value.version==='string';
    if(action==='bookmark-open' && target)this.openBookmark(value.path ?? value.index,value.version,value.newTab===true);
    if(action==='bookmark-menu' && target)this.bookmarkMenu(value.path ?? value.index,value.version,value.anchor);
    if(action==='bookmark-overflow' && target)this.bookmarkOverflow(value.index,value.version,value.anchor);
    if(action==='bookmark-context' && target)this.bookmarkContext(value.path ?? value.index,value.version);
    const contents=this.view?.webContents;
    if(action==='bookmark-add' && this.canEditBookmarks() && contents && validUrl(contents.getURL()))void this.bookmarkRequest({type:'bookmark-add',url:contents.getURL(),title:contents.getTitle().slice(0,256)});
    this.action(action,value);
  }

  trustedToolbar(event) {
    return [this.window.webContents,this.bookmarkOverlay?.view?.webContents,this.fullscreen?.view?.webContents].some(contents=>contents && !contents.isDestroyed() && event.sender===contents && event.senderFrame===contents.mainFrame);
  }

  get busy() { return this.tabs.some(tab => tab.lifecycle?.busy); }
  get lifecycle() { return this.activeTab?.lifecycle; }

  get addressBarVisible() {return !!this.config.WebShowAddressBar || this.activeTab?.url === 'about:blank';}

  publishSession() {
    if (this.tabs.length > 64) {
      if (!this.sessionLimitNoticed) {this.sessionLimitNoticed = true;this.emit({type:'warning',message:'This launcher has more than 64 tabs. Its previous saved session is preserved until tabs are closed.'});}
      return;
    }
    if (this.restoringSession || this.detached || !this.tabs.length) return;
    const tabs = [], activeIndex = this.tabs.indexOf(this.activeTab);
    let active = 0;
    for (const [index, tab] of this.tabs.entries()) {
      const current = tab.view?.webContents.getURL();
      const url = validUrl(current) ? current : tab.url;
      if (!validUrl(url) || url.length > 8192 || tabs.length >= 64) continue;
      if (index <= activeIndex) active = tabs.length;
      tabs.push(url);
    }
    const session = {tabs, active};
    const revision = JSON.stringify(session);
    if (revision === this.sessionRevision) return;
    this.sessionRevision = revision;
    this.emit({type: 'session', id: this.id, session});
  }

  toolbar() {
    if (this.window.isDestroyed()) return;
    // Navigation can hide the temporary blank-tab address row without a resize.
    // Keep the page bounds synchronized with the rows we publish to chrome.
    this.layout();
    this.publishSession();
    const contents = this.view?.webContents;
    this.bookmarkOverlay.update();
    this.fullscreen?.layout();
    this.window.webContents.send('toolbar-state', {
      showBookmarkBar: this.presentation.showBookmarkBar, editable: this.canEditBookmarks(),
      showAddressBar: this.addressBarVisible,
      showTabs: !!this.config.WebAlwaysShowTabs || this.tabs.length > 1,
      tabs: this.tabs.map(tab => ({id: tab.id, title: tab.title || tab.url || 'New tab', favicon: this.bookmarkIcons.peek(tab.url), active: tab === this.activeTab})),
      address: this.activeTab?.initializing ? this.activeTab.url : contents?.getURL() || this.activeTab?.url || '',
      canGoBack: !!contents?.navigationHistory.canGoBack(), canGoForward: !!contents?.navigationHistory.canGoForward(),
      pinSupported: this.anchored || process.env.XDG_SESSION_TYPE !== 'wayland',
      loading: !!this.activeTab?.initializing || !!contents?.isLoading(), pinned: !!this.config.WebPinFlyout,
      regular: !this.anchored, nativeFrame: !this.anchored, maximized: this.window.isMaximized(),
      bookmarkIconsOnly: !!this.config.WebBookmarkIconsOnly,
      bookmarkVersion: bookmarkVersion(this.config.WebBookmarks || []),
      name: this.config.Name + (this.detached ? ' · ' + (this.detachReason || 'Removed launcher: finish activity here') : ''),
      theme: this.theme, bookmarks: this.bookmarks(),
      extensions: this.services.extensions?.presentation(this) || [],
    });
  }

  selectTab(id) {
    const tab = this.tabs.find(item => item.id === id);
    if (!tab) return;
    if(this.fullscreen?.state && this.activeTab!==tab){this.fullscreen.exitPage();this.fullscreen.leave();}
    this.bookmarkOverlay?.close();
    if (this.view) this.window.contentView.removeChildView(this.view);
    if (this.activeTab?.lifecycle) void this.activeTab.lifecycle.visibility(true);
    this.activeTab = tab;
    if (!tab.view) { this.createView(tab.url, true, {}, tab); return; }
    this.view = tab.view;
    this.window.contentView.addChildView(this.view);
    extensionRegistry.activateTab(this.view.webContents,this);
    void tab.lifecycle.visibility(!this.window.isVisible());
    this.layout(); this.toolbar();
    if (this.window.isVisible()) this.view.webContents.focus();
  }

  async closeTab(id) {
    const tab = this.tabs.find(item => item.id === id);
    if (!tab || tab.closePending) return;
    tab.closePending = true;
    try {
      if (tab.lifecycle?.busy) {
        const ticket = this.beginDialog();
        try {
          const result = await dialog.showMessageBox(this.window, {type: 'warning',
            message: 'Closing this tab may interrupt media or transfers.',
            buttons: ['Keep open', 'Close tab'], defaultId: 0, cancelId: 0});
          if (result.response !== 1) return;
        } finally { this.endDialog(ticket); }
      }
      // The confirmation can outlive a launcher removal or a different close request.
      if (!this.tabs.includes(tab) || this.window.isDestroyed()) return;
      const index = this.tabs.indexOf(tab);
      if (tab === this.activeTab && this.view) this.window.contentView.removeChildView(this.view);
      const address = tab.view?.webContents.getURL() || tab.url;
      if (validUrl(address)) {
        this.closedTabs.push(address);
        if (this.closedTabs.length > 10) this.closedTabs.shift();
      }
      tab.closing = true;
      tab.view?.webContents.close({waitForBeforeUnload: false});
      this.tabs.splice(index, 1);
      if (tab === this.activeTab) {
        this.view = null; this.activeTab = null;
        if (!this.tabs.length) {
          const session = {tabs: [], active: 0};
          this.sessionRevision = JSON.stringify(session);
          if (!this.detached) this.emit({type:'session',id:this.id,session});
          this.hide();
        } else this.selectTab(this.tabs[Math.min(index, this.tabs.length - 1)].id);
      }
      this.layout(); this.toolbar();
    } finally {tab.closePending = false;}
  }

  createView(address, makeActive = true, options = {}, existing = null) {
    const view = new WebContentsView({...(options.webContents ? {webContents: options.webContents} : {}), webPreferences: {...options.webPreferences, session: this.profiles.get(this.config),
      sandbox: true, contextIsolation: true, nodeIntegration: false, disableHtmlFullscreenWindowResize: true, preload: undefined,
      webSecurity: true, allowRunningInsecureContent: false}});
    const contents = view.webContents;
    contents.setZoomMode('isolated');
    contents.setUserAgent(browserUserAgent());
    contents.setZoomFactor(Math.min(4, Math.max(.25, (this.config.WebZoomPercent || 100) / 100)));
    const lifecycle = new PageLifecycle(contents, value => this.emit({...value, busy: this.busy, id: this.id}),
      this.config.WebHiddenPolicy || 0, this.config.WebIdleUnloadMinutes || 5);
    const tab = existing || {id: ++this.tabSequence, url: address || this.presentation.homeUrl, title: ''};
    Object.assign(tab, {view, lifecycle, unloading: false, closing: false});
    if (!existing) this.tabs.push(tab);
    extensionRegistry.registerTab(contents,this);
    if (makeActive) this.selectTab(tab.id);
    else void lifecycle.visibility(true);
    for (const event of ['did-start-loading', 'did-stop-loading', 'did-navigate-in-page']) {
      contents.on(event, () => this.toolbar());
    }
    contents.on('dom-ready',()=>{void contents.executeJavaScript('('+installNotificationFrame.toString()+')()').catch(()=>{});});
    contents.on('enter-html-full-screen',()=>this.fullscreen.enter(tab));
    contents.on('leave-html-full-screen',()=>this.fullscreen.leave(tab));
    contents.on('zoom-changed', (event, direction) => {
      event.preventDefault(); this.action(direction === 'in' ? 'zoom-in' : 'zoom-out');
    });
    contents.on('page-favicon-updated', (_event, urls) => {
      this.bookmarkIcons.observe(contents.getURL(), urls, tab.iconRedirects || []);
      // Page-declared icons can be on the site's CDN. Only expose bounded raster
      // bytes to the native chooser, never page content, cookies or raw metadata.
      const page = contents.getURL(), home = this.presentation.homeUrl;
      if (!validUrl(page) || !validUrl(home) || new URL(page).origin !== new URL(home).origin) return;
      const candidates = urls.filter(url => {
        if (!validUrl(url) || url.length > 8192) return false;
        const parsed = new URL(url);
        return !parsed.username && !parsed.password && !(page.startsWith('https:') && parsed.protocol !== 'https:');
      }).slice(0, 3);
      const identity = page + JSON.stringify(candidates);
      if (tab.iconRequest === identity) return;
      tab.iconRequest = identity;
      void (async () => {
        for (const url of candidates) {
          let png;
          try { png = await this.bookmarkIcons.loadBytes(url); } catch { continue; }
          if (contents.isDestroyed() || tab.iconRequest !== identity || contents.getURL() !== page) return;
          if (png) {this.emit({type:'page-icon', id:this.id, url:page, imageBase64:png.toString('base64')});return;}
        }
      })();
    });
    contents.on('page-title-updated', (_event, title) => { tab.title = title; this.toolbar(); });
    contents.on('before-input-event', (event, input) => {
      if (input.type !== 'keyDown') return;
      if (this.shortcut(input)) event.preventDefault();
    });
    contents.on('will-navigate', event => {
      if (!validUrl(event.url)) event.preventDefault();
    });
    contents.on('will-redirect', event => {
      if (!allowedPageRedirect(event.url,event.isMainFrame,id=>contents.session.extensions.getExtension(id))) event.preventDefault();
      else if(event.isMainFrame && (tab.iconRedirects?.length||0)<16)(tab.iconRedirects ||= []).push(event.url);
    });
    contents.on('content-bounds-updated', event => event.preventDefault());
    contents.setWindowOpenHandler(details => {
      if (this.maintenance) return {action: 'deny'};
      if (this.config.WebLinksInBrowser && validUrl(details.url)) {
        void require('electron').shell.openExternal(details.url).catch(() => {});
        return {action: 'deny'};
      }
      if (!validUrl(details.url) && details.url !== 'about:blank') return {action: 'deny'};
      return {action: 'allow', outlivesOpener: true, overrideBrowserWindowOptions: {webPreferences: {
        sandbox: true, contextIsolation: true, nodeIntegration: false, disableHtmlFullscreenWindowResize: true, preload: undefined,
        session: this.profiles.get(this.config)}}, createWindow: options => {
        const child = this.createView(options.webContents ? 'about:blank' : details.url, details.disposition !== 'background-tab', options);
        // A script-requested popup can be a pending sign-in. Retain the former owned-popup
        // protection after adopting it as a tab; ordinary target-blank links remain idle eligible.
        if (details.disposition === 'new-window') {
          child.lifecycle.downloads.add(child);
          void child.lifecycle.update();
        }
        return child.view.webContents;
      }};
    });
    contents.on('did-fail-load', (_event, code, _description, _url, main) => {
      if (main && code !== -3) this.emit({type: 'error', id: this.id, message: 'The site could not be loaded. Check its address and connection.'});
    });
    this.layout();
    const url = address || tab.url;
    if (!options.webContents && (validUrl(url) || url === 'about:blank')) {
      tab.initializing=true;lifecycle.preparing=true;
      tab.navigationReady=Promise.resolve(this.profiles.ready?.(this.config)).then(async()=>{
        // CDP needs a live renderer. Bootstrap without running any site script,
        // retaining the intended saved URL until its real navigation completes.
        if(!contents.getURL()){
          tab.bootstrapping=true;tab.bootstrapped=true;
          try{await contents.loadURL('about:blank');}finally{tab.bootstrapping=false;}
        }
        try{await this.profiles.prepareContents?.(contents);}catch{if(!contents.isDestroyed())this.emit({type:'warning',message:'Website notification integration could not start. The page can still be opened.'});}
        if(!contents.isDestroyed()&&url!=='about:blank')return contents.loadURL(url);
      }).catch(()=>{}).finally(()=>{tab.initializing=false;lifecycle.preparing=false;void lifecycle.update();this.toolbar();});
    }
    if(options.webContents)void this.profiles.prepareContents?.(contents).catch(()=>{});
    contents.on('did-navigate', (_event, url) => {
      if(tab.bootstrapping)return;
      if(tab.bootstrapped&&url!=='about:blank'){
        const history=contents.navigationHistory,entries=history.getAllEntries();
        if(entries.length>1&&entries[0].url==='about:blank')history.removeEntryAtIndex(0);
        tab.bootstrapped=false;
      }
      tab.url = url;
      contents.setZoomFactor(Math.min(4, Math.max(.25, (this.config.WebZoomPercent || 100) / 100)));
      this.toolbar();
    });
    contents.on('did-navigate-in-page', (_event, url, main) => {if (main) {tab.url = url;this.toolbar();}});
    const contentsId = contents.id;
    contents.once('destroyed', () => {
      for (const key of this.profiles.grants.keys()) {
        if (key.startsWith(contentsId + ':')) this.profiles.grants.delete(key);
      }
      // A delayed destruction from a sleeping view cannot remove its already-restored replacement.
      if (tab.view !== view) return;
      tab.view = null;
      if (!tab.closing && !tab.unloading && !this.services.isQuitting() && !this.window.isDestroyed() && this.tabs.includes(tab)) {
        const index = this.tabs.indexOf(tab);
        this.tabs.splice(index, 1);
        if (tab === this.activeTab) {
          this.view = null;
          this.activeTab = null;
          if (this.tabs.length) this.selectTab(this.tabs[Math.min(index, this.tabs.length - 1)].id);
          else this.createView(this.presentation.homeUrl);
        }
        this.layout(); this.toolbar();
      }
    });
    contents.on('did-start-navigation', event => {
      if (event.isMainFrame && !event.isSameDocument) {
        tab.iconRedirects=[event.url];
        this.fullscreen.leave(tab);
        for (const key of this.profiles.grants.keys()) {
          if (key.startsWith(contentsId + ':')) this.profiles.grants.delete(key);
        }
      }
    });
    return tab;
  }

  layout() {
    if (!this.view) return;
    for(const tab of this.tabs)if(tab.view&&!tab.view.webContents.isDestroyed())extensionRegistry.registerTab(tab.view.webContents,this);
    if(this.activeTab?.view)extensionRegistry.activateTab(this.activeTab.view.webContents,this);
    const [width, height] = this.window.getContentSize();
    if(this.fullscreen?.state){this.view.setBounds({x:0,y:0,width,height});this.fullscreen.layout();return;}
    const top = 34 + (this.addressBarVisible ? 34 : 0) + (this.config.WebAlwaysShowTabs || this.tabs.length > 1 ? 34 : 0);
    this.view.setBounds({x: 0, y: top, width, height: Math.max(1, height - top - (this.presentation.showBookmarkBar ? 34 : 0))});
  }

  displayForAnchor(anchor) {
    if (!Array.isArray(anchor)) return screen.getPrimaryDisplay();
    let point = {x: anchor[0], y: anchor[1]};
    if (anchor[2] === 'native') {
      for (const display of screen.getAllDisplays()) {
        const bounds = display.bounds;
        const local = {x: bounds.x + (point.x - bounds.x) / display.scaleFactor,
          y: bounds.y + (point.y - bounds.y) / display.scaleFactor};
        if (local.x >= bounds.x && local.x < bounds.x + bounds.width && local.y >= bounds.y && local.y < bounds.y + bounds.height) {
          return display;
        }
      }
    }
    return screen.getDisplayNearestPoint(point);
  }

  remap(anchor) {
    const wasFullscreen=!!this.fullscreen.state;
    this.fullscreen.dispose();
    this.bookmarkOverlay.dispose();
    const previous = this.window;
    if (this.view) previous.contentView.removeChildView(this.view);
    previous.hide();
    this.createWindow(anchor);
    if (this.view) this.window.contentView.addChildView(this.view);
    previous.destroy();
    if(wasFullscreen)this.fullscreen.enter(this.activeTab);
    this.layout();
  }

  applyConfig(previous) {
    if (!!previous.WebSharedProfile !== !!this.config.WebSharedProfile) this.bookmarkIcons = new BookmarkIcons(this.profiles.get(this.config), () => this.toolbar(), (bytes,token)=>this.emit({type:'bookmark-icon',id:this.id,token,imageBase64:bytes.toString('base64')}));
    if (!!previous.WebRegularWindow !== !!this.config.WebRegularWindow && this.presented) {
      const visible = this.window.isVisible();
      this.remap(this.anchor);
      if (visible) {this.window.show(); this.view?.webContents.focus();}
    }
    if (previous.WebZoomPercent !== this.config.WebZoomPercent) {
      for (const tab of this.tabs) tab.view?.webContents.setZoomFactor(Math.min(4, Math.max(.25, (this.config.WebZoomPercent || 100) / 100)));
    }
    if (previous.WebFlyoutWidth !== this.config.WebFlyoutWidth || previous.WebFlyoutHeight !== this.config.WebFlyoutHeight) {
      const area = this.displayForAnchor(this.anchor).workAreaSize;
      const width = Math.min(this.config.WebFlyoutWidth || 480, area.width - 16);
      const height = Math.min(this.config.WebFlyoutHeight || 720, area.height - 16);
      const current = this.window.getContentSize();
      if (current[0] !== width || current[1] !== height) {
        this.applyingSize = true;
        this.window.setContentSize(width, height);
        setTimeout(() => {this.applyingSize = false;}, 150);
      }
    }
  }

  rememberSize() {
    if (this.fullscreen?.state || !this.presented || !this.canRememberSize || !this.window.isVisible() || this.applyingSize || this.config.WebLockSize ||
        this.window.isMaximized() || this.window.isFullScreen()) return;
    clearTimeout(this.sizeTimer);
    this.sizeTimer = setTimeout(() => {
      if (this.fullscreen?.state || this.window.isDestroyed() || !this.window.isVisible() || this.config.WebLockSize) return;
      const [width, height] = this.window.getContentSize();
      if (width >= 320 && height >= 240 && width <= 4000 && height <= 4000 &&
          (width !== this.config.WebFlyoutWidth || height !== this.config.WebFlyoutHeight)) {
        void this.services.request({type: 'web-size', id: this.id, width, height});
      }
    }, 200);
  }

  activateNotification(tabId) {
    if(this.window.isDestroyed()||!this.tabs.some(tab=>tab.id===tabId))return;
    this.selectTab(tabId);
    if(this.window.isMinimized())this.window.restore();
    let anchor=this.anchor;
    if(!this.presented&&!Array.isArray(anchor)&&this.presentation.positionSupported){
      const point=screen.getCursorScreenPoint();anchor=[Math.round(point.x),Math.round(point.y)];
    }
    this.show(anchor,{notification:true});
    this.window.focus();
    this.view?.webContents.focus();
  }

  async notificationClient(action,address,sourceId) {
    const source=this.tabs.find(tab=>tab.id===sourceId);
    if(!source)return false;
    try{
      const url=new URL(address),origin=new URL(source.view?.webContents.getURL()||source.url).origin;
      if(!validUrl(address)||(action==='focus'&&url.origin!==origin)||url.username||url.password||address.length>8192)return false;
    }catch{return false;}
    if(action==='open'&&this.config.WebLinksInBrowser){await shell.openExternal(address);return true;}
    let target=this.tabs.find(tab=>(tab.view?.webContents.getURL()||tab.url)===address);
    if(!target&&action==='open'){this.createView(address);target=this.activeTab;}
    if(!target)return false;
    this.activateNotification(target.id);
    if(action==='open'&&target.navigationReady){
      let timer;
      await Promise.race([target.navigationReady,new Promise(resolve=>{timer=setTimeout(resolve,5000);})]);
      clearTimeout(timer);
    }else if(action==='open'&&target.view?.webContents.isLoading()){
      const contents=target.view.webContents;
      await new Promise(resolve=>{const done=()=>{clearTimeout(timer);contents.removeListener('did-stop-loading',done);resolve();};const timer=setTimeout(done,5000);contents.once('did-stop-loading',done);});
    }
    return !this.window.isDestroyed();
  }

  show(anchor = this.anchor, {notification=false} = {}) {
    const wasPresented = this.presented;
    const hadView = !!this.view;
    const wantsAnchor = Array.isArray(anchor) && !this.config.WebRegularWindow;
    // A Keep running page was preloaded without a tray anchor. Its first visible host must
    // still acquire the captured activation, while retaining the same loaded WebContents.
    if ((!notification && this.presented && !this.window.isVisible()) || wantsAnchor !== this.anchored || (Array.isArray(anchor) && !Array.isArray(this.anchor))) this.remap(anchor);
    this.presented = true;
    if (!this.view && this.activeTab) this.selectTab(this.activeTab.id);
    if (!this.view) this.createView();
    if (!notification && wasPresented && hadView && !this.activeTab?.initializing && this.config.WebReloadOnShow) this.view.webContents.reload();
    this.lifecycle.visibility(false);
    this.window.show();
    this.toolbar();
    this.view.webContents.focus();
  }

  hide() {
    this.bookmarkOverlay.close();
    clearTimeout(this.sizeTimer);
    this.canRememberSize = false;
    this.window.hide();
    for (const tab of this.tabs) void tab.lifecycle?.visibility(true);
  }

  tick() {
    for (const tab of this.tabs) {
      if (tab.view && tab.lifecycle.canUnload()) {
        if (tab === this.activeTab) {
          this.window.contentView.removeChildView(tab.view);
          this.view = null;
        }
        tab.unloading = true;
        tab.view.webContents.close();
        tab.view = null;
        this.emit({type: 'unloaded', id: this.id});
      }
    }
  }
}

Object.assign(WebLauncher.prototype, require('./web_actions.cjs'));
module.exports = {WebLauncher, validUrl};
