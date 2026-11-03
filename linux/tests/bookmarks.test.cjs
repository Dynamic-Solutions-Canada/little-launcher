'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {projectBookmarks, bookmarkActions, bookmarkVersion} = require('../browser/bookmarks.cjs');

test('render revisions change when source membership or undisplayed metadata changes', () => {
  const tree = [{Name: 'Wiki', Url: 'https://example.com', Future: 1}];
  const version = bookmarkVersion(tree);
  assert.equal(bookmarkVersion(structuredClone(tree)), version);
  assert.notEqual(bookmarkVersion([{...tree[0], Future: 2}]), version);
  assert.notEqual(bookmarkVersion([{Name: 'Hidden', Url: 'custom:old'}, ...tree]), version);
});

test('display paths retain hidden saved indexes and editable empty folders', () => {
  const source = [{Name: 'Legacy', Url: 'file:///private'},
    {Name: 'Wiki', Url: 'https://example.com'},
    {Name: 'Empty', IsFolder: true, Children: []}];
  const before = structuredClone(source);
  const projection = projectBookmarks(source);
  assert.deepEqual(projection.map(item => item.sourcePath), [[1], [2]]);
  assert.equal(projection[1].Children.length, 0);
  assert.deepEqual(source, before);
});

test('native menu callbacks retain saved indexes and reject descendant destinations', () => {
  const source = [{Name: 'Work', IsFolder: true, Children: [
    {Name: 'Nested', IsFolder: true, Children: []},
    {Name: 'Wiki', Url: 'https://example.com'},
  ]}, {Name: 'Elsewhere', IsFolder: true, Children: []}];
  const calls = [];
  const menu = bookmarkActions(source, [0], (...args) => calls.push(args));
  const destinations = menu.find(item => item.label === 'Move to folder').submenu;
  assert.deepEqual(destinations.map(item => item.label), ['Elsewhere']);
  destinations[0].click();
  assert.deepEqual(calls[0], ['move', [0], {parent: [1]}]);
  const editContents = menu.find(item => item.label === 'Edit contents').submenu;
  editContents[1].submenu.find(item => item.label === 'Rename…').click();
  assert.deepEqual(calls[1], ['rename', [0, 1]]);
});


test('icon-only inheritance retains per-item choices, folder paths and explicit menu values', () => {
  const source = [{Name:'Hidden',Url:'legacy:skip'}, {Name:'Folder',IsFolder:true,IconsOnly:true,
    Children:[{Name:'Saved',Url:'https://example.com',IconsOnly:false,Future:9}]}];
  const calls=[];
  const projected=projectBookmarks(source);
  assert.equal(projected[0].IconsOnly,true);
  assert.deepEqual(projected[0].Children[0].sourcePath,[1,0]);
  const local=bookmarkActions(source,[1,0],(...args)=>calls.push(args));
  const toggle=local.find(item=>item.label==='Icon only');
  assert.equal(toggle.checked,false); assert.equal(toggle.enabled,true);
  toggle.click({checked:true});
  assert.deepEqual(calls,[['icons-only',[1,0],{value:true}]]);
  const inherited=bookmarkActions(source,[1,0],()=>{},true).find(item=>item.label==='Icon only');
  assert.equal(inherited.checked,true); assert.equal(inherited.enabled,false);
  assert.equal(source[1].Children[0].IconsOnly,false);
});


test('bookmark navigation precedes editing and stays available to read-only subscribers', () => {
  const tree=[{Name:'Saved',Url:'https://example.com',Future:7}];
  const calls=[];
  const menu=bookmarkActions(tree,[0],(...args)=>calls.push(args),false,{editable:false});
  assert.deepEqual(menu.filter(item=>item.type!=='separator').slice(0,5).map(item=>item.label),
    ['Open','Open in new tab','Rename…','Edit address…','Copy address']);
  for(const label of ['Open','Open in new tab','Copy address','Open in browser']) {
    const row=menu.find(item=>item.label===label);assert.equal(row.enabled,true);row.click();
  }
  for(const label of ['Rename…','Edit address…','Icon only','Set as home page']) assert.equal(menu.find(item=>item.label===label).enabled,false);
  assert.deepEqual(calls.map(call=>call[1]),[[0],[0],[0],[0]]);
  assert.equal(menu.some(item=>item.label==='Remove'),false);
  assert.equal(bookmarkActions(tree,[0],()=>{},false,{homeUrl:tree[0].Url}).find(item=>item.label==='Set as home page').enabled,false);
});
