'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {bookmarkDrop}=require('../browser/strip_drag.cjs');
const {bookmarkVersion}=require('../browser/bookmarks.cjs');

test('bookmark drops resolve visible rows to full paths, including folder membership and hidden entries',()=>{
 const config={WebBookmarks:[{Url:'legacy:skip',Future:1},{IsFolder:true,Name:'Folder',Children:[]},{Name:'Saved',Url:'https://example.com',Future:9}]};
 const version=bookmarkVersion(config.WebBookmarks);
 const drop=bookmarkDrop(config,{version,source:1,target:0,inside:true,after:false});
 assert.deepEqual(drop.path,[2]);assert.deepEqual(drop.parent,[1]);assert.equal(drop.before,null);
 assert.deepEqual(drop.expected,config.WebBookmarks);
 assert.equal(bookmarkDrop(config,{version,source:0,target:0,inside:true,after:false}),null);
 assert.equal(bookmarkDrop(config,{version:'stale',source:1,target:0,inside:false,after:false}),null);
 const reorder=bookmarkDrop(config,{version,source:1,target:0,inside:false,after:false});
 assert.deepEqual(reorder.parent,[]);assert.equal(reorder.before,1);
});

test('nested bookmark source paths retain hidden siblings, reject descendants and target full saved collections',()=>{
 const config={WebBookmarks:[{IsFolder:true,Name:'A',Children:[{Url:'legacy:hidden'},{Url:'https://one.test',Future:8},{IsFolder:true,Name:'Nested',Children:[]}]},{Url:'https://two.test'}]};
 const version=bookmarkVersion(config.WebBookmarks);
 const move=bookmarkDrop(config,{source:[0,1],target:[1],version,inside:false,after:true});
 assert.deepEqual(move.path,[0,1]);assert.deepEqual(move.parent,[]);assert.equal(move.before,2);
 const inside=bookmarkDrop(config,{source:[1],target:[0,2],version,inside:true,after:false});
 assert.deepEqual(inside.parent,[0,2]);assert.equal(inside.before,null);
 assert.equal(bookmarkDrop(config,{source:[0],target:[0,2],version,inside:true,after:true}),null);
 for(const source of [[-1],[0,'1'],[],[999]])assert.equal(bookmarkDrop(config,{source,target:[1],version,inside:false,after:false}),null);
});
