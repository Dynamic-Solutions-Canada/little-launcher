'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {requestContents} = require('../browser/request_contents.cjs');
test('worker/missing or invalid request IDs never reach Electron fromId',()=>{
  const lookup=()=>{throw Error('Lookup must not be called');};
  for(const id of [undefined,null,-1,0,NaN,Infinity,'12',2**40])assert.equal(requestContents({webContentsId:id},lookup),null);
  assert.equal(requestContents({},lookup),null);
});
test('live supplied contents and valid IDs resolve without accepting destroyed senders',()=>{
  const contents={isDestroyed:()=>false};
  assert.equal(requestContents({webContents:contents},()=>{throw Error();}),contents);
  assert.equal(requestContents({webContentsId:12},id=>{assert.equal(id,12);return contents;}),contents);
  assert.equal(requestContents({webContentsId:12},()=>({isDestroyed:()=>true})),null);
  assert.equal(requestContents({webContentsId:12},()=>undefined),null);
  assert.equal(requestContents({webContentsId:12},()=>{throw Error('Renderer vanished');}),null);
});
