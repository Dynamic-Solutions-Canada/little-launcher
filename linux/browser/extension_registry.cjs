'use strict';
const {ipcMain,webContents}=require('electron');
const registrations=new Map(),active=new Map(),listeners=new Set(),handlers=new Map();
const emit=event=>{for(const listener of listeners)listener(event);};
const registry={
 getRegistrationForWebContents:id=>registrations.get(id)||null,
 waitForRegistration:async id=>registrations.get(id)||null,
 subscribe(listener){listeners.add(listener);return()=>listeners.delete(listener);},
 unregisterWebContents(id){const old=registrations.get(id);if(!old)return;registrations.delete(id);if(active.get(old.hostWebContentsId)===id)active.delete(old.hostWebContentsId);emit({type:'unregistered',...old});},
};
function registerTab(contents,owner){
 const old=registrations.get(contents.id);
 if(old?.hostWebContentsId===owner.window.webContents.id)return;
 if(old)registry.unregisterWebContents(contents.id);
 const entry={webContentsId:contents.id,hostWebContentsId:owner.window.webContents.id,browserId:String(contents.id),workspaceId:owner.id};
 registrations.set(contents.id,entry);contents.once('destroyed',()=>registry.unregisterWebContents(contents.id));emit({type:'registered',...entry});
}
function activateTab(contents,owner){
 registerTab(contents,owner);const host=owner.window.webContents.id;
 if(active.get(host)!==contents.id){active.set(host,contents.id);emit({type:'active-changed',hostWebContentsId:host,browserId:String(contents.id),workspaceId:owner.id,webContentsId:contents.id});}
}
function registerSessionHandler(profile,channel,handler){
 if(!handlers.has(channel)){
  handlers.set(channel,new Map());ipcMain.handle(channel,(event,input)=>{
   const target=handlers.get(channel).get(event.sender.session);
   if(!target)throw Error('No extension host for this session');return target(event,input);
  });
 }
 handlers.get(channel).set(profile,handler);
}
module.exports={registerTab,activateTab,registerSessionHandler,getPaseoBrowserWebviewRegistry:()=>registry,
 isPreparedPaseoBrowserWebContents:contents=>registrations.has(contents.id),
 getActivePaseoBrowserWebContentsForHostWindow:id=>active.has(id)?webContents.fromId(active.get(id)):null,
 getPaseoBrowserWebContentsForHostWindow:(browserId,host)=>{const entry=registrations.get(Number(browserId));return entry?.hostWebContentsId===host?webContents.fromId(entry.webContentsId):null;}};
