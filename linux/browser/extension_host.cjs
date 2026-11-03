'use strict';
const {app,nativeImage}=require('electron');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const log=require('electron-log');log.transports.file.level=false;log.transports.console.level=false;
const {registerBrowserExtensionCompatibility}=require('./vendor/generated/extensions/compatibility.js');
const {getExtensionPopupUrl,getExtensionToolbarMetadata,readExtensionToolbarIcon}=require('./vendor/generated/extensions/manifest.js');
const {openBrowserExtensionDocumentWindow}=require('./vendor/generated/extensions/document-window.js');

/** Shared code choices, isolated extension/vault data in each persistent sign-in profile. */
class ExtensionHost {
 constructor(emit){
  this.emit=emit;this.sessions=new Map();this.popups=new Map();this.entries=[];this.errors=new Map();this.corrupt=false;this.icons=new Map();this.changed=()=>{};
  this.root=path.join(app.getPath('userData'),'extensions','packages');this.file=path.join(app.getPath('userData'),'extension-catalog.json');
  try{
   const data=JSON.parse(fs.readFileSync(this.file,'utf8'));
   if(data.version!==1||!Array.isArray(data.extensions)||data.extensions.length>64)throw Error();
   for(const entry of data.extensions)if(!entry||typeof entry.path!=='string'||typeof entry.enabled!=='boolean'||!this.packagePath(entry.path))throw Error();
   this.entries=data.extensions;
  }catch(error){if(error.code!=='ENOENT')this.corrupt=true;}
 }
 packagePath(candidate){try{const root=path.resolve(this.root),resolved=fs.realpathSync(candidate);return resolved.startsWith(root+path.sep)&&fs.lstatSync(candidate).isDirectory()&&!fs.lstatSync(candidate).isSymbolicLink()?resolved:null;}catch{return null;}}
 save(entries){
  if(this.corrupt)throw Error('The extension catalog is unreadable. Its original file was preserved.');
  const temporary=this.file+'.'+crypto.randomUUID()+'.tmp';fs.mkdirSync(path.dirname(this.file),{recursive:true,mode:0o700});
  let fd;try{fd=fs.openSync(temporary,'wx',0o600);fs.writeFileSync(fd,JSON.stringify({version:1,extensions:entries})+'\n');fs.fsyncSync(fd);fs.closeSync(fd);fd=null;fs.renameSync(temporary,this.file);this.entries=entries;}finally{if(fd!==null&&fd!==undefined)fs.closeSync(fd);try{fs.unlinkSync(temporary);}catch{}}
 }
 register(profile){
  if(this.sessions.has(profile)){const existing=this.sessions.get(profile);if(existing.suspended){existing.suspended=false;existing.ready=this.restore(profile);}return existing.ready;}
  const state={compat:registerBrowserExtensionCompatibility(profile,{onStorageFailure:(id)=>{this.errors.set(id,'Extension background storage stopped. Disable and re-enable it to retry.');},onActionChanged:()=>this.changed()}),ready:null};
  this.sessions.set(profile,state);
  this.popups.set(profile,new Map());
  profile.extensions.on('extension-unloaded',(_event,extension)=>{const popup=this.popups.get(profile)?.get(extension.id);if(popup&&!popup.isDestroyed())popup.destroy();this.popups.get(profile)?.delete(extension.id);});
  state.ready=this.restore(profile);return state.ready;
 }
 async restore(profile){
  for(const entry of this.entries.filter(item=>item.enabled)){
   if(this.sessions.get(profile).suspended)break;
   try{const loaded=await this.load(profile,entry.path);if(this.sessions.get(profile).suspended)profile.extensions.removeExtension(loaded.id);}
   catch{this.errors.set(entry.path,'The extension could not start in this profile.');}
  }
 }
 suspend(profile){
  const state=this.sessions.get(profile);if(!state)return;state.suspended=true;
  for(const extension of profile.extensions.getAllExtensions())profile.extensions.removeExtension(extension.id);
 }
 async load(profile,directory){
  const existing=profile.extensions.getAllExtensions().find(item=>item.path===directory);if(existing)return existing;
  const extension=await profile.extensions.loadExtension(directory,{allowFileAccess:false});
  try {await Promise.race([this.sessions.get(profile).compat.waitUntilReady(extension),new Promise((_,reject)=>{const timer=setTimeout(()=>reject(Error('Extension startup timed out')),20000);timer.unref();})]);}catch(error){profile.extensions.removeExtension(extension.id);throw error;}
  if(!this.icons.has(directory)){
   const metadata=getExtensionToolbarMetadata(extension.manifest,extension.name);
   const bytes=await readExtensionToolbarIcon(directory,metadata.iconPath);
   const image=bytes?nativeImage.createFromBuffer(bytes):null;
   this.icons.set(directory,image&&!image.isEmpty()?image.resize({width:32,height:32}).toDataURL():null);
  }
  this.changed();return extension;
 }
 list(profile){return this.entries.map(entry=>{const loaded=profile.extensions.getAllExtensions().find(item=>item.path===entry.path);return {...entry,id:loaded?.id||entry.id,name:loaded?.name||entry.name,version:loaded?.version||entry.version,running:!!loaded,error:this.errors.get(entry.path)||this.errors.get(entry.id)||''};});}
 async command(message,profile,owner){
  if(this.corrupt)throw Error('Extension catalog is unreadable');
  await this.register(profile);
  if(message.action==='add'){
   const directory=this.packagePath(message.path);if(!directory)throw Error('Choose a managed extension package.');
   if(!this.entries.some(item=>item.path===directory)){
    if(this.entries.length>=64)throw Error('Too many extensions are installed.');
    const extension=await this.load(profile,directory);
    try{this.save([...this.entries,{path:directory,id:extension.id,name:extension.name,version:extension.version,enabled:true}]);}
    catch(error){profile.extensions.removeExtension(extension.id);throw error;}
    for(const other of this.sessions.keys())if(other!==profile&&!this.sessions.get(other).suspended)try{await this.load(other,directory);}catch{this.errors.set(directory,'Could not load in another sign-in profile.');}
   }
  } else if(message.action==='enable'||message.action==='remove'){
   const entry=this.entries.find(item=>item.path===message.path);if(!entry)throw Error('The selected extension is no longer installed.');
   const next=message.action==='remove'?this.entries.filter(item=>item!==entry):this.entries.map(item=>item===entry?{...item,enabled:message.enabled===true}:item);
   this.save(next);
   for(const current of this.sessions.keys()){
    const loaded=current.extensions.getAllExtensions().find(item=>item.path===entry.path);
    if(loaded)current.extensions.removeExtension(loaded.id);
    if(message.action==='enable'&&message.enabled&&!this.sessions.get(current).suspended)try{await this.load(current,entry.path);this.errors.delete(entry.path);}catch{this.errors.set(entry.path,'The extension could not start.');}
   }
  } else if(message.action==='pin'){
   const selected=this.list(profile).find(item=>item.id===message.id);
   const entry=this.entries.find(item=>item.path===selected?.path);if(!entry)throw Error('The extension is no longer installed.');
   this.save(this.entries.map(item=>item===entry?{...item,pinned:message.pinned===true}:item));
  } else if(message.action==='open'){
   await this.open(profile,message.id,owner);
  } else if(message.action!=='list')throw Error('Unknown extension action.');
  this.changed();return this.list(profile);
 }
 async open(profile,id,owner){
  if(!owner?.view||owner.window.isDestroyed())throw Error('Open this launcher first, then open its extension.');
  const extension=profile.extensions.getExtension(id);if(!extension)throw Error('The extension is not enabled.');
  const url=getExtensionPopupUrl(id,extension.manifest);if(!url)throw Error('This extension has no popup.');
  const previous=this.popups.get(profile)?.get(id);
  if(previous&&!previous.isDestroyed()){if(previous.getParentWindow()===owner.window){previous.show();previous.focus();return;}previous.destroy();}
  const ticket=owner.beginDialog();
  try{
   const window=await openBrowserExtensionDocumentWindow({owner:owner.window,profile,extension,url,width:420,height:640,onCreated:popup=>this.sessions.get(profile).compat.registerPopup(popup.webContents,owner.view.webContents)});
   this.popups.get(profile).set(id,window);
   window.once('closed',()=>{if(this.popups.get(profile)?.get(id)===window)this.popups.get(profile).delete(id);owner.endDialog(ticket);});
  }catch(error){owner.endDialog(ticket);throw error;}
 }
 presentation(owner){
  const profile=owner.view?.webContents.session;if(!profile)return [];
  return this.list(profile).map(item=>{
   const extension=profile.extensions.getExtension(item.id);
   return {id:item.id,name:item.name,pinned:item.pinned===true,running:item.running,
    popup:!!extension&&!!getExtensionPopupUrl(item.id,extension.manifest),icon:this.icons.get(item.path)||null,
    badge:this.sessions.get(profile)?.compat.getActionBadge(item.id,owner.view.webContents.id)?.text?.slice(0,4)||''};
  });
 }
 menu(owner){
  const profile=owner.view?.webContents.session;
  const invoke=message=>{void this.command(message,profile,owner).catch(()=>this.emit({type:'warning',message:'The extension action could not complete.'}));};
  return profile?this.presentation(owner).map(item=>({label:item.name,submenu:[
   item.popup?{label:'Open',click:()=>invoke({action:'open',id:item.id})}:{label:item.running?'Runs in the background':'Disabled',enabled:false},
   {label:'Pin to the header',type:'checkbox',checked:item.pinned,enabled:item.popup,click:()=>invoke({action:'pin',id:item.id,pinned:!item.pinned})}
  ]})):[];
 }
}
module.exports={ExtensionHost};
