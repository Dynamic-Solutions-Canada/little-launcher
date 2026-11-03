'use strict';
// Private compositor qualification only; remote and chrome assertions use disposable localhost pages.
const assert = require('node:assert/strict');
const {app, session, nativeImage} = require('electron');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
assert.equal(process.env.XDG_RUNTIME_DIR, '/tmp/ll-runtime');
const {WebLauncher} = require('../browser/window.cjs');
const {Profiles} = require('../browser/profiles.cjs');
app.setPath('userData', fs.mkdtempSync('/work/web-ui-'));
app.setDesktopName('io.github.RyanEwen.LittleLauncher.Web.desktop');
app.commandLine.appendSwitch('use-fake-device-for-media-stream');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check) { for(let i=0;i<100;i++){ if(await check()) return; await delay(50); } throw Error('Runtime condition timed out'); }
const iconRequests=[];
let png=nativeImage.createFromBitmap(Buffer.from([255,0,0,255]),{width:1,height:1}).toPNG();
const iconServer=http.createServer((req,res)=>{iconRequests.push(req.headers.cookie);res.setHeader('Content-Type','image/png');res.end(png);});
iconServer.listen(0,'127.0.0.1');
const server = http.createServer((_req, res) => {
  if(_req.url.endsWith('.ico') || _req.url==='/icon.png'){iconRequests.push(_req.headers.cookie);res.setHeader('Content-Type','image/png');res.end(png);return;}
  if(_req.url==='/oversize'){res.setHeader('Content-Type','image/png');res.end(Buffer.alloc(300*1024));return;}
  if(_req.url==='/redirect'){res.writeHead(302,{Location:'/icon.png'});res.end();return;}
  res.setHeader('Content-Type', 'text/html'); res.end(`<link rel="icon" href="http://127.0.0.1:${iconServer.address().port}/page.png"><title>UI fixture</title><a target="_blank" href="/child">Child tab</a><p>Disposable page</p>`);});
