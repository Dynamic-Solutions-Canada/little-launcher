'use strict';
// Generic extension storage/lifecycle qualification. No vendor, vault or account behavior is assumed.
const assert=require('node:assert/strict');
const {app}=require('electron');
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
const root=process.argv.find(value=>value.startsWith('--data='))?.slice(7);assert.ok(root?.startsWith('/work/'));
app.setPath('userData',root);const second=process.argv.includes('--second');
const {Profiles}=require('../browser/profiles.cjs'),{ExtensionHost}=require('../browser/extension_host.cjs'),{WebLauncher}=require('../browser/window.cjs');
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let server,host;const owners=[];
app.whenReady().then(async()=>{
 const records=[];const services={isQuitting:()=>true,request:async()=>false,emit:value=>records.push(value)};
 host=new ExtensionHost(services.emit);services.extensions=host;host.changed=()=>{for(const owner of owners)owner.toolbar();};
 services.profiles=new Profiles(services.request,contents=>owners.find(owner=>owner.tabs.some(tab=>tab.view?.webContents===contents)));services.profiles.extensions=host;
 const directory=path.join(root,'extensions/packages/generic-storage-fixture');
 if(!second){
  fs.mkdirSync(directory,{recursive:true});
  fs.writeFileSync(path.join(directory,'manifest.json'),JSON.stringify({manifest_version:3,name:'Generic storage fixture',version:'1.0',permissions:['storage'],action:{default_popup:'popup.html'},background:{service_worker:'worker.js'}}));
  fs.writeFileSync(path.join(directory,'popup.html'),'<title>Generic extension</title><p>Generic extension storage fixture</p>');
  fs.writeFileSync(path.join(directory,'worker.js'),'chrome.runtime.onInstalled.addListener(()=>{});');
 }
 const a={Id:'profile-owner-a',Kind:1,Name:'Shared fixture A',WebSharedProfile:true};
 const b={Id:'profile-owner-b',Kind:1,Name:'Shared fixture B',WebSharedProfile:true};
 const c={Id:'profile-owner-c',Kind:1,Name:'Private fixture C',WebSharedProfile:false};
 const shared=services.profiles.get(a);assert.equal(services.profiles.get(b),shared);
 const entries=await host.command({action:second?'list':'add',path:directory},shared,null);assert.equal(entries.length,1);
 const id=entries[0].id;
 if(second)assert.equal(id,JSON.parse(fs.readFileSync(path.join(root,'expected.json'))).id,'Extension identity survives app restart');
 else fs.writeFileSync(path.join(root,'expected.json'),JSON.stringify({id}));
 server=http.createServer((_req,res)=>{res.setHeader('Content-Type','text/html');res.end('<title>Profile fixture</title>Profile fixture');});await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 function launcher(config){const owner=new WebLauncher(config,{window:'#eeeeee',text:'#000000',button:'#dddddd',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,{homeUrl:`http://127.0.0.1:${server.address().port}/`,showBookmarkBar:false},services);owners.push(owner);owner.show();return owner;}
 async function popup(owner){const profile=owner.view.webContents.session;await services.profiles.ready(owner.config);await host.open(profile,id,owner);const result=host.popups.get(profile).get(id);assert.ok(result);return result;}
 const first=launcher(a),next=launcher(b);assert.equal(first.view.webContents.session,next.view.webContents.session);
 let view=await popup(first);
 if(!second){await view.webContents.executeJavaScript('Promise.all([chrome.storage.local.set({fixtureLogin:"persisted-profile-value"}),chrome.storage.session.set({fixtureUnlock:"session-value"})])');}
 const persisted=await view.webContents.executeJavaScript('chrome.storage.local.get("fixtureLogin")');assert.equal(persisted.fixtureLogin,'persisted-profile-value');
 const session=await view.webContents.executeJavaScript('chrome.storage.session.get("fixtureUnlock")');assert.equal(session.fixtureUnlock,second?undefined:'session-value');
 view=await popup(next);
 assert.equal((await view.webContents.executeJavaScript('chrome.storage.local.get("fixtureLogin")')).fixtureLogin,'persisted-profile-value');
 assert.equal((await view.webContents.executeJavaScript('chrome.storage.session.get("fixtureUnlock")')).fixtureUnlock,second?undefined:'session-value');
 const privateOwner=launcher(c);await services.profiles.ready(c);
 const privateView=await popup(privateOwner);
 assert.equal((await privateView.webContents.executeJavaScript('chrome.storage.local.get("fixtureLogin")')).fixtureLogin,undefined);
 assert.equal((await privateView.webContents.executeJavaScript('chrome.storage.session.get("fixtureUnlock")')).fixtureUnlock,undefined);
 const result={passed:true,secondProcess:second,genericExtension:true,sharedProfileLocalStorageShared:true,sharedProfileSessionStorageShared:!second,persistentStorageSurvivesRestart:second,sessionStorageClearedOnRestart:second,privateProfileIsolated:true,extensionIdentityStable:true,noAccountUsed:true};
 fs.writeFileSync(path.join(root,second?'second.json':'first.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));
}).catch(error=>{console.error(error.stack);process.exitCode=1;}).finally(()=>{for(const owner of owners){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}for(const state of host?.sessions.values()||[])state.compat.disposeStorageObservers();server?.close();app.exit(process.exitCode||0);});
