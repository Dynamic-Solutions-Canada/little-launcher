'use strict';
// Verify the requested Google UA experiment without entering an account or using user profiles.
const assert=require('node:assert/strict');
const {app}=require('electron');
const fs=require('node:fs'),http=require('node:http');
const {Profiles}=require('../browser/profiles.cjs');
const {WebLauncher}=require('../browser/window.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/google-identity-'));
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let owner,server;
app.whenReady().then(async()=>{
 const received=[];
 server=http.createServer((req,res)=>{received.push(req.headers['user-agent']);res.setHeader('Content-Type','text/html');res.end('<title>Local browser identity fixture</title>Local fixture');});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const services={isQuitting:()=>true,emit:()=>{},request:async()=>false};services.profiles=new Profiles(services.request,()=>owner);
 const config={Id:'google-ua-fixture',Name:'Google identity fixture',WebHiddenPolicy:2};
 const profile=services.profiles.get(config);const googleHeaders=[];
 profile.webRequest.onSendHeaders(details=>{if(details.resourceType==='mainFrame'&&new URL(details.url).hostname==='accounts.google.com')googleHeaders.push(details.requestHeaders['User-Agent']||details.requestHeaders['user-agent']);});
 owner=new WebLauncher(config,{window:'#eeeeee',text:'#000000',button:'#dddddd',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,{homeUrl:'https://accounts.google.com/ServiceLogin',showBookmarkBar:false},services);owner.show();
 for(let i=0;i<400;i++){await delay(50);if(googleHeaders.length&&!owner.view.webContents.isLoading())break;}
 console.log('phase: initial sign-in navigation completed');
 assert.ok(googleHeaders.length,'Actual Google sign-in requests observed');assert.ok(googleHeaders.every(value=>value==='Chrome'));
 assert.equal(new URL(owner.view.webContents.getURL()).hostname,'accounts.google.com');
 assert.equal(await owner.view.webContents.executeJavaScript('navigator.userAgent'),'Chrome');
 assert.equal(await owner.view.webContents.executeJavaScript('typeof window.launcher+":"+typeof process'),'undefined:undefined');
 await owner.view.webContents.loadURL(`http://127.0.0.1:${server.address().port}/`);
 assert.match(received.at(-1),/Linux.*Chrome\//);assert.match(await owner.view.webContents.executeJavaScript('navigator.userAgent'),/Linux.*Chrome\//);
 await owner.view.webContents.loadURL('https://accounts.google.com/ServiceLogin');
 assert.equal(await owner.view.webContents.executeJavaScript('navigator.userAgent'),'Chrome');
 await owner.view.webContents.loadURL('https://messages.google.com/web/');
 for(let i=0;i<200;i++){if(await owner.view.webContents.executeJavaScript('/Messages|400\\. That|malformed/i.test(document.body?.innerText||"")'))break;await delay(50);}
 const messages=await owner.view.webContents.executeJavaScript('({ua:navigator.userAgent,text:document.body?.innerText||""})');
 assert.match(messages.ua,/Linux.*Chrome\//);assert.doesNotMatch(messages.text,/400\. That|malformed|unsupported browser/i);assert.match(messages.text,/Messages/i);
 const result={passed:true,googleRequestAndNavigatorUseBareChrome:true,sameTabNormalIdentityRestored:true,messagesLoadsAfterGoogle:true,remoteBridgeAbsent:true,personalProfileUsed:false,accountEntered:false,realGoogleAccountAcceptancePending:true};
 fs.writeFileSync('/work/google-identity-result.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result));
}).catch(error=>{console.error(error.stack);process.exitCode=1;}).finally(()=>{if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}server?.close();app.exit(process.exitCode||0);});
