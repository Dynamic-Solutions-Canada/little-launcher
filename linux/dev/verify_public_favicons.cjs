'use strict';
// Public unsigned-in acceptance only, with a disposable browser profile.
const assert=require('node:assert/strict');
const {app}=require('electron');
const fs=require('node:fs');
const {WebLauncher}=require('../browser/window.cjs');
const {Profiles}=require('../browser/profiles.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/public-favicons-'));
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let owner;
app.whenReady().then(async()=>{
 const records=[];const services={isQuitting:()=>true,request:async()=>false,emit:record=>{
  records.push(record);
  if(record.type==='bookmark-icon'){
   const png=require('node:child_process').execFileSync('python3',['-c',"import sys;sys.path.insert(0,'/source/linux');from little_launcher.site_icon import normalized_png;sys.stdout.buffer.write(normalized_png(sys.stdin.buffer.read()) or b'')"],{input:Buffer.from(record.imageBase64,'base64')});
   if(png.length)owner.bookmarkIcons.accept(record.token,png.toString('base64'));
  }
 }};
 services.profiles=new Profiles(services.request,()=>owner);
 const messages=process.argv.includes('--messages');const url=messages?'https://messages.google.com/web/':'https://youtube.com/';
 owner=new WebLauncher({Id:'public-favicon',Name:'Public favicon fixture',WebShowBookmarkBar:true,WebBookmarks:[{Name:'YouTube',Url:url}]},
  {window:'#282828',text:'#ffffff',button:'#444444',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,{homeUrl:url,showBookmarkBar:true},services);
 if(process.argv.includes('--normal'))owner.view.webContents.setUserAgent(require('../browser/browser_identity.cjs').browserUserAgent());
 const declared=[];owner.view.webContents.on('page-favicon-updated',(_event,urls)=>declared.push(...urls));owner.show();
 for(let i=0;i<300;i++){await delay(100);if(declared.length&&!owner.bookmarkIcons.active)break;}
 const unsupported=await owner.view.webContents.executeJavaScript('/browser.{0,30}(not supported|unsupported)|update your browser/i.test(document.body?.innerText||"")');
 const pageError=await owner.view.webContents.executeJavaScript('/400\\. That|request.{0,30}malformed/i.test(document.body?.innerText||"")');
 const result={httpErrorPage:pageError,site:messages?'Google Messages':'YouTube',requestIdentity:owner.view.webContents.getUserAgent(),unsupportedBrowser:unsupported,finalOrigin:new URL(owner.view.webContents.getURL()).origin,declared:[...new Set(declared)],bookmarkImage:!!owner.bookmarkIcons.peek(url),loadedPageImage:!!owner.bookmarkIcons.peek(owner.view.webContents.getURL()),entries:[...owner.bookmarkIcons.entries].map(([origin,entry])=>({origin,iconUrl:entry.url,decoded:!!entry.image}))};
 console.log(JSON.stringify(result,null,2));fs.writeFileSync(messages?'/work/messages-identity-result.json':'/work/youtube-favicon-result.json',JSON.stringify(result,null,2));
 if(process.argv.includes('--assert')){assert.ok(result.bookmarkImage,'The saved bookmark has its loaded page favicon');assert.equal(pageError,false,'The public page did not return an HTTP error');assert.equal(unsupported,false,'The public page remains supported with the trial identity');}
}).catch(error=>{console.error(error.stack);process.exitCode=1;}).finally(()=>{if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}app.exit(process.exitCode||0);});
