'use strict';
const {app, session, desktopCapturer, webContents} = require('electron');
const crypto = require('node:crypto');
const path = require('node:path');
const {PermissionStore}=require('./permission_store.cjs');
const {installWorkerNotifications}=require('./worker_notifications.cjs');
const {browserUserAgent,googleSignIn,applyNavigationIdentity} = require('./browser_identity.cjs');
const {requestContents} = require('./request_contents.cjs');
const {PageNotifications}=require('./page_notifications.cjs');

/** Persistent browser partitions contain no portable credentials or settings. */
class Profiles {
  constructor(request, ownerFor, options = {}) {
    this.request = request;
    this.permissionStore=options.permissionStore || new PermissionStore(path.join(app.getPath('userData'),'site-permissions.json'));
    this.permissionError=options.onPermissionError || (()=>{});
    this.ownerFor = ownerFor;
    this.installed = new Set();
    this.grants = new Map();
    this.notifications = new Map();
    this.notificationBridges=new Map();
    this.notificationPages=new Map();
    this.profileKeys=new WeakMap();
    this.notificationPrompts=new WeakMap();
  }

  requestOwners(profile, details) {
    const contents = requestContents(details, id => webContents.fromId(id));
    const owner = contents ? this.ownerFor(contents) : null;
    if (owner) return [owner];
    // Worker requests belong to a session, not necessarily a live renderer. Protect all
    // matching launcher views conservatively until that upload/response completes.
    return webContents.getAllWebContents().filter(candidate => !candidate.isDestroyed() && candidate.session === profile)
      .map(candidate => this.ownerFor(candidate)).filter(Boolean);
  }

  resetPermissions(config) {
    const profile = this.get(config);
    this.permissionStore.clear(config.WebSharedProfile ? 'shared' : crypto.createHash('sha256').update(config.Id).digest('hex'));
    this.notifications.get(profile)?.clear();
    this.notificationBridges.get(profile)?.reset();
    void this.refreshNotificationPages(profile,true);
    const ids = new Set(webContents.getAllWebContents().filter(contents => contents.session === profile).map(contents => contents.id));
    for (const key of this.grants.keys()) if (ids.has(Number(key.split(':')[0]))) this.grants.delete(key);
  }

  async clearData(config, owners) {
    const profile = this.get(config);
    const affected = owners.filter(owner => this.get(owner.config) === profile);
    if (affected.some(owner => owner.busy)) throw new Error('Finish active media and transfers in this sign-in profile before clearing its browsing data.');
    for (const owner of affected) {
      owner.maintenance = true;
      for (const tab of owner.tabs) {
        if (!tab.view) continue;
        if (tab === owner.activeTab) owner.window.contentView.removeChildView(tab.view);
        tab.unloading = true;
        const view = tab.view;
        tab.view = null;
        view.webContents.close({waitForBeforeUnload:false});
      }
      owner.view = null;
    }
    try {
      await profile.clearStorageData();
      await profile.clearCache();
      this.resetPermissions(config);
    } finally {
      for (const owner of affected) {
        owner.maintenance = false;
        if (!owner.window.isDestroyed()) {
          if (owner.window.isVisible() || owner.config.WebHiddenPolicy === 2) owner.selectTab(owner.activeTab.id);
          if (owner.config.WebHiddenPolicy === 2) {
            for (const tab of owner.tabs) if (!tab.view) owner.createView(tab.url, false, {}, tab);
          }
          owner.toolbar();
        }
      }
    }
  }

