'use strict';
// Disposable local pages and private compositor only; no personal profile or browser is used.
const assert = require('node:assert/strict');
const {app, clipboard, shell, dialog, ipcMain} = require('electron');
const fs = require('node:fs');
const http = require('node:http');
const {WebLauncher} = require('../browser/window.cjs');
const {Profiles} = require('../browser/profiles.cjs');
const {bookmarkVersion} = require('../browser/bookmarks.cjs');
assert.equal(process.env.XDG_RUNTIME_DIR, '/tmp/ll-runtime');
app.setPath('userData', fs.mkdtempSync('/work/context-actions-'));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check) {for (let i=0;i<150;i++) {if (await check()) return;await delay(40);}throw Error('Context action timed out');}
let upload;
const server = http.createServer((req,res) => {
  if (req.url === '/upload') {upload=res;req.resume();return;}
  if (req.url === '/favicon.ico') {res.writeHead(404);res.end();return;}
  res.setHeader('Content-Type','text/html');res.end('<title>Local context fixture</title>Disposable page');
});
server.listen(0,'127.0.0.1');
let owner;
app.whenReady().then(async () => {
  const url=`http://127.0.0.1:${server.address().port}/`;
  const requests=[];
  const services={isQuitting:()=>true,emit:()=>{},request:async message=>{requests.push(message);return true;}};
  services.profiles = new Profiles(services.request, contents => {
    const tab=owner?.tabs.find(tab=>tab.view?.webContents===contents);
    if(!tab)return;
    const facade=Object.create(owner);Object.defineProperty(facade,'lifecycle',{value:tab.lifecycle});return facade;
  });
  owner=new WebLauncher({Id:'context-fixture',Name:'Context fixture',WebRegularWindow:true,WebAlwaysShowTabs:true,
    WebHomeUrl:url,WebBookmarks:[{Name:'Legacy',Url:'legacy:skip'},
      {Name:'Folder',IsFolder:true,Children:[]},{Name:'Saved',Url:url+'saved',Future:7}]},
    {window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans'},
    null,{homeUrl:url,showBookmarkBar:true},services);
  owner.show();
  await until(()=>!owner.view.webContents.isLoading() && owner.view.webContents.getURL()===url);
  const originalTab=owner.activeTab;
  await until(()=>!owner.window.webContents.isLoading());
  ipcMain.on('toolbar-action',(event,action,value)=>owner.handleToolbar(event,action,value));
  owner.toolbar();await delay(80);
  await owner.window.webContents.executeJavaScript(`document.querySelector('[data-bookmark-index="1"]').dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}))`);
  await until(()=>owner.tabs.length===2 && !owner.view.webContents.isLoading());
  assert.equal(owner.view.webContents.getURL(),url+'saved');
  await owner.closeTab(owner.activeTab.id);
  owner.selectTab(originalTab.id);
  let menu;
  owner.menu=template=>{menu=template;};
  await owner.window.webContents.executeJavaScript(`document.querySelector('[role="tab"]').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))`);
  await until(()=>Array.isArray(menu));
  assert.ok(menu.some(item=>item.label==='Duplicate'));
  const click=label=>{const item=menu.find(item=>item.label===label);assert.ok(item,label);assert.notEqual(item.enabled,false,label);item.click();};
  const version=bookmarkVersion(owner.config.WebBookmarks);
  owner.toolbar();await delay(80);
  // External data with the same MIME marker cannot manufacture an internal drag source.
  const beforeDrop=requests.length;
  await owner.window.webContents.executeJavaScript(`(() => {
    const transfer=new DataTransfer();transfer.setData('application/x-little-launcher-strip','internal');
    const target=document.querySelector('[data-bookmark-index="0"]');
    target.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:transfer}));
  })()`);
  await delay(40);assert.equal(requests.length,beforeDrop);
  await owner.window.webContents.executeJavaScript(`(() => {
    const source=document.querySelector('[data-bookmark-index="1"]'), target=document.querySelector('[data-bookmark-index="0"]');
    const transfer=new DataTransfer(), bounds=target.getBoundingClientRect();
    source.dispatchEvent(new DragEvent('dragstart',{bubbles:true,dataTransfer:transfer}));
    target.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:transfer,clientX:bounds.left+bounds.width/2}));
  })()`);
  await until(()=>requests.some(message=>message.action==='reorder'));
  const dropped=requests.find(message=>message.action==='reorder');
  assert.deepEqual(dropped.path,[2]);assert.deepEqual(dropped.parent,[1]);assert.equal(dropped.before,null);

  owner.bookmarkContext(1,version);
  click('Copy address');assert.equal(await clipboard.readText(),url+'saved');
  click('Set as home page');await until(()=>requests.some(item=>item.action==='set-home'));
  const home=requests.find(item=>item.action==='set-home');
  assert.deepEqual(home.path,[2]);assert.equal(home.expectedHome,url);
  click('Open in new tab');
  await until(()=>owner.tabs.length===2 && !owner.view.webContents.isLoading());
  assert.equal(owner.view.webContents.getURL(),url+'saved');
  const savedTab=owner.activeTab;
  owner.selectTab(originalTab.id);
  owner.tabContext(savedTab.id);click('Copy address');assert.equal(await clipboard.readText(),url+'saved');
  click('Duplicate');
  await until(()=>owner.tabs.length===3);
  assert.equal(owner.activeTab,originalTab,'Duplicating an inactive tab must not select it');
  const duplicate=owner.tabs.at(-1);
  await until(()=>!duplicate.view.webContents.isLoading());
  assert.equal(duplicate.view.webContents.getURL(),url+'saved');
  owner.toolbar();await delay(80);
  await owner.window.webContents.executeJavaScript(`(() => {
    const source=document.querySelector('[data-tab-id="${duplicate.id}"]'),target=document.querySelector('[role="tab"]');
    const transfer=new DataTransfer(),bounds=target.getBoundingClientRect();
    source.dispatchEvent(new DragEvent('dragstart',{bubbles:true,dataTransfer:transfer}));
    target.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:transfer,clientX:bounds.left+2}));
  })()`);
  await until(()=>owner.tabs[0]===duplicate);
  assert.equal(owner.activeTab,originalTab,'Tab reorder preserves active page identity');

  const openExternal=shell.openExternal, external=[];
  shell.openExternal=async address=>{external.push(address);};
  try {owner.tabContext(savedTab.id);click('Open in browser');assert.deepEqual(external,[url+'saved']);}
  finally {shell.openExternal=openExternal;}
  await owner.closeTab(duplicate.id);
  assert.equal(owner.closedTabs.at(-1),url+'saved');
  owner.shortcut({key:'T',control:true,shift:true});
  await until(()=>owner.tabs.length===3 && !owner.view.webContents.isLoading());
  assert.equal(owner.view.webContents.getURL(),url+'saved');
  // Read-only bookmark navigation is usable, but no mutation can be dispatched.
  owner.config.IsShared=true;
  owner.bookmarkContext(1,version);
  assert.equal(menu.find(item=>item.label==='Rename…').enabled,false);
  click('Copy address');assert.equal(await clipboard.readText(),url+'saved');
  owner.config.IsShared=false;
  // A menu built before an external tree change must never select a nearby new row.
  const staleOpen=menu.find(item=>item.label==='Open in new tab').click;
  owner.config.WebBookmarks.unshift({Name:'Inserted',Url:url+'inserted'});
  const count=owner.tabs.length;staleOpen();assert.equal(owner.tabs.length,count);
  // A real worker/session-attributed POST protects the inactive tab during Close other tabs.
  const kept=owner.activeTab;
  owner.selectTab(savedTab.id);
  await until(()=>!savedTab.lifecycle.frozen);
  await savedTab.view.webContents.executeJavaScript('void fetch("/upload",{method:"POST",body:"disposable"})');
  await until(()=>upload && savedTab.lifecycle.uploads.size>0);
  owner.selectTab(kept.id);
  const originalMessageBox=dialog.showMessageBox;let confirmations=0;
  dialog.showMessageBox=async()=>{confirmations++;return {response:0};};
  try {
    owner.tabContext(kept.id);click('Close other tabs');
    await until(()=>confirmations===1 && !savedTab.closePending && owner.tabs.length===2);
    assert.ok(owner.tabs.includes(savedTab),'Declined protected tab survives Close other tabs');
    assert.ok(owner.tabs.includes(kept));
  } finally {dialog.showMessageBox=originalMessageBox;}
  upload.end('done');await until(()=>savedTab.lifecycle.uploads.size===0);
  owner.tabContext(kept.id);click('Close other tabs');await until(()=>owner.tabs.length===1);
  assert.equal(owner.activeTab,kept);
  // A callback whose tab has been removed is inert.
  const oldCount=owner.tabs.length;
  owner.tabContext(kept.id);const staleDuplicate=menu.find(item=>item.label==='Duplicate').click;
  await owner.closeTab(kept.id);const afterClose=owner.tabs.length;
  assert.equal(afterClose,0);assert.equal(owner.window.isVisible(),false,'Closing the final tab hides the launcher');
  staleDuplicate();await delay(100);assert.equal(owner.tabs.length,afterClose);assert.equal(oldCount,1);
  console.log(JSON.stringify({passed:true,bookmarkStripDrag:true,tabStripDrag:true,externalDropIgnored:true,lastTabCloseHides:true,bookmarkSourcePathPreserved:true,homePreimageCaptured:true,bookmarkModifierOpen:true,
    inactiveTabAddressActions:true,duplicateRemainsInBackground:true,reopenClosedTab:true,
    readOnlyNavigation:true,staleBookmarkMenuRejected:true,realUploadProtectsCloseOthers:true,
    declinedTabPreserved:true,staleTabCallbackInert:true},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{
  ipcMain.removeAllListeners('toolbar-action');
  upload?.end();
  if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
  server.close();app.exit(process.exitCode||0);
});
