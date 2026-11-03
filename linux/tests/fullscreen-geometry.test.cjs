'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict');
const {fitVideoSize}=require('../browser/fullscreen_geometry.cjs');
test('video fitting uses intrinsic ratio and respects minimums and desktop work area',()=>{
 assert.deepEqual(fitVideoSize(600,16/9,{width:1200,height:700}),{width:600,height:338});
 assert.deepEqual(fitVideoSize(1200,16/9,{width:1200,height:600}),{width:1067,height:600});
 assert.deepEqual(fitVideoSize(320,16/9,{width:1200,height:700}),{width:427,height:240});
 assert.equal(fitVideoSize(500,100,{width:1200,height:700}),null);
 assert.equal(fitVideoSize(500,.01,{width:1200,height:700}),null);
 for(const ratio of [NaN,Infinity,0,-2])assert.equal(fitVideoSize(500,ratio,{width:1200,height:700}),null);
});
