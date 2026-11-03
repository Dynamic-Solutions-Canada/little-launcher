'use strict';
const assert=require('node:assert/strict');
const {app,BrowserWindow,Notification}=require('electron');
const {WebLauncher}=require('../browser/window.cjs');
const {Profiles}=require('../browser/profiles.cjs');
const fs=require('node:fs'),http=require('node:http'),path=require('node:path');
assert.equal(process.env.XDG_RUNTIME_DIR,'/tmp/ll-runtime');
const directory=process.argv.find(v=>v.startsWith('--profile='))?.slice(10);assert.ok(directory?.startsWith('/work/'));app.setPath('userData',directory);
const second=process.argv.includes('--second');const sleep=ms=>new Promise(r=>setTimeout(r,ms));async function until(f){for(let i=0;i<100;i++){if(await f())return;await sleep(50);}throw Error('Condition timed out');}
const owners=[],servers=[],prompts=[];let allow=true,nativeShows=0;const show=Notification.prototype.show;Notification.prototype.show=function(){nativeShows++;return show.call(this);};
app.whenReady().then(async()=>{
 const portsFile=path.join(directory,'fixture-ports.json'),ports=second?JSON.parse(fs.readFileSync(portsFile)):[];
 for(let i=0;i<2;i++){const server=http.createServer((_req,res)=>{res.setHeader('Content-Type','text/html');res.end('<title>Permission state fixture</title>')});await new Promise(r=>server.listen(ports[i]||0,'127.0.0.1',r));servers.push(server);ports[i]=server.address().port;}
 if(!second)fs.writeFileSync(portsFile,JSON.stringify(ports));
 const services={isQuitting:()=>true,emit:()=>{},request:async message=>{assert.equal(message.type,'permission');assert.equal(message.permission,'notifications');prompts.push(message.id);return allow;}};
 services.profiles=new Profiles(services.request,contents=>{const owner=owners.find(o=>o.tabs.some(t=>t.view?.webContents===contents));if(!owner)return;const tab=owner.tabs.find(t=>t.view?.webContents===contents);const facade=Object.create(owner);Object.defineProperty(facade,'lifecycle',{value:tab.lifecycle});Object.defineProperty(facade,'focusNotification',{value:()=>owner.activateNotification(tab.id)});return facade;});
 const theme={window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans',fontSize:10};
 for(const [id,shared,port]of [['one',true,ports[0]],['two',true,ports[0]],['private',false,ports[0]],['other-origin',true,ports[1]]]){
  const url=`http://127.0.0.1:${port}/`;const owner=new WebLauncher({Id:id,Name:id,WebSharedProfile:shared,WebRegularWindow:true,WebHiddenPolicy:2},theme,null,{homeUrl:url,showBookmarkBar:false},services);owners.push(owner);await until(()=>owner.view.webContents.getURL()===url&&!owner.view.webContents.isLoading());
 }
 const run=(index,source)=>owners[index].view.webContents.executeJavaScript(source,true);
 const state=index=>run(index,'({notification:Notification.permission})');
 if(!second){
  for(let i=0;i<4;i++)assert.equal((await state(i)).notification,'default');
  await run(1,'navigator.permissions.query({name:"notifications"}).then(status=>{window.permissionStatus=status;window.changes=0;status.onchange=()=>window.changes++;return true})');
  await run(1,'(()=>{const control=Notification[Symbol.for("little-launcher.notification-presentation")],original=control.update;control.update=value=>{window.updatePayloadType=typeof value;return original(value)};return true})()');
  const before=nativeShows;assert.equal(await run(0,'new Promise(resolve=>{const n=new Notification("Unknown must not display");n.onerror=()=>resolve(true)})'),true);assert.equal(nativeShows,before);
  assert.equal(await run(0,'Notification.requestPermission(value=>window.legacy=value)'),'granted');assert.equal(await run(0,'window.legacy'),'granted');assert.equal(prompts.length,1);
  assert.equal((await state(1)).notification,'granted');assert.equal(await run(1,'window.permissionStatus.state'),'granted');assert.ok(await run(1,'window.changes>0'));assert.equal(await run(1,'window.updatePayloadType'),'string');
  assert.equal((await state(2)).notification,'default');assert.equal((await state(3)).notification,'default');
  allow=false;assert.equal(await run(2,'Notification.requestPermission()'),'denied');assert.equal(await run(2,'navigator.permissions.query({name:"notifications"}).then(s=>s.state)'),'denied');
  const count=prompts.length;assert.equal(await run(2,'Notification.requestPermission()'),'denied');assert.equal(prompts.length,count);
  services.profiles.resetPermissions(owners[0].config);await until(async()=> (await state(0)).notification==='default');assert.equal((await state(1)).notification,'default');assert.equal((await state(2)).notification,'denied');
  allow=true;assert.equal(await run(0,'Notification.requestPermission()'),'granted');
 }else{
  assert.equal((await state(0)).notification,'granted');assert.equal((await state(1)).notification,'granted');assert.equal((await state(2)).notification,'denied');assert.equal((await state(3)).notification,'default');
  assert.equal(await run(0,'Notification.requestPermission()'),'granted');assert.equal(await run(2,'Notification.requestPermission()'),'denied');assert.equal(prompts.length,0);
 }
 for(let i=0;i<4;i++)assert.equal(await run(i,'typeof process==="undefined"&&typeof require==="undefined"&&!Object.getOwnPropertyNames(window).some(v=>v.startsWith("__llNotification_"))'),true);
 const result={passed:true,secondProcess:second,defaultIsPrompt:true,sharedProfileUpdatesLive:true,privateProfileIsolated:true,otherOriginIsolated:true,denyRetained:true,unknownCannotDisplay:true,resetReturnsToDefault:true,restartChoicesRetained:second,noRepeatedPrompt:second,noPageNodeOrGeneralBridge:true};fs.writeFileSync(path.join(directory,second?'second.json':'first.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));
}).catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>{for(const window of BrowserWindow.getAllWindows())window.destroy();for(const server of servers)server.close();app.exit(process.exitCode||0);});
