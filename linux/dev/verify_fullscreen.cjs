'use strict';
// Synthetic local canvas/video and private compositor; no host media device or personal page.
const child=require('node:child_process');
const {app,ipcMain}=require('electron'),assert=require('node:assert/strict'),fs=require('node:fs'),http=require('node:http');
const {WebLauncher}=require('../browser/window.cjs'),{Profiles}=require('../browser/profiles.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/fullscreen-'));
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check){for(let i=0;i<200;i++){if(await check())return;await delay(40);}throw Error('Fullscreen timeout');}
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(`<style>body{margin:0;background:#245}#player{width:100%;height:100%}video{width:100%;height:100%}</style><div id="player"><video muted controls autoplay></video></div><script>const canvas=document.createElement('canvas');canvas.width=640;canvas.height=360;const ctx=canvas.getContext('2d');ctx.fillStyle='#278050';ctx.fillRect(0,0,640,360);const video=document.querySelector('video');video.srcObject=canvas.captureStream(1);video.play();</script>`);});server.listen(0,'127.0.0.1');
let owner;const deadline=setTimeout(()=>app.exit(2),30000);
app.whenReady().then(async()=>{
 const requests=[],url=`http://127.0.0.1:${server.address().port}/`;
 const services={isQuitting:()=>true,emit:()=>{},request:async message=>{requests.push(message);return true;}};
 services.profiles=new Profiles(services.request,()=>owner);
 owner=new WebLauncher({Id:'fullscreen-qa',Name:'Contained fullscreen fixture',WebRegularWindow:true,WebHiddenPolicy:2,WebFlyoutWidth:480,WebFlyoutHeight:360,WebHomeUrl:url,WebFullScreenInWindow:true},
 {window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans'},null,{homeUrl:url,showBookmarkBar:true},services);
 ipcMain.on('toolbar-action',(event,action,value)=>owner.handleToolbar(event,action,value));owner.show();
 await until(()=>!owner.view.webContents.isLoading() && !owner.window.webContents.isLoading());
 const page=owner.view.webContents;await until(()=>page.executeJavaScript('document.querySelector("video").videoWidth===640'));
 const original=owner.window.getContentSize();
 await page.executeJavaScript('document.getElementById("player").requestFullscreen()',true);
 await until(()=>owner.fullscreen.state && owner.fullscreen.view && !owner.fullscreen.view.webContents.isLoading());
 await delay(200);assert.equal(owner.window.isFullScreen(),false);assert.deepEqual(owner.window.getContentSize(),original);
 assert.deepEqual(owner.view.getBounds(),{x:0,y:0,width:original[0],height:original[1]});
 assert.equal(await page.executeJavaScript('typeof window.launcher'),'undefined');
 assert.equal(owner.window.contentView.children.at(-1),owner.fullscreen.view);
 await owner.fullscreen.view.webContents.executeJavaScript('document.getElementById("exit-fullscreen").click()');
 await until(()=>!owner.fullscreen.state);assert.equal(await page.executeJavaScript('!!document.fullscreenElement'),false);
 assert.deepEqual(owner.window.getContentSize(),original);assert.ok(owner.view.getBounds().y>0);
 owner.config.WebFitFullscreenToVideo=true;
 await page.executeJavaScript('document.getElementById("player").requestFullscreen()',true);
 await until(()=>owner.fullscreen.state && Math.abs(owner.window.getContentSize()[0]/owner.window.getContentSize()[1]-16/9)<.02);
 assert.equal(owner.window.isFullScreen(),false);
 child.execFileSync('python3',['/source/linux/dev/capture_wayland.py','/work/fullscreen.png']);
 await owner.fullscreen.view.webContents.executeJavaScript('document.getElementById("fullscreen-maximize").click()');await until(()=>owner.window.isMaximized());
 await owner.fullscreen.view.webContents.executeJavaScript('document.getElementById("fullscreen-maximize").click()');await until(()=>!owner.window.isMaximized());
 await delay(500);assert.equal(requests.filter(message=>message.type==='web-size').length,0,'Temporary fullscreen/video dimensions must not overwrite portable size');
 owner.shortcut({key:'Escape'});await until(()=>!owner.fullscreen.state);
 assert.deepEqual(owner.window.getContentSize(),original);
 owner.config.WebFullScreenInWindow=false;owner.config.WebFitFullscreenToVideo=false;
 await page.executeJavaScript('document.getElementById("player").requestFullscreen()',true);
 await until(()=>owner.fullscreen.state && owner.window.isFullScreen());
 owner.fullscreen.exitPage();await until(()=>!owner.fullscreen.state && !owner.window.isFullScreen());
 await delay(400);assert.deepEqual(owner.window.getContentSize(),original);
 console.log(JSON.stringify({passed:true,realHtmlFullscreen:true,containedKeepsWindowSize:true,remoteFillsContent:true,trustedExitOverlay:true,remoteBridgeAbsent:true,videoRatioFit:true,temporarySizeNotSaved:true,maximizeAndRestoreControls:true,escapeExitsPageFullscreen:true,displayFullscreen:true,restoresNormalSizeAndBars:true},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{clearTimeout(deadline);ipcMain.removeAllListeners('toolbar-action');if(owner){owner.fullscreen.dispose();owner.bookmarkOverlay.dispose();for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}server.close();app.exit(process.exitCode||0);});
