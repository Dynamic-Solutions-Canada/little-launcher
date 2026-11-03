'use strict';
const assert = require('node:assert/strict');
const {app} = require('electron');
const fs = require('node:fs');
const http = require('node:http');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.commandLine.appendSwitch('ozone-platform','wayland');
app.setPath('userData',fs.mkdtempSync('/work/worker-network-'));
const {WebLauncher}=require('../browser/window.cjs');
const {Profiles}=require('../browser/profiles.cjs');
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check){for(let i=0;i<100;i++){if(check())return;await delay(50);}throw Error('Worker fixture condition timed out');}
let owner,server,held;
app.whenReady().then(async()=>{
  server=http.createServer((req,res)=>{
    if(req.url==='/worker.js'){
      res.setHeader('Content-Type','text/javascript');res.end(`self.addEventListener('install',event=>event.waitUntil(self.skipWaiting()));self.addEventListener('activate',event=>event.waitUntil(self.clients.claim()));self.addEventListener('message',event=>event.waitUntil(fetch('/worker-post',{method:'POST',body:'synthetic'}).then(()=>event.source.postMessage('done'))));`);
    }else if(req.url==='/worker-post')held=res;
    else{res.setHeader('Content-Type','text/html');res.end('<title>Worker fixture</title>Disposable worker fixture');}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const observations=[];
  const services={isQuitting:()=>true,request:async()=>false,emit:()=>{}};
  services.profiles=new Profiles(services.request,contents=>contents && contents===owner?.view?.webContents?owner:null);
  const resolve=services.profiles.requestOwners.bind(services.profiles);
  services.profiles.requestOwners=(profile,details)=>{
    observations.push({hasContentsId:Number.isInteger(details.webContentsId)&&details.webContentsId>0});
    return resolve(profile,details);
  };
  const url=`http://127.0.0.1:${server.address().port}/`;
  owner=new WebLauncher({Id:'worker-fixture',Name:'Worker fixture'},
    {window:'#fff',text:'#000',button:'#eee',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,
    {homeUrl:url,showBookmarkBar:false},services);
  owner.show();
  await until(()=>owner.view.webContents.getURL()===url&&!owner.view.webContents.isLoading());
  const job=owner.view.webContents.executeJavaScript(`new Promise(async resolve=>{navigator.serviceWorker.addEventListener('message',event=>{if(event.data==='done')resolve('done');});await navigator.serviceWorker.register('/worker.js');const registration=await navigator.serviceWorker.ready;registration.active.postMessage('fetch');})`);
  await until(()=>held);
  assert.equal(owner.lifecycle.uploads.size,1);
  owner.hide();await owner.lifecycle.update();
  owner.lifecycle.lastHidden=Date.now()-600000;owner.tick();
  assert.equal(owner.lifecycle.frozen,false);assert.ok(owner.view);
  held.end('ok');held=null;
  owner.show();
  assert.equal(await Promise.race([job,delay(5000).then(()=>{throw Error('Worker reply timed out');})]),'done');
  await until(()=>owner.lifecycle.uploads.size===0);
  assert.ok(observations.some(item=>!item.hasContentsId),'Real worker request must exercise missing renderer metadata');
  console.log(JSON.stringify({passed:true,realServiceWorkerFetchCompleted:true,missingWebContentsIdObserved:true,
    uploadProtectsHiddenPage:true,protectionClearedAfterResponse:true,remoteBridgeAbsent:await owner.view.webContents.executeJavaScript('typeof process+":"+typeof window.launcher')==='undefined:undefined'},null,2));
}).catch(error=>{console.error(error.message);process.exitCode=1;}).finally(()=>{
  held?.end();server?.close();
  if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
  app.exit(process.exitCode||0);
});
