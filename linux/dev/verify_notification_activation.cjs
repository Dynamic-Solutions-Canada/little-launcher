'use strict';
const assert=require('node:assert/strict');
const {app,Notification,BrowserWindow}=require('electron');
const {Profiles}=require('../browser/profiles.cjs');
const {WebLauncher}=require('../browser/window.cjs');
const fs=require('node:fs'),http=require('node:http'),cp=require('node:child_process');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');app.setPath('userData',fs.mkdtempSync('/work/notification-activation-'));app.setDesktopName('io.github.RyanEwen.LittleLauncher.Web.desktop');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));async function until(f){for(let i=0;i<100;i++){if(await f())return;await sleep(50);}throw Error('Timed out');}
const notices=[];const originalShow=Notification.prototype.show;Notification.prototype.show=function(){notices.push(this);return originalShow.call(this);};
if(process.argv.includes('--trace-bindings')){const {PageNotifications}=require('../browser/page_notifications.cjs');const handle=PageNotifications.prototype.handle;PageNotifications.prototype.handle=function(event){console.error('BINDING',JSON.parse(event.payload).method,JSON.stringify(this.contexts.get(event.executionContextId)),this.contents.getURL());return handle.call(this,event);};}
let owner,server;const events=[],created=[];app.on('browser-window-created',(_event,window)=>created.push(window));
app.whenReady().then(async()=>{
 server=http.createServer((req,res)=>{if(req.url==='/sw.js'){res.setHeader('Content-Type','application/javascript');res.end(`self.addEventListener('install',()=>self.skipWaiting());self.addEventListener('activate',e=>e.waitUntil(clients.claim()));self.addEventListener('notificationclick',e=>e.waitUntil((async()=>{const all=await clients.matchAll({type:'window',includeUncontrolled:true});if(e.notification.data.open)await clients.openWindow('/inbox');else if(all[0])await all[0].focus();for(const client of await clients.matchAll({includeUncontrolled:true}))client.postMessage({clicked:true});})()));`);return;}res.setHeader('Content-Type','text/html');res.end('<title>Activation fixture</title><script>navigator.serviceWorker.addEventListener("message",e=>{window.clicked=e.data.clicked})</script>');});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const url=`http://127.0.0.1:${server.address().port}/`;
 const services={isQuitting:()=>true,emit:value=>events.push(value),request:async()=>true};services.profiles=new Profiles(services.request,contents=>{const tab=owner?.tabs.find(t=>t.view?.webContents===contents);if(!tab)return;const facade=Object.create(owner);Object.defineProperty(facade,'lifecycle',{value:tab.lifecycle});Object.defineProperty(facade,'focusNotification',{value:()=>owner.activateNotification(tab.id)});Object.defineProperty(facade,'notificationClient',{value:(action,url)=>owner.notificationClient(action,url,tab.id)});return facade;});
 owner=new WebLauncher({Id:'notify-activation',Name:'Activation fixture',WebRegularWindow:!process.argv.includes('--flyout'),WebHiddenPolicy:2,WebAnchor:8,WebReloadOnShow:true},{window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,{homeUrl:url,showBookmarkBar:false,positionSupported:process.argv.includes('--flyout')},services);
 owner.view.webContents.on('console-message',event=>console.error('RENDERER',event.message));
 await sleep(100);for(const entry of services.profiles.notificationPages.values())await entry.ready;
 await until(()=>!owner.view.webContents.isLoading()&&owner.view.webContents.getURL()===url);
 const initial=await owner.view.webContents.executeJavaScript('(()=>{try{return {notification:Notification.permission}}catch(e){return {error:String(e)}}})()');
 const initialQuery=await owner.view.webContents.executeJavaScript('navigator.permissions.query({name:"notifications"}).then(s=>s.state).catch(e=>String(e))');
 await owner.view.webContents.executeJavaScript('Notification.requestPermission()',true);
 await owner.view.webContents.executeJavaScript('(async()=>{await navigator.serviceWorker.register("/sw.js");await navigator.serviceWorker.ready;})()');
 const results=[];
 for(const open of [false,true]){
  if(!process.argv.includes('--preloaded')||results.length)owner.show();await sleep(100);owner.hide();await owner.view.webContents.executeJavaScript(`navigator.serviceWorker.ready.then(r=>r.showNotification('Activation fixture',{requireInteraction:true,data:{open:${open}}}))`,true);
  const notice=notices.at(-1);assert.ok(notice);notice.emit('click');await sleep(1500);
  results.push({openWindow:open,launcherVisible:owner.window.isVisible(),launcherFocused:owner.window.isFocused(),windowCount:BrowserWindow.getAllWindows().length,titles:BrowserWindow.getAllWindows().map(w=>w.getTitle()),workerClick:await owner.view.webContents.executeJavaScript('!!window.clicked')});
 }
 let nativePage;
 if(process.argv.includes('--native-click')){
  owner.show();await sleep(100);owner.hide();
  await owner.view.webContents.executeJavaScript('window.nativeNotice=new Notification("Native page activation fixture");nativeNotice.onclick=()=>{window.pageClicked=true;window.focus()};true',true);
  await sleep(300);cp.execFileSync('gdbus',['call','--session','--dest','org.freedesktop.Notifications','--object-path','/QA','--method','org.littlelauncher.NotificationQA.Click']);await sleep(1200);
  nativePage={launcherVisible:owner.window.isVisible(),launcherFocused:owner.window.isFocused(),windowCount:BrowserWindow.getAllWindows().length,titles:BrowserWindow.getAllWindows().map(w=>w.getTitle()),clickReceived:await owner.view.webContents.executeJavaScript('!!window.pageClicked')};
 }
 assert.equal(initial.notification,'default');assert.equal(initialQuery,'prompt');for(const result of results){assert.equal(result.launcherVisible,true);assert.equal(result.launcherFocused,true);assert.equal(result.windowCount,1);assert.equal(result.workerClick,true);}if(nativePage){assert.equal(nativePage.launcherVisible,true);assert.equal(nativePage.launcherFocused,true);assert.equal(nativePage.windowCount,1);assert.equal(nativePage.clickReceived,true);}
 const bridgeAbsent=await owner.view.webContents.executeJavaScript('typeof process==="undefined" && typeof require==="undefined" && typeof window.launcher==="undefined" && !Object.getOwnPropertyNames(window).some(name=>name.startsWith("__llNotification_"))');assert.equal(bridgeAbsent,true);
 const result={passed:true,noPageNodeOrGeneralBridge:bridgeAbsent,nativePage,initialNotificationPermission:initial.notification,initialPermissionQuery:initialQuery,scenarios:results};fs.writeFileSync('/work/notification-activation.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{for(const window of BrowserWindow.getAllWindows())window.destroy();server?.close();app.exit(process.exitCode||0);});
