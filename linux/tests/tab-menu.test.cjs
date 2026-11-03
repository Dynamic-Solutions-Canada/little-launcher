'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {tabTemplate}=require('../browser/tab_menu.cjs');

test('tab menus use the clicked inactive tab and Windows command order',()=>{
  const tab={id:7,url:'https://example.com/saved'};
  const owner={tabs:[{id:1},tab],closedTabs:['https://closed.example'],config:{WebBookmarks:[{Url:tab.url}]},canEditBookmarks:()=>false};
  const calls=[];
  const menu=tabTemplate(owner,tab,action=>calls.push(action));
  assert.deepEqual(menu.filter(item=>item.type!=='separator').map(item=>item.label),
    ['Reload','Duplicate','Copy address','Open in browser','Remove from bookmarks','Close tab','Close other tabs','Reopen closed tab']);
  assert.equal(menu[0].enabled,false,'An idle-unloaded tab cannot be reloaded');
  assert.equal(menu.find(item=>item.label==='Remove from bookmarks').enabled,false);
  menu.find(item=>item.label==='Close other tabs').click();assert.deepEqual(calls,['close-others']);
  assert.equal(tabTemplate({...owner,closedTabs:[]},null,()=>{})[0].enabled,false);
});
