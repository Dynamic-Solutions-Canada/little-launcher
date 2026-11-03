'use strict';
// Explicit unsigned-in WhatsApp acceptance in the private KDE fixture only.
const assert=require('node:assert/strict');
const {app,nativeImage}=require('electron');
const fs=require('node:fs');
const {WebLauncher}=require('../browser/window.cjs');
const {Profiles}=require('../browser/profiles.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/public-page-icon-'));
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let owner;const diagnostics={};let declared=[];
app.whenReady().then(async()=>{
 const records=[];
 const services={emit:record=>records.push(record),request:async()=>false,isQuitting:()=>true};
 services.profiles=new Profiles(services.request,()=>owner);
 owner=new WebLauncher({Id:'public-icon',Kind:1,Name:'Public icon fixture'},
  {window:'#282828',text:'#ffffff',button:'#444444',highlight:'#3daee9',font:'Noto Sans',fontSize:10},
  null,{homeUrl:'https://web.whatsapp.com/',showBookmarkBar:false},services);
 owner.view.webContents.on('did-fail-load',(_event,code,_description,_url,main)=>{if(main)diagnostics.mainLoadCode=code;});
 owner.view.webContents.on('page-favicon-updated',(_event,urls)=>{diagnostics.declaredIconCount=urls.length;declared=urls;});
 owner.view.webContents.session.webRequest.onCompleted((details)=>{if(details.resourceType==='mainFrame')diagnostics.mainStatus=details.statusCode;});
 owner.show();
 for(let index=0;index<200 && !records.some(record=>record.type==='page-icon');index++)await delay(50);
 if(!records.some(record=>record.type==='page-icon')) {
   diagnostics.icons=[];
   for(const url of declared.slice(0,3)) {
     try {
       const parsed=new URL(url);
       const response=await owner.view.webContents.session.fetch(url,{credentials:'omit',redirect:'error',signal:AbortSignal.timeout(5000)});
       const bytes=Buffer.from(await response.arrayBuffer());const decoded=nativeImage.createFromBuffer(bytes);
       diagnostics.icons.push({scheme:parsed.protocol,host:parsed.hostname,status:response.status,mime:response.headers.get('content-type'),bytes:bytes.length,decoded:decoded.getSize(),loader:!!(await owner.bookmarkIcons.load(url,64))});
     } catch(error) {diagnostics.icons.push({errorType:error.name});}
   }
 }
 const record=records.find(record=>record.type==='page-icon');
 assert.ok(record,'Unsigned-in WhatsApp did not produce a usable declared icon');
 assert.equal(new URL(record.url).origin,'https://web.whatsapp.com');
 const raw=Buffer.from(record.imageBase64,'base64');assert.ok(raw.length>0 && raw.length<=256*1024);
 require('node:child_process').execFileSync('python3',['-c',"import sys;sys.path.insert(0,'/source/linux');from little_launcher.site_icon import normalized_png;from pathlib import Path;png=normalized_png(sys.stdin.buffer.read());assert png;Path('/work/whatsapp-page-icon.png').write_bytes(png)"],{input:raw});
 console.log(JSON.stringify({passed:true,unsignedInWhatsApp:true,declaredWebsiteIcon:true,boundedRaster:true,noPersonalProfiles:true}));
}).catch(()=>{console.error('Public WhatsApp icon acceptance did not complete; no personal profile was used. '+JSON.stringify(diagnostics));process.exitCode=1;}).finally(()=>{
 if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
 app.exit(process.exitCode||0);
});