  get(config) {
    const key = config.WebSharedProfile ? 'shared' : crypto.createHash('sha256').update(config.Id).digest('hex');
    const profile = session.fromPartition('persist:launcher-' + key);
    if (this.installed.has(profile)) return profile;
    profile.setUserAgent(browserUserAgent());
    this.installed.add(profile);
    this.profileKeys.set(profile,key);
    const notificationGrants = new Map(this.permissionStore.entries(key));
    this.notifications.set(profile, notificationGrants);
    this.notificationBridges.set(profile,installWorkerNotifications(profile,notificationGrants,this.ownerFor,this.permissionError));
    profile.setPermissionCheckHandler((contents, permission, origin) => {
      let normalized;
      try {
        normalized = new URL(origin).origin;
      } catch {
        return false;
      }
      // A service worker has no WebContents, but uses its launcher's existing origin grant.
      if (permission === 'notifications') return notificationGrants.get(normalized) === true;
      const allowed = this.grants.get(`${contents?.id}:${normalized}:${permission}`) === true;
      if (allowed && ['media', 'display-capture'].includes(permission)) {
        const owner = this.ownerFor(contents);
        if (owner) {
          owner.lifecycle.capturePermission = true;
          owner.lifecycle.update();
        }
      }
      return allowed;
    });
    profile.setPermissionRequestHandler(async (contents, permission, callback, details) => {
      const owner = this.ownerFor(contents);
      let origin;
      try {
        origin = new URL(details.requestingUrl || contents.getURL()).origin;
      } catch {
        callback(false);
        return;
      }
      if (!owner || !['media', 'display-capture', 'notifications', 'geolocation', 'fullscreen', 'clipboard-sanitized-write'].includes(permission)) {
        callback(false);
        return;
      }
      if(permission==='notifications'){
        callback(await this.requestNotifications(contents,origin)==='granted');return;
      }
      const ticket = owner.beginDialog();
      let allowed;
      try {
        allowed = await this.request({type: 'permission', id: owner.id, permission,
          origin, mediaTypes: details.mediaTypes || []});
      } finally {
        owner.endDialog(ticket);
      }
      if (contents.isDestroyed()) {
        callback(false);
        return;
      }
      this.grants.set(`${contents.id}:${origin}:${permission}`, allowed === true);
      if (allowed && permission === 'media') {
        owner.lifecycle.capturePermission = true;
        await owner.lifecycle.update();
      }
      callback(allowed === true);
    });
    profile.setDisplayMediaRequestHandler(async (capture, callback) => {
      // On Wayland, PipeWire's portal presents the system chooser and returns its one source.
      // Never select an arbitrary desktop source on X11 where that contract does not apply.
      let contents;
      try { if (capture.frame && !capture.frame.detached) contents = webContents.fromFrame(capture.frame); }
      catch { callback(null); return; }
      const owner = contents && !contents.isDestroyed() ? this.ownerFor(contents) : null;
      if (!owner || process.env.XDG_SESSION_TYPE !== 'wayland') {
        callback(null);
        return;
      }
      const ticket = owner.beginDialog();
      try {
        const sources = await desktopCapturer.getSources({types: ['screen', 'window']});
        if (sources.length !== 1) {
          callback(null);
          return;
        }
        owner.lifecycle.capturePermission = true;
        await owner.lifecycle.update();
        callback({video: sources[0]});
      } catch {
        callback(null);
      } finally {
        owner.endDialog(ticket);
      }
    });
    const uploads = new Map();
    profile.webRequest.onBeforeSendHeaders((details,callback)=>{
      const headers={...details.requestHeaders};
      if(details.resourceType==='mainFrame'||googleSignIn(details.url)){
        for(const name of Object.keys(headers))if(name.toLowerCase()==='user-agent')delete headers[name];
        headers['User-Agent']=googleSignIn(details.url)?'Chrome':browserUserAgent();
      }
      callback({requestHeaders:headers});
    });
    profile.webRequest.onBeforeRequest((details, callback) => {
      if (['POST', 'PUT', 'PATCH'].includes(details.method)) {
        const owners = this.requestOwners(profile, details);
        uploads.set(details.id, owners);
        for (const owner of owners) {
          owner.lifecycle.uploads.add(details.id);
          void owner.lifecycle.update();
        }
      }
      const contents=details.resourceType==='mainFrame'?requestContents(details,id=>webContents.fromId(id)):null;
      if(!contents){callback(null);return;}
      const timer=setTimeout(()=>finish(),2000);let completed=false;
      const finish=()=>{if(completed)return;completed=true;clearTimeout(timer);callback(null);};
      void applyNavigationIdentity(contents,details.url).then(finish,finish);
    });
    const complete = details => {
      const owners = uploads.get(details.id) || [];
      uploads.delete(details.id);
      for (const owner of owners) {
        owner.lifecycle.uploads.delete(details.id);
        void owner.lifecycle.update();
      }
    };
    profile.webRequest.onCompleted(complete);
    profile.webRequest.onErrorOccurred(complete);
    profile.on('will-download', async (_event, item, contents) => {
      const owner = this.ownerFor(contents);
      if (!owner) {
        item.cancel();
        return;
      }
      owner.lifecycle.downloads.add(item);
      owner.lifecycle.update();
      item.once('done', () => {
        owner.lifecycle.downloads.delete(item);
        owner.lifecycle.update();
      });
      item.setSaveDialogOptions({title: 'Save download'});
    });
    void this.extensions?.register(profile);
    return profile;
  }

  async requestNotifications(contents,origin){
    if(!contents||contents.isDestroyed())return 'denied';
    const profile=contents.session,grants=this.notifications.get(profile),owner=this.ownerFor(contents);
    try{if(!owner||new URL(contents.getURL()).origin!==origin||!grants)return 'denied';}catch{return 'denied';}
    if(grants.has(origin))return grants.get(origin)?'granted':'denied';
    let prompts=this.notificationPrompts.get(profile);if(!prompts){prompts=new Map();this.notificationPrompts.set(profile,prompts);}
    if(prompts.has(origin))return prompts.get(origin);
    const pending=(async()=>{
      const ticket=owner.beginDialog();let allowed;
      try{allowed=await this.request({type:'permission',id:owner.id,permission:'notifications',origin});}
      finally{owner.endDialog(ticket);}
      if(contents.isDestroyed())return 'denied';
      try{
        if(new URL(contents.getURL()).origin!==origin)return 'denied';
        this.permissionStore.set(this.profileKeys.get(profile),origin,allowed===true);
        grants.set(origin,allowed===true);await this.refreshNotificationPages(profile);
        return allowed===true?'granted':'denied';
      }catch{this.permissionError('Notification permission could not be saved. Existing permissions were preserved.');return 'denied';}
    })();prompts.set(origin,pending);
    try{return await pending;}finally{prompts.delete(origin);}
  }

  prepareContents(contents){
    if(this.notificationPages.has(contents.id))return this.notificationPages.get(contents.id).ready;
    const profile=contents.session;
    const client=new PageNotifications(contents,{
      decisions:()=>Object.fromEntries(this.notifications.get(profile)||[]),
      permission:origin=>this.requestNotifications(contents,origin),
      activate:()=>{const owner=this.ownerFor(contents);if(owner?.focusNotification)owner.focusNotification();else owner?.show();},
    });
    this.notificationPages.set(contents.id,{client,profile,ready:client.initialize()});
    contents.once('destroyed',()=>this.notificationPages.delete(contents.id));
    return this.notificationPages.get(contents.id).ready;
  }

  async refreshNotificationPages(profile,close=false){
    await Promise.allSettled([...this.notificationPages.values()].filter(value=>value.profile===profile).map(value=>{
      if(close)value.client.close();return value.client.update();
    }));
  }

  ready(config){const profile=this.get(config);return this.extensions?.register(profile)||Promise.resolve();}
}
module.exports = {Profiles};
