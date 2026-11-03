'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {PermissionStore}=require('../browser/permission_store.cjs');
const profile='a'.repeat(64),other='b'.repeat(64);
test('notification decisions survive reconstruction, remain profile-local, and reset explicitly',()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'ll-permissions-'));try{
  const file=path.join(dir,'site-permissions.json');const first=new PermissionStore(file);first.set(profile,'https://web.whatsapp.com',true);first.set(profile,'https://denied.example',false);
  const restarted=new PermissionStore(file);assert.deepEqual(restarted.entries(profile),[['https://web.whatsapp.com',true],['https://denied.example',false]]);assert.deepEqual(restarted.entries(other),[]);
  assert.equal(fs.statSync(file).mode&0o777,0o600);restarted.clear(profile);assert.deepEqual(new PermissionStore(file).entries(profile),[]);
 }finally{fs.rmSync(dir,{recursive:true});}
});
test('unreadable permissions are preserved and cannot be overwritten by a new choice',()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'ll-permissions-'));try{
  const file=path.join(dir,'site-permissions.json');fs.writeFileSync(file,'broken');const store=new PermissionStore(file);assert.equal(store.unreadable,true);assert.throws(()=>store.set(profile,'https://example.com',true));assert.equal(fs.readFileSync(file,'utf8'),'broken');
 }finally{fs.rmSync(dir,{recursive:true});}
});
test('only bounded origin notification booleans enter the permission file',()=>{
 const store=new PermissionStore(path.join(os.tmpdir(),'nonexistent-'+Date.now()));
 for(const origin of ['file:///tmp/a','https://example.com/path','https://u:p@example.com'])assert.throws(()=>store.set(profile,origin,true));
 assert.throws(()=>store.set(profile,'https://example.com','media'));
});
