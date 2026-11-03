'use strict';
// Genuine private-compositor pointer input; never connects to the user's compositor or bus.
const assert=require('node:assert/strict');
const {app}=require('electron');
const fs=require('node:fs'),http=require('node:http'),child=require('node:child_process');
const {WebLauncher}=require('../browser/window.cjs');
const {Profiles}=require('../browser/profiles.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
app.setPath('userData',fs.mkdtempSync('/work/zoom-'));
app.setDesktopName('io.github.RyanEwen.LittleLauncher.Web.desktop');
app.on('window-all-closed',()=>{});
const deadline=setTimeout(()=>{console.error('Zoom fixture deadline');app.exit(1);},30000);
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check){for(let i=0;i<150;i++){if(await check())return;await delay(40);}throw Error('Zoom check timed out');}
const server=http.createServer((_req,res)=>{res.setHeader('Content-Type','text/html');res.end('<title>Zoom fixture</title>Disposable zoom page');});server.listen(0,'127.0.0.1');
const owners=[];let scriptId;
function python(code){return child.execFileSync('python3',['-c',code],{encoding:'utf8'});}
app.whenReady().then(async()=>{
 scriptId='zoom-qa-'+Date.now();
 const script='/work/'+scriptId+'.js';
 fs.writeFileSync(script,"workspace.windowAdded.connect(function(window){if(window.caption==='QAZoom:Target')window.frameGeometry={x:80,y:70,width:500,height:500};});");
 python(`import sys;sys.path.insert(0,'/source/linux');from little_launcher.desktop import DesktopIntegration;d=DesktopIntegration();d._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.loadScript',${JSON.stringify(script)},${JSON.stringify(scriptId)});d._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.start')`);
 const inputTrace=[];
 const requests=[],url=`http://127.0.0.1:${server.address().port}/`;
 const services={isQuitting:()=>true,emit:()=>{},request:async message=>{requests.push(message);return true;}};
 services.profiles=new Profiles(services.request,()=>null);
 const theme={window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans'};
 const create=(id,zoom)=>{const owner=new WebLauncher({Id:id,Name:id==='zoom-one'?'QAZoom:Target':'QAZoom:Second',WebSharedProfile:true,WebHiddenPolicy:2,WebRegularWindow:true,WebZoomPercent:zoom},theme,null,{homeUrl:url,showBookmarkBar:false},services);owners.push(owner);return owner;};
 const first=create('zoom-one',125),second=create('zoom-two',75);
 first.show();await until(()=>owners.every(owner=>!owner.view.webContents.isLoading() && owner.view.webContents.getURL()===url));
 assert.equal(first.view.webContents.session,second.view.webContents.session);
 assert.equal(first.view.webContents.getZoomFactor(),1.25);assert.equal(second.view.webContents.getZoomFactor(),.75);
 assert.equal(first.view.webContents.getZoomMode(),'isolated');
 assert.ok(Math.abs(await first.view.webContents.executeJavaScript('innerWidth')-first.view.getBounds().width/1.25)<=1,'The configured zoom must actually scale the page');
 assert.ok(Math.abs(await second.view.webContents.executeJavaScript('innerWidth')-second.view.getBounds().width/.75)<=1);
 for(const [label,contents] of [['chrome',first.window.webContents],['page',first.view.webContents]]) {
  contents.on('before-input-event',(_event,input)=>inputTrace.push({owner:label,type:input.type,key:input.key,control:input.control}));
  contents.on('before-mouse-event',(_event,mouse)=>inputTrace.push({owner:label,type:mouse.type,modifiers:mouse.modifiers}));
  contents.on('zoom-changed',(_event,direction)=>inputTrace.push({owner:label,type:'zoom',direction}));
 }
 first.window.focus();first.view.webContents.focus();await until(()=>first.window.isFocused());
 child.execFileSync('python3',['/source/linux/dev/capture_wayland.py','/work/zoom-desktop.png']);
 if(process.argv.includes('--qualify-wheel')) child.execFileSync('python3',['/source/linux/dev/private_input.py','wheel','280','270','120','control'],{timeout:5000});
 if(process.argv.includes('--qualify-wheel')) {
  try {await until(()=>requests.some(message=>message.key==='WebZoomPercent'));} catch(error) {console.error(JSON.stringify({inputTrace,chromeZoom:first.window.webContents.getZoomFactor(),pageZoom:first.view.webContents.getZoomFactor()}));throw error;}
 }
 const request=requests.find(message=>message.key==='WebZoomPercent');
 if(request){assert.equal(request.id,'zoom-one');assert.ok([110,150].includes(request.value));}
 assert.equal(second.view.webContents.getZoomFactor(),.75);
 console.log(JSON.stringify({passed:true,sharedProfileZoomIndependent:true,isolatedZoomMode:true,realPageScaleVerified:true,realPrivatePointerWheel:!!request,preferenceSaved:!!request},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{
 clearTimeout(deadline);
 if(scriptId){try{python(`import sys;sys.path.insert(0,'/source/linux');from little_launcher.desktop import DesktopIntegration;DesktopIntegration()._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.unloadScript',${JSON.stringify(scriptId)})`);}catch{}}
 for(const owner of owners){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
 server.close();app.exit(process.exitCode||0);
});
