'use strict';
const {app,BrowserWindow,Notification}=require('electron');
const {Profiles}=require('../browser/profiles.cjs');
const fs=require('node:fs'),http=require('node:http'),cp=require('node:child_process');
const assert=require('node:assert/strict');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');app.setPath('userData',fs.mkdtempSync('/work/notification-delivery-'));
let window,server,monitor;const shown=[];const originalShow=Notification.prototype.show;Notification.prototype.show=function(){shown.push(this);return originalShow.call(this);};
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
app.whenReady().then(async()=>{
 const trace=[];monitor=cp.spawn('dbus-monitor',['--session',"interface='org.freedesktop.Notifications',member='Notify'"],{stdio:['ignore','pipe','ignore']});monitor.stdout.on('data',data=>trace.push(data.toString()));await delay(300);
 server=http.createServer((req,res)=>{if(req.url==='/sw.js'){res.setHeader('Content-Type','application/javascript');res.end('self.addEventListener("install",e=>self.skipWaiting());self.addEventListener("activate",e=>e.waitUntil(clients.claim()));self.addEventListener("notificationclick",e=>{if(!(e instanceof NotificationEvent))throw Error("Wrong event type");e.waitUntil(clients.matchAll().then(all=>Promise.all(all.map(client=>client.postMessage({clicked:true,data:e.notification.data})))));});');return;}res.setHeader('Content-Type','text/html');res.end('<title>Delivery fixture</title>Disposable notification test');});await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const owner={id:'delivery',beginDialog:()=>({}),endDialog:()=>{},lifecycle:{update:async()=>{}}};const profiles=new Profiles(async()=>true,()=>owner,{onPermissionError:message=>console.error('Routing warning: '+message)});const profile=profiles.get({Id:'delivery'});profile.serviceWorkers.on('console-message',(_event,details)=>console.error('Fixture worker: '+details.message));
 window=new BrowserWindow({show:false,webPreferences:{session:profile,sandbox:true,contextIsolation:true,nodeIntegration:false}});owner.window=window;owner.show=()=>window.show();await window.loadURL(`http://127.0.0.1:${server.address().port}/`);
 await window.webContents.executeJavaScript('navigator.serviceWorker.addEventListener("message",e=>{window.workerClick=e.data;})');
 await window.webContents.executeJavaScript('Notification.requestPermission()',true);
 const {installNotificationFrame}=require('../browser/notification_frame.cjs');
 await window.webContents.executeJavaScript('('+installNotificationFrame.toString()+')()');
 const native=new Notification({title:'LL native delivery fixture',body:'Private KDE fixture only'});native.show();
 await window.webContents.executeJavaScript('window.fixtureNotice=new Notification("LL page delivery fixture",{body:"Private KDE fixture only"})',true);
 let worker;try{worker=await window.webContents.executeJavaScript('(async()=>{await navigator.serviceWorker.register("/sw.js");const ready=await navigator.serviceWorker.ready;await ready.showNotification("LL worker delivery fixture",{body:"Private KDE fixture only",data:{fixtureToken:5}});return "resolved"})()',true);}catch{worker='rejected';}
 await delay(300);const notice=shown.find(value=>value.title==='LL worker delivery fixture');assert.ok(notice);notice.emit('click');
 let clicked=false;for(let i=0;i<100;i++){clicked=await window.webContents.executeJavaScript('window.workerClick?.data?.fixtureToken===5');if(clicked)break;await delay(50);}
 await delay(1500);const calls=trace.join('');const result={nativeSupported:Notification.isSupported(),nativeDelivered:calls.includes('LL native delivery fixture'),pageDelivered:calls.includes('LL page delivery fixture'),serviceWorkerDelivered:calls.includes('LL worker delivery fixture'),workerResult:worker,notificationClickRouted:clicked};assert.equal(result.serviceWorkerDelivered,true,'Worker notification reached KDE');console.log('Routing counts '+JSON.stringify(profiles.notificationBridges.get(profile).diagnostics()));assert.equal(clicked,true,'Click delivered to the original worker with its data');console.log(JSON.stringify(result));fs.writeFileSync('/work/notification-delivery.json',JSON.stringify(result));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{monitor?.kill();window?.destroy();server?.close();app.exit(process.exitCode||0);});
