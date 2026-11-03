'use strict';
const {Menu, shell, dialog, clipboard} = require('electron');
const {validUrl} = require('./urls.cjs');
const {moreTemplate,zoomLevels,bookmarked,stepZoom} = require('./web_menu.cjs');

const {tabTemplate} = require('./tab_menu.cjs');
const {bookmarkVersion} = require('./bookmarks.cjs');
const {bookmarkDrop} = require('./strip_drag.cjs');

/** Trusted browser chrome actions. Page content has neither this preload nor IPC access. */
module.exports = {
  async preference(key, value) {
    if (this.detached) return;
    await this.services.request({type: 'web-preference', id: this.id, key, value});
  },
  settings(section = '') {
    const ticket = this.beginDialog();
    void this.services.request({type: 'settings', id: this.id, section}).finally(() => {
      this.endDialog(ticket);
      if (!this.window.isDestroyed() && this.window.isVisible()) this.window.focus();
    });
  },
  menu(template) {
    const ticket = this.beginDialog();
    Menu.buildFromTemplate(template).popup({window: this.window, callback: () => this.endDialog(ticket)});
  },
  moreMenu() { const template=moreTemplate(this);if(this.services.extensions){template.push({type:'separator'},{label:'Extensions',submenu:[...this.services.extensions.menu(this),{label:'Manage extensions…',click:()=>this.emit({type:'extension-settings',id:this.id})}]});}this.menu(template); },
  reopenClosedTab() {
    const address = this.closedTabs.pop();
    if (validUrl(address)) this.createView(address);
  },
  tabContext(id) {
    const tab = this.tabs.find(item => item.id === id);
    if (id !== null && !tab) return;
    const address = tab?.view?.webContents.getURL() || tab?.url;
    const expected = structuredClone(this.config.WebBookmarks || []);
    const invoke = async action => {
      if (this.window.isDestroyed()) return;
      if (action === 'reopen') {this.reopenClosedTab();return;}
      if (!tab || !this.tabs.includes(tab)) return;
      if (action === 'close') {await this.closeTab(tab.id);return;}
      if (action === 'close-others') {
        for (const other of [...this.tabs]) {
          if (!this.tabs.includes(tab)) break;
          if (other !== tab) await this.closeTab(other.id);
        }
        return;
      }
      if (action === 'reload') {tab.view?.webContents.reload();return;}
      if (!validUrl(address)) return;
      if (action === 'duplicate') {this.createView(address, false);this.toolbar();}
      if (action === 'copy') await clipboard.writeText(address);
      if (action === 'external') void shell.openExternal(address).catch(() => {});
      if (action === 'bookmark' && this.canEditBookmarks()) {
        if (bookmarkVersion(this.config.WebBookmarks || []) !== bookmarkVersion(expected)) {this.toolbar();return;}
        const saved = bookmarked(expected, address);
        await this.bookmarkRequest(saved ? {type:'bookmark-action',action:'remove',path:saved,expected} :
          {type:'bookmark-add',url:address,title:(tab.title || address).slice(0,256)});
      }
    };
    this.menu(tabTemplate(this, tab, action => {
      void invoke(action).catch(() => {if (!this.window.isDestroyed()) this.emit({type:'warning',message:'The tab action could not be completed.'});});
    }));
  },
  showShortcuts() {
    const ticket = this.beginDialog();
    void dialog.showMessageBox(this.window, {title: 'Keyboard shortcuts', message: 'Keyboard shortcuts',
      detail: 'Ctrl+L  Address\nCtrl+T  New tab\nCtrl+W  Close tab\nCtrl+Shift+T  Reopen closed tab\nCtrl+Tab / Ctrl+Shift+Tab  Switch tab\nCtrl+1…8 / Ctrl+9  Select tab / last tab\nAlt+Left / Alt+Right  Back / Forward\nAlt+Home  Home\nCtrl+R / F5  Reload\nCtrl+Plus / Minus / 0  Zoom\nCtrl+D  Add/remove bookmark\nCtrl+Shift+O  Open in browser\nCtrl+/  Keyboard shortcuts\nEscape  Hide launcher'}).finally(() => this.endDialog(ticket));
  },
  action(action, value) {
    const contents = this.view?.webContents;
    switch (action) {
      case 'position-picker': {
        if(this.detached || !this.canEditBookmarks())break;
        const ticket=this.beginDialog();
        void this.services.request({type:'position-picker',id:this.id}).finally(()=>this.endDialog(ticket));break;
      }
      case 'extensions': if(this.services.extensions)this.menu([...this.services.extensions.menu(this),{type:'separator'},{label:'Manage extensions…',click:()=>this.emit({type:'extension-settings',id:this.id})}]);break;
      case 'extension-open': if(typeof value==='string' && this.services.extensions)void this.services.extensions.open(contents?.session,value,this).catch(()=>this.emit({type:'warning',message:'The extension popup could not open.'}));break;
      case 'extension-context': if(typeof value==='string' && this.services.extensions){
        const item=this.services.extensions.presentation(this).find(item=>item.id===value);
        if(item)this.menu([{label:'Unpin from the header',click:()=>{void this.services.extensions.command({action:'pin',id:item.id,pinned:false},contents.session,this).catch(()=>{});}}]);
        break;
      }
      case 'more': this.moreMenu(); break;
      case 'settings': this.settings(); break;
      case 'hide': this.hide(); break;
      case 'back': if (contents?.navigationHistory.canGoBack()) contents.navigationHistory.goBack(); break;
      case 'forward': if (contents?.navigationHistory.canGoForward()) contents.navigationHistory.goForward(); break;
      case 'reload': contents?.reload(); break;
      case 'home': if (validUrl(this.presentation.homeUrl)) void contents?.loadURL(this.presentation.homeUrl).catch(() => {}); break;
      case 'navigate': if (validUrl(value)) void contents?.loadURL(value).catch(() => {}); break;
      case 'external': if (validUrl(contents?.getURL())) void shell.openExternal(contents.getURL()).catch(() => {}); break;
      case 'address': void this.preference('WebShowAddressBar', !this.config.WebShowAddressBar); break;
      case 'pin': void this.preference('WebPinFlyout', !this.config.WebPinFlyout); break;
      case 'maximize': this.window.isMaximized() ? this.window.unmaximize() : this.window.maximize(); break;
      case 'minimize': if (!this.anchored) this.window.minimize(); break;
      case 'new-tab': this.createView('about:blank'); this.focusAddress(true); break;
      case 'move-tab': {
        if (!value || !Number.isInteger(value.source) || !Number.isInteger(value.target) || typeof value.after !== 'boolean') break;
        const from=this.tabs.findIndex(tab=>tab.id===value.source), target=this.tabs.findIndex(tab=>tab.id===value.target);
        if (from<0 || target<0 || from===target) break;
        let index=target+(value.after?1:0);if(index>from)index--;
        this.tabs.splice(index,0,this.tabs.splice(from,1)[0]);this.toolbar();break;
      }
      case 'move-bookmark': {
        if (!this.canEditBookmarks()) break;
        if (Array.isArray(value?.source) && !this.bookmarkOverlay.validDrop(value)) break;
        const drop=bookmarkDrop(this.config,value);
        if (drop) void this.bookmarkRequest({type:'bookmark-action',...drop});
        else this.toolbar();
        break;
      }
      case 'tab-context': if (value === null || Number.isInteger(value)) this.tabContext(value); break;
      case 'reopen-tab': this.reopenClosedTab(); break;
      case 'select-tab': if (Number.isInteger(value)) this.selectTab(value); break;
      case 'close-tab': if (Number.isInteger(value)) void this.closeTab(value); break;
      case 'zoom-in': void this.preference('WebZoomPercent', stepZoom(this.config.WebZoomPercent, 1)); break;
      case 'zoom-out': void this.preference('WebZoomPercent', stepZoom(this.config.WebZoomPercent, -1)); break;
      case 'zoom-reset': void this.preference('WebZoomPercent', 100); break;
    }
  },
  toggleBookmark() {
    const contents = this.view?.webContents, url = contents?.getURL();
    if (!this.canEditBookmarks() || !validUrl(url)) return;
    const expected = structuredClone(this.config.WebBookmarks || []), saved = bookmarked(expected, url);
    void this.bookmarkRequest(saved ? {type:'bookmark-action',action:'remove',path:saved,expected} :
      {type:'bookmark-add',url,title:contents.getTitle().slice(0,256)});
  },
  focusAddress(temporary = false) {
    if (!temporary && !this.config.WebShowAddressBar) void this.preference('WebShowAddressBar', true);
    this.window.webContents.focus();
    this.window.webContents.send('focus-address');
  },
  shortcut(input) {
    const key = input.key.toLowerCase();
    if (input.control && !input.alt) {
      if (key === 'l') { this.focusAddress(); return true; }
      if (key === 't') { this.action(input.shift ? 'reopen-tab' : 'new-tab'); return true; }
      if (key === 'w') { void this.closeTab(this.activeTab?.id); return true; }
      if (key === 'tab') {
        if (!this.tabs.length) return true;
        const index = this.tabs.indexOf(this.activeTab);
        this.selectTab(this.tabs[(index + (input.shift ? -1 : 1) + this.tabs.length) % this.tabs.length].id); return true;
      }
      if (!input.shift && /^[1-9]$/.test(key)) {
        const index = key === '9' ? this.tabs.length - 1 : Number(key) - 1;
        if (this.tabs[index]) this.selectTab(this.tabs[index].id);
        return true;
      }
      if (!input.shift && key === 'd') {this.toggleBookmark();return true;}
      if (input.shift && key === 'o') {this.action('external');return true;}
      if (key === '/' || key === '?') {this.showShortcuts();return true;}
      if (key === 'r') { this.action('reload'); return true; }
      if (['+', '=', '-', '0'].includes(key)) {
        this.action(key === '0' ? 'zoom-reset' : key === '-' ? 'zoom-out' : 'zoom-in'); return true;
      }
    }
    if (input.alt && ['arrowleft', 'arrowright', 'home'].includes(key)) {
      this.action(key === 'home' ? 'home' : key === 'arrowleft' ? 'back' : 'forward'); return true;
    }
    if (key === 'f5') { this.action('reload'); return true; }
    if (key === 'escape' && this.fullscreen?.state) {this.fullscreen.exitPage();return true;}
    if (key === 'escape') { this.hide(); return true; }
    return false;
  },
};
