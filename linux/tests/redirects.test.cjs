'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict');
const {allowedPageRedirect}=require('../browser/urls.cjs');
const id='abcdefghijklmnopabcdefghijklmnop';
test('installed extension subframe redirects retain native WAR handling',()=>{
 assert.equal(allowedPageRedirect(`chrome-extension://${id}/overlay.html`,false,value=>value===id),true);
 assert.equal(allowedPageRedirect(`chrome-extension://${id}/overlay.html`,false,()=>null),false);
});
test('top-level extension redirects and non-web unsafe schemes remain blocked',()=>{
 for(const main of [true,undefined])assert.equal(allowedPageRedirect(`chrome-extension://${id}/overlay.html`,main,()=>true),false);
 for(const url of ['file:///etc/passwd','javascript:bad()','data:text/html,bad','chrome://settings',`chrome-extension://name:password@${id}/overlay.html`,'invalid'])assert.equal(allowedPageRedirect(url,false,()=>true),false);
 assert.equal(allowedPageRedirect('https://example.com/',true,()=>false),true);
});