server.listen(0,'127.0.0.1');
const redirectServer=http.createServer((_req,res)=>{res.writeHead(302,{Location:`http://127.0.0.1:${server.address().port}/`});res.end();});
redirectServer.listen(0,'127.0.0.1');
let owner;
const extraOwners=[];
app.whenReady().then(async () => {
  const url = `http://127.0.0.1:${server.address().port}/`;
  const alias=`http://127.0.0.1:${redirectServer.address().port}/`;
  const redirectServices={isQuitting:()=>true,emit:()=>{},request:async()=>false};redirectServices.profiles=new Profiles(redirectServices.request,()=>null);
  const redirected=new WebLauncher({Id:'redirect-icons',Name:'Redirect fixture',WebShowBookmarkBar:true,WebBookmarks:[{Name:'Redirected bookmark',Url:alias}]},
    {window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,{homeUrl:alias,showBookmarkBar:true},redirectServices);
  extraOwners.push(redirected);redirected.show();
  await until(()=>!!redirected.bookmarkIcons.peek(alias));
  assert.equal(redirected.bookmarkIcons.peek(alias),redirected.bookmarkIcons.peek(url),'A verified redirect keeps the saved bookmark icon current');
  const {BookmarkIcons}=require('../browser/bookmark_icons.cjs');const isolated=new BookmarkIcons({},()=>{});
  isolated.observe('https://accounts.other.test/',[],['https://original.test/']);
  isolated.observe('http://www.example.test/',[],['https://example.test/']);
  assert.equal(isolated.aliases.size,0,'Foreign login redirects and HTTPS downgrades cannot replace bookmark icons');

  const {windowIcon}=require('../browser/window_icon.cjs');
  const expectedFallback=nativeImage.createFromPath('/source/linux/resources/little-launcher.png').toDataURL();
  assert.equal(windowIcon('/private/not-an-icon').toDataURL(),expectedFallback);
  const oversized=Buffer.from(png);oversized.writeUInt32BE(100000,16);
  assert.equal(windowIcon(oversized.toString('base64')).toDataURL(),expectedFallback);
  assert.equal(windowIcon(png.toString('base64')).isEmpty(),false);

  const records=[];
  const services={isQuitting:()=>true,emit:value=>records.push(value), request:async message=>message.type==='permission' && message.permission==='media'};
  services.profiles = new Profiles(services.request, contents => {
    const tab=owner?.tabs.find(tab=>tab.view?.webContents===contents);
    if(!tab)return;
    const facade=Object.create(owner);Object.defineProperty(facade,'lifecycle',{value:tab.lifecycle});return facade;
  });
  owner=new WebLauncher({Id:'ui-fixture',Name:'Web UI fixture',WebShowAddressBar:true,WebShowBookmarkBar:true,WebBookmarks:[{Name:'First',Url:url},{Name:'Second',Url:url+'second'}]},
    {window:'#eff0f1',text:'#232629',button:'#d9dada',highlight:'#3daee9',font:'Noto Sans',fontSize:10},
    null,{homeUrl:url,showBookmarkBar:true},services);
  owner.show();
  await until(()=>owner.window.webContents.getURL().endsWith('chrome.html') && !owner.window.webContents.isLoading());
  await until(()=>owner.view.webContents.getURL()===url && !owner.view.webContents.isLoading());
  owner.toolbar();await delay(100);
  const chrome=owner.window.webContents;
  const controls=await chrome.executeJavaScript(`Object.fromEntries(['back','forward','reload','home','more','address','pin','minimize','maximize','hide'].map(id=>[id,!!document.getElementById(id)]))`);
  assert.ok(Object.values(controls).every(Boolean));
  assert.equal(await chrome.executeJavaScript('document.querySelector("header").getBoundingClientRect().height'),34);
  assert.equal(owner.view.getBounds().y,68);
  assert.equal(await owner.view.webContents.executeJavaScript('typeof window.launcher + ":" + typeof process'), 'undefined:undefined');
  // A blank tab temporarily shows the address row. Navigating it must reclaim
  // exactly that row without needing a native resize or another tab switch.
  owner.config.WebShowAddressBar=false;
  owner.createView('about:blank');
  assert.equal(owner.view.getBounds().y,102);
  await until(()=>owner.view.webContents.getURL()==='about:blank' && !owner.view.webContents.isLoading());
  await owner.view.webContents.loadURL(url);
  await delay(100);
  assert.equal(await chrome.executeJavaScript('document.getElementById("address-form").hidden'),true);
  assert.equal(owner.view.getBounds().y,68);
  await owner.closeTab(owner.activeTab.id);
  owner.config.WebShowAddressBar=true;owner.toolbar();
  assert.equal(owner.view.getBounds().y,68);
  await until(()=>records.some(record=>record.type==='page-icon'));
  assert.ok(Buffer.from(records.find(record=>record.type==='page-icon').imageBase64,'base64').length>0);
  const aligned = await chrome.executeJavaScript(`(() => {const bar=document.getElementById('bookmarks');const visible=[...bar.children].filter(item=>!item.hidden);const first=visible[0].getBoundingClientRect(),last=visible.at(-1).getBoundingClientRect();return {middle:(first.left+last.right)/2,width:bar.clientWidth,overflow:bar.classList.contains('overflowing')};})()`);
  assert.equal(aligned.overflow,false);assert.ok(Math.abs(aligned.middle-aligned.width/2)<1,'Fitting bookmarks are centered');
  await until(()=>owner.bookmarkIcons.entries.get(new URL(url).origin)?.url.startsWith(`http://127.0.0.1:${iconServer.address().port}/`));
  const staleToken=owner.bookmarkIcons.entries.get(new URL(url).origin).token;
  const previousFavicon=owner.bookmarkIcons.get(url);
  png=nativeImage.createFromBitmap(Buffer.from([0,255,0,255]),{width:1,height:1}).toPNG();
  await owner.view.webContents.executeJavaScript(`document.querySelector('link[rel="icon"]').href += '?fresh=1'`);
  await until(()=>owner.bookmarkIcons.get(url)!==previousFavicon);
  await until(()=>chrome.executeJavaScript(`document.querySelector('[data-bookmark-index="0"] img')?.src`) .then(value=>value===owner.bookmarkIcons.get(url)));
  const refreshed=owner.bookmarkIcons.get(url);
  owner.bookmarkIcons.accept(staleToken,Buffer.from(previousFavicon.split(',')[1],'base64').toString('base64'));
  assert.equal(owner.bookmarkIcons.get(url),refreshed,'Late decode replies cannot replace a newer page icon');
  const first=owner.activeTab;
  await owner.view.webContents.executeJavaScript('document.querySelector("a").click()');
  await until(()=>owner.tabs.length===2 && owner.activeTab!==first && owner.view.webContents.getURL().endsWith('/child'));
  await until(()=>!owner.view.webContents.isLoading());
  await delay(250);
  assert.equal(owner.view.getBounds().y,102);
  assert.equal(owner.view.webContents.session, first.view.webContents.session);
  assert.equal(await owner.view.webContents.executeJavaScript('typeof window.launcher + ":" + typeof process'), 'undefined:undefined');
  require('node:child_process').execFileSync('python3', ['/source/linux/dev/capture_wayland.py', '/work/web-ui-desktop.png']);
  const child=owner.activeTab;
  await child.view.webContents.executeJavaScript('navigator.mediaDevices.getUserMedia({video:true}).then(stream=>{window.fixtureStream=stream;})');
  assert.equal(child.lifecycle.capturePermission,true);
  owner.selectTab(first.id);
  assert.equal(owner.busy,true,'Inactive tab capture protects launcher quit/removal');
  assert.equal(child.lifecycle.frozen,false,'Protected inactive tab must not freeze');
  owner.hide();
  child.lifecycle.lastHidden=Date.now()-600000;owner.tick();
  assert.ok(child.view,'Protected inactive tab must not unload');
  await child.view.webContents.executeJavaScript('window.fixtureStream.getTracks().forEach(track=>track.stop())');
  await child.view.webContents.reload();
  await until(()=>!child.view.webContents.isLoading() && !child.lifecycle.capturePermission);
  await owner.closeTab(child.id);
  assert.equal(owner.tabs.length,1);
  assert.equal(owner.activeTab,first);
  assert.equal(owner.view.webContents.getURL(),url,'Returning to tab must not navigate it');
  await first.view.webContents.executeJavaScript('void window.open("/sign-in", "auth", "width=400,height=500")');
  await until(()=>owner.tabs.length===2 && owner.activeTab!==first);
  const auth=owner.activeTab;
  assert.ok(auth.lifecycle.downloads.has(auth),'Script popup retains conservative sign-in protection as a tab');
  auth.lifecycle.downloads.clear();await auth.lifecycle.update();
  await owner.closeTab(auth.id);
  owner.hide();
  first.lifecycle.lastHidden=Date.now()-600000;owner.tick();
  assert.equal(first.view,null);
  owner.show();
  await until(()=>owner.view && owner.view.webContents.getURL()===url && !owner.view.webContents.isLoading());
  assert.equal(owner.activeTab,first);
  assert.equal(owner.tabs.length,1,'Waking an unloaded tab must not duplicate it');
  // Mode switches and preference application preserve the live page and its JavaScript state.
  const before=owner.view.webContents;
  assert.match(before.getUserAgent(), /Chrome\/156\./);
  assert.doesNotMatch(before.getUserAgent(), /Electron\//);
  await before.executeJavaScript('window.parityState=42');
  let previous=owner.config;
  owner.config={...owner.config,WebZoomPercent:125};owner.applyConfig(previous);
  assert.equal(before.getZoomFactor(),1.25);
  previous=owner.config;owner.config={...owner.config,WebRegularWindow:true};owner.applyConfig(previous);
  assert.equal(owner.view.webContents,before);
  assert.equal(await before.executeJavaScript('window.parityState'),42);
  owner.config.WebLinksInBrowser=true;
  const shell=require('electron').shell, originalExternal=shell.openExternal, external=[];
  shell.openExternal=async value=>{external.push(value);};
  try {
    await before.executeJavaScript('void window.open("/external", "_blank")');
    await until(()=>external.length===1);
    assert.equal(owner.tabs.length,1);
  } finally {shell.openExternal=originalExternal;}
  const profile=before.session;
  await profile.cookies.set({url,name:'parity-cookie',value:'disposable'});
  assert.ok((await profile.cookies.get({name:'parity-cookie'})).length);
  await services.profiles.clearData(owner.config,[owner]);
  await until(()=>owner.view && owner.view.webContents.getURL()===url && !owner.view.webContents.isLoading());
  assert.equal((await profile.cookies.get({name:'parity-cookie'})).length,0);
  assert.equal(owner.tabs.length,1);
  owner.config.WebReloadOnShow=true;
  await owner.view.webContents.executeJavaScript('window.reloadWitness=42');
  owner.hide();owner.show();await delay(100);
  await until(()=>!owner.view.webContents.isLoading());
  assert.equal(await owner.view.webContents.executeJavaScript('typeof window.reloadWitness'),'undefined');
  owner.hide();
  const preloaded=new WebLauncher({Id:'preload-anchor',Name:'Preloaded',WebHiddenPolicy:2,WebFlyoutWidth:480,WebFlyoutHeight:320},
    owner.theme,null,{homeUrl:url,showBookmarkBar:false},services);
  extraOwners.push(preloaded);
  await until(()=>preloaded.view.webContents.getURL()===url && !preloaded.view.webContents.isLoading());
  const retained=preloaded.view.webContents;
  preloaded.show([1100,770]);
  assert.equal(preloaded.anchored,true);
  assert.equal(preloaded.view.webContents,retained);
  assert.match(preloaded.window.getTitle(),/^LittleLauncherFlyout:preload-anchor:1100:770/);
  previous=preloaded.config;preloaded.config={...preloaded.config,WebRegularWindow:true};preloaded.applyConfig(previous);
  assert.equal(preloaded.anchored,false);
  assert.equal(preloaded.view.webContents,retained);
  previous=preloaded.config;preloaded.config={...preloaded.config,WebRegularWindow:false};preloaded.applyConfig(previous);
  assert.equal(preloaded.anchored,true);
  assert.equal(preloaded.view.webContents,retained);
  owner.show();
  // Verify real renderer icon inheritance, accessible labels, favicon bounds and overflow.
  await until(()=>!owner.window.webContents.isLoading());
  owner.config.WebShowBookmarkBar=true;
  owner.presentation.showBookmarkBar=true;
  owner.config.WebBookmarks=[{Name:'Undisplayed',Url:'legacy:skip'},
    {Name:'Local icon choice',Url:url,IconsOnly:true,Future:9},
    {Name:'Folder',IsFolder:true,Children:[]},
    ...Array.from({length:30},(_,i)=>({Name:'Bookmark '+i,Url:url+'?bookmark='+i}))];
  await profile.cookies.set({url,name:'favicon-private',value:'disposable'});
  owner.toolbar();owner.layout();
  const currentChrome=owner.window.webContents;
  assert.ok(await owner.bookmarkIcons.load(url+'icon.png'), 'Fixture PNG decodes to a chrome-safe icon');
  await until(()=>iconRequests.length>0 && owner.bookmarkIcons.get(url));
  owner.toolbar();await delay(100);
  const inspect=()=>currentChrome.executeJavaScript(`(() => {
    const buttons=[...document.querySelectorAll('[data-bookmark-index]')];
    return {firstIconOnly:buttons[0].querySelector('span').hidden,
      firstWidth:buttons[0].getBoundingClientRect().width,firstTooltip:buttons[0].title,
      firstImage:buttons[0].querySelector('img')?.src,
      folderLabel:buttons[1].querySelector('span').hidden,
      allIconsOnly:buttons.every(button=>button.querySelector('span').hidden),
      overflow:!document.querySelector('.bookmark-overflow').hidden,
      nativeControls:['title','minimize','maximize','hide'].every(id=>document.getElementById(id).hidden)};
  })()`);
  let bar=await inspect();
  assert.equal(bar.firstIconOnly,true);assert.equal(bar.firstWidth,30);
  assert.match(bar.firstTooltip,/Local icon choice/);assert.match(bar.firstImage,/^data:image\/png;base64,/);
  assert.equal(bar.folderLabel,false);assert.equal(bar.overflow,true);assert.equal(bar.nativeControls,true);
  assert.ok(await currentChrome.executeJavaScript('!!document.querySelector(".tab-icon img")'),'Tab favicon uses the validated site icon');
  assert.ok(iconRequests.every(cookie=>!cookie),'Favicon requests omit profile credentials');
  assert.equal(await owner.bookmarkIcons.load(url+'oversize'),null);
  assert.equal(await owner.bookmarkIcons.load(url+'not-an-image'),null);
  const beforeFallback=owner.bookmarkIcons.get(url);
  png=nativeImage.createFromBitmap(Buffer.from([180,0,180,255]),{width:1,height:1}).toPNG();
  owner.bookmarkIcons.observe(url,[url+'not-an-image',url+'icon.png']);
  await until(()=>!owner.bookmarkIcons.active);
  assert.notEqual(owner.bookmarkIcons.get(url),beforeFallback,'A failed first declaration falls back to the next raster icon');
  const oldIcon=owner.bookmarkIcons.get(url);
  owner.bookmarkIcons.observe(url,[url+'not-an-image']);
  await until(()=>!owner.bookmarkIcons.active);
  assert.equal(owner.bookmarkIcons.get(url),oldIcon,'Failed icon refresh retains the existing image');
  await assert.rejects(owner.bookmarkIcons.load(url+'redirect'));
  owner.config.WebBookmarkIconsOnly=true;owner.toolbar();await delay(100);
  bar=await inspect();assert.equal(bar.allIconsOnly,true);assert.equal(bar.overflow,true);
  owner.config.WebBookmarkIconsOnly=false;owner.toolbar();await delay(100);
  bar=await inspect();assert.equal(bar.firstIconOnly,true);assert.equal(bar.folderLabel,false);
  assert.equal(owner.config.WebBookmarks[1].Future,9);
  assert.equal(owner.config.WebBookmarks[2].IconsOnly,undefined);
  require('node:child_process').execFileSync('python3', ['/source/linux/dev/capture_wayland.py', '/work/bookmark-icons-desktop.png']);
  console.log(JSON.stringify({bookmarkIconsOnly:true,bookmarkFavicons:true,bookmarkIconTooltips:true,bookmarkOverflow:true,bookmarkCentering:true,loadedPageFaviconRefresh:true,redirectedBookmarkIcon:true,foreignLoginIconRejected:true,faviconCandidateFallback:true,
    nativeFrameControlsOnly:true,pageDeclaredCdnIconPreview:true,faviconCredentialsOmitted:true,faviconContentBounds:true,
    headerHeight:34,headerControls:controls,ordinaryContentTop:68,tabbedContentTop:102,
    preloadedFirstOpenAnchored:true,modeSwitchRetainsPage:true,zoomPreferenceApplied:true,externalLinkPolicyApplied:true,explicitProfileClearVerified:true,reloadOnOpenVerified:true,
    newWindowBecomesTab:true,tabSessionSharedWithinLauncher:true,remoteBridgeAbsent:true,
    inactiveCaptureProtectsQuit:true,actualFakeDeviceCapture:true,popupSignInProtectionRetained:true,protectedTabNotFrozenOrUnloaded:true,returnDoesNotNavigate:true,unloadedTabRestoresWithoutDuplication:true},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{
  for(const extra of extraOwners){for(const tab of extra.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}extra.window.destroy();}
  if(owner){for(const tab of owner.tabs) {tab.closing=true;if(tab.view&&!tab.view.webContents.isDestroyed())tab.view.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
  redirectServer.close();iconServer.close();server.close();app.exit(process.exitCode || 0);
});
