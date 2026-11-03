'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {moreTemplate} = require('../browser/web_menu.cjs');
const {browserUserAgent,googleSignIn} = require('../browser/browser_identity.cjs');
function fixture(config={}) {
  const changes=[];
  const owner={config,canEditBookmarks:()=>true,preference:(...args)=>changes.push(args),
    view:{webContents:{getURL:()=> 'https://example.com',getTitle:()=> 'Example'}},settings:()=>{},
    action:()=>{},showShortcuts:()=>{},bookmarkRequest:(value)=>changes.push(value)};
  return {menu:moreTemplate(owner),changes};
}
test('browser identity reports the actual Linux Chromium engine without app tokens',()=>{
  const value=browserUserAgent('156.0.8078.48','arm64');
  assert.match(value,/Linux aarch64/);assert.match(value,/Chrome\/156.0.8078.48/);
  assert.doesNotMatch(value,/Electron|LittleLauncher|Windows/);
  assert.throws(()=>browserUserAgent(null));
});
test('Google UA experiment only matches the exact secure sign-in host',()=>{
  assert.equal(googleSignIn('https://accounts.google.com/ServiceLogin'),true);
  for(const address of ['https://messages.google.com/','https://www.youtube.com/','https://accounts.google.com.evil.test/','http://accounts.google.com/','https://name:password@accounts.google.com/','file:///accounts.google.com','invalid'])assert.equal(googleSignIn(address),false);
});
test('quick controls retain Windows section/bar order and save explicit wire settings',()=>{
  const {menu,changes}=fixture({WebAlwaysShowTabs:true});
  assert.deepEqual(menu.filter(item=>item.submenu).slice(0,5).map(item=>item.label),
    ['General','Appearance','Size and position','Behavior','Privacy and sign-ins']);
  const appearance=menu.find(item=>item.label==='Appearance').submenu;
  assert.deepEqual(appearance.slice(0,3).map(item=>item.label),['Tab bar','Address bar','Bookmarks bar']);
  appearance[0].click();assert.deepEqual(changes,[['WebAlwaysShowTabs',false]]);
});
test('zoom choices retain an imported custom percentage and store a portable selection',()=>{
  const {menu,changes}=fixture({WebZoomPercent:120});
  const size=menu.find(item=>item.label==='Size and position').submenu;
  const zoom=size[0].submenu;
  assert.equal(zoom.find(item=>item.label==='120%').checked,true);
  zoom.find(item=>item.label==='125%').click();
  assert.deepEqual(changes,[['WebZoomPercent',125]]);
  size.find(item=>item.label==='Remember size changes').click();
  assert.deepEqual(changes[1],['WebLockSize',true]);
});
test('mode-dependent behavior controls and existing-bookmark removal use the current source path',()=>{
  const {menu,changes}=fixture({WebRegularWindow:true,WebBookmarks:[{IsFolder:true,Children:[{Name:'Example',Url:'https://example.com'}]}]});
  const behavior=menu.find(item=>item.label==='Behavior').submenu;
  assert.equal(behavior[1].label,'Close when focus is lost');
  behavior[0].click();assert.deepEqual(changes[0],['WebRegularWindow',false]);
  assert.equal(menu[0].label,'Remove from bookmarks');menu[0].click();
  assert.deepEqual(changes[1].path,[0,0]);assert.equal(changes[1].action,'remove');
  assert.ok(!fixture().menu.find(item=>item.label==='Behavior').submenu.some(item=>item.label==='Close when focus is lost'));
});


test('zoom steps preserve imported values at ladder boundaries instead of reversing direction',()=>{
 const {stepZoom}=require('../browser/web_menu.cjs');
 assert.equal(stepZoom(25,-1),25);assert.equal(stepZoom(400,1),400);
 assert.equal(stepZoom(25,1),50);assert.equal(stepZoom(400,-1),200);
 assert.equal(stepZoom(83,-1),80);assert.equal(stepZoom(83,1),90);
 assert.equal(stepZoom(300,1),300);
});
