'use strict';
// Local pages, synthetic bookmark tree and private compositor only.
const {app,ipcMain}=require('electron');
const child=require('node:child_process');
const assert=require('node:assert/strict'),fs=require('node:fs'),http=require('node:http');
const {WebLauncher}=require('../browser/window.cjs'),{Profiles}=require('../browser/profiles.cjs');
const {bookmarkVersion}=require('../browser/bookmarks.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/bookmark-overlay-'));
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check){for(let i=0;i<160;i++){if(await check())return;await delay(40);}throw Error('Bookmark overlay timeout');}
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end('<body style="background:rgb(230,70,30)"><h1>Isolated page stays loaded</h1>');});server.listen(0,'127.0.0.1');
let owner;const deadline=setTimeout(()=>app.exit(2),30000);
app.whenReady().then(async()=>{
 const url=`http://127.0.0.1:${server.address().port}/`,requests=[];
 const services={isQuitting:()=>true,emit:()=>{},request:async message=>{requests.push(message);return true;}};services.profiles=new Profiles(services.request,()=>null);
 const bookmarks=[{Name:'Hidden',Url:'legacy:hidden'},{IsFolder:true,Name:'Folder',Children:[{Name:'Hidden child',Url:'legacy:hidden'},{Name:'Child',Url:url+'child',Future:8},{IsFolder:true,Name:'Nested',Children:[]}]},{Name:'Root',Url:url+'root'}];
 for(let i=0;i<18;i++)bookmarks.push({Name:'Overflow bookmark '+i,Url:url+'overflow'+i});
 owner=new WebLauncher({Id:'overlay-qa',Name:'Bookmark overlay',WebRegularWindow:true,WebHomeUrl:url,WebBookmarks:bookmarks},
 {window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans'},null,{homeUrl:url,showBookmarkBar:true},services);
 ipcMain.on('toolbar-action',(event,action,value)=>owner.handleToolbar(event,action,value));
 owner.show();await until(()=>!owner.view.webContents.isLoading() && !owner.window.webContents.isLoading());
 const remote=owner.view.webContents;owner.toolbar();await delay(100);
 await owner.window.webContents.executeJavaScript(`document.querySelector('[data-bookmark-index="0"]').click()`);
 await until(()=>owner.bookmarkOverlay.view && !owner.bookmarkOverlay.view.webContents.isLoading());
 const overlay=owner.bookmarkOverlay.view.webContents;
 await until(()=>overlay.executeJavaScript('document.querySelectorAll(".bookmark-row").length===2'));
 assert.equal(owner.window.contentView.children.at(-1),owner.bookmarkOverlay.view);
 assert.equal(await remote.executeJavaScript('typeof window.launcher'),'undefined');
 assert.equal(await overlay.executeJavaScript('typeof require'),'undefined');
 assert.deepEqual(await overlay.executeJavaScript('[...document.querySelectorAll(".bookmark-row")].map(row=>JSON.parse(row.dataset.path))'),[[1,1],[1,2]]);
 await overlay.executeJavaScript(`document.querySelector('[data-path="[1,2]"]').click()`);
 assert.equal(await overlay.executeJavaScript('document.querySelectorAll(".bookmark-panel").length'),2);
 assert.ok(await overlay.executeJavaScript('document.querySelector(".empty-folder").textContent.includes("Empty")'));
 assert.ok(await overlay.executeJavaScript('[...document.querySelectorAll(".bookmark-panel")].every(panel=>{const r=panel.getBoundingClientRect();return r.x>=0&&r.y>=0&&r.right<=innerWidth&&r.bottom<=innerHeight})'));
 // Private compositor coordinates let actual native input cross trusted view boundaries.
 const probeCode="import sys,time,json;sys.path[:0]=['/source/linux','/source/linux/dev'];from compositor_probe import CompositorProbe;p=CompositorProbe('Bookmark overlay',None);deadline=time.monotonic()+4\nwhile not p.latest and time.monotonic()<deadline:time.sleep(.02)\nprint(json.dumps(p.latest));p.close()";
 const geometry=JSON.parse(child.execFileSync('python3',['-c',probeCode],{encoding:'utf8'}));assert.ok(geometry);
 for(const contents of [owner.window.webContents,overlay])await contents.executeJavaScript("window.qaPointer=[];for(const name of ['pointerdown','mousedown','dragstart','dragenter','dragover','drop'])document.addEventListener(name,event=>{if(window.qaPointer.length<40)window.qaPointer.push({name,x:event.clientX,y:event.clientY,token:name==='drop'?event.dataTransfer.getData('application/x-little-launcher-bookmark'):null})},true)");
 const sourceRect=await owner.window.webContents.executeJavaScript('(()=>{const r=document.querySelector(\'[data-bookmark-index="1"]\').getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2}})()');
 const targetRect=await overlay.executeJavaScript('(()=>{const r=document.querySelector(\'[data-path="[1,2]"]\').getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2}})()');
 const overlayBounds=owner.bookmarkOverlay.view.getBounds();
 await new Promise((resolve,reject)=>{const input=child.spawn('python3',['/source/linux/dev/private_input.py','drag',String(Math.round(geometry.x+sourceRect.x)),String(Math.round(geometry.y+sourceRect.y)),String(Math.round(geometry.x+overlayBounds.x+targetRect.x)),String(Math.round(geometry.y+overlayBounds.y+targetRect.y)),'.5']);input.on('exit',code=>code===0?resolve():reject(Error('Native drag failed')));});
 try {await until(()=>requests.some(value=>value.action==='reorder' && JSON.stringify(value.path)==='[2]' && JSON.stringify(value.parent)==='[1,2]'));}catch(error){console.error(JSON.stringify({geometry,sourceRect,targetRect,overlayBounds,drag:owner.bookmarkOverlay.drag,rootTrace:await owner.window.webContents.executeJavaScript('window.qaPointer'),overlayTrace:await overlay.executeJavaScript('window.qaPointer')}));throw error;}
 requests.length=0;
 // The full hidden-aware path, not a visible child index, reaches native context actions.
 let menu;owner.menu=template=>{menu=template;};
 await overlay.executeJavaScript(`document.querySelector('[data-path="[1,1]"]').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))`);
 await until(()=>menu);menu.find(item=>item.label==='Copy address').click();
 // Create a genuine owned local drag session on the overlay, then drop it back on the root bar.
 const token=await overlay.executeJavaScript(`(()=>{const row=document.querySelector('[data-path="[1,1]"]'),data=new DataTransfer();row.dispatchEvent(new DragEvent('dragstart',{bubbles:true,dataTransfer:data}));return data.getData('application/x-little-launcher-bookmark');})()`);
 await until(()=>owner.bookmarkOverlay.drag?.token===token);
 await owner.window.webContents.executeJavaScript(`(()=>{const data=new DataTransfer();data.setData('application/x-little-launcher-bookmark',${JSON.stringify(token)});document.getElementById('bookmarks').dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));})()`);
 await until(()=>requests.some(value=>value.action==='reorder'));
 const move=requests.find(value=>value.action==='reorder');assert.deepEqual(move.path,[1,1]);assert.deepEqual(move.parent,[]);assert.equal(move.expected[1].Children[1].Future,8);
 await until(()=>!owner.bookmarkOverlay.drag);
 const before=requests.length;
 // A forged token does not initiate or dispatch a move.
 await overlay.executeJavaScript(`(()=>{const data=new DataTransfer();data.setData('application/x-little-launcher-bookmark','forged');document.querySelector('.bookmark-panel').dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));})()`);
 await delay(80);assert.equal(requests.length,before);
 owner.config.IsShared=true;owner.toolbar();await delay(80);
 assert.equal(await overlay.executeJavaScript('document.querySelectorAll("[draggable=true]").length'),0);
 owner.config.IsShared=false;owner.bookmarkOverlay.close();
 owner.toolbar();await delay(80);
 await owner.window.webContents.executeJavaScript('document.querySelector(".bookmark-overflow").click()');
 await until(()=>owner.bookmarkOverlay.selection?.folder===null);
 await until(()=>overlay.executeJavaScript('document.querySelectorAll(".bookmark-row").length>0'));
 assert.ok(await overlay.executeJavaScript('document.querySelector(".bookmark-panel").getAttribute("aria-label")==="More bookmarks"'));
 await delay(200);child.execFileSync('python3',['/source/linux/dev/capture_wayland.py','/work/bookmark-overlay.png']);
 await overlay.executeJavaScript('document.getElementById("panels").dispatchEvent(new PointerEvent("pointerdown",{bubbles:true}))');
 await until(()=>owner.bookmarkOverlay.selection===null);assert.equal(owner.bookmarkOverlay.view.getVisible(),false);
 assert.equal(remote,owner.view.webContents);assert.equal(remote.isDestroyed(),false);
 owner.bookmarkMenu([1],bookmarkVersion(owner.config.WebBookmarks),{x:8});await delay(80);
 owner.config.WebBookmarks[1].Name='Changed elsewhere';owner.toolbar();assert.equal(owner.bookmarkOverlay.selection,null);
 console.log(JSON.stringify({passed:true,realPointerAcrossNativeViews:true,nestedFolders:true,fullSourcePaths:true,overlayAboveRemote:true,remoteBridgeAbsent:true,moveFromFolderToBar:true,foreignMarkerRejected:true,readOnlyDisablesDrag:true,overflowOverlay:true,backdropDismissal:true,staleTreeDismissal:true,pageIdentityRetained:true},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{clearTimeout(deadline);ipcMain.removeAllListeners('toolbar-action');if(owner){owner.bookmarkOverlay.dispose();for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}server.close();app.exit(process.exitCode||0);});
