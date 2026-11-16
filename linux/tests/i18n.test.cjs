'use strict';
const {test,after}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const i18n=require('../browser/i18n.cjs');

const roots=[];
after(()=>{for(const root of roots)fs.rmSync(root,{recursive:true,force:true});});

/** Synthetic catalogs: a full English source and partial translations with one corrupt area. */
function fixture() {
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'little-launcher-i18n-'));
  roots.push(root);
  const write=(catalog,name,content)=>{
    fs.mkdirSync(path.join(root,catalog),{recursive:true});
    fs.writeFileSync(path.join(root,catalog,name),typeof content==='string'?content:JSON.stringify(content));
  };
  write('en-US','sample.json',{'sample.greeting':'Hello {name}','sample.only':'English only',
    'sample.items.one':'{count} item','sample.items.other':'{count} items','sample.pair':'{first} and {second}'});
  write('fr-FR','sample.json',{'sample.greeting':'Bonjour {name}','sample.items.one':'{count} élément','sample.items.other':'{count} éléments'});
  write('fr-FR','broken.json','{"sample.only": ');
  write('ja-JP','sample.json',{'sample.items.one':'{count} 件','sample.items.other':'{count} 件'});
  write('de-DE','sample.json',['not','an','object']);
  return root;
}

test('unconfigured lookups read the shared English catalog beside the source tree',()=>{
  assert.equal(i18n.current(),'en-US');
  assert.equal(i18n.t('common.ok'),'OK');
  assert.equal(i18n.t('webTabs.closeTab'),'Close tab');
});

test('missing keys fall back to English, then to the key; corrupt areas are skipped',()=>{
  i18n.configure({catalog:'fr-FR',directory:fixture()});
  assert.equal(i18n.t('sample.greeting',{name:'Ada'}),'Bonjour Ada');
  assert.equal(i18n.t('sample.only'),'English only','A corrupt French area falls back to English');
  assert.equal(i18n.t('no.such.key'),'no.such.key');
  i18n.configure({catalog:'de-DE'});
  assert.equal(i18n.t('sample.only'),'English only','A non-object area file is ignored');
});

test('unknown or unsafe catalog names mean English and never leave the catalog directory',()=>{
  const root=fixture();
  for(const name of ['it-IT','../fr-FR','fr-CA','',undefined,'constructor']) {
    i18n.configure({catalog:name,directory:root});
    assert.equal(i18n.current(),'en-US');
    assert.equal(i18n.t('sample.greeting',{name:'Ada'}),'Hello Ada');
  }
  i18n.configureFromArguments(['electron','main.cjs','--launcher-language=fr-FR','--launcher-catalogs='+root]);
  assert.equal(i18n.current(),'fr-FR');
  i18n.configureFromArguments(['electron','main.cjs','--launcher-catalogs=relative/path']);
  assert.equal(i18n.current(),'en-US','A missing language argument means English');
  assert.equal(i18n.t('sample.only'),'English only','A relative directory keeps the previous location');
});

test('placeholders are filled once and inserted values are never expanded',()=>{
  i18n.configure({catalog:'en-US',directory:fixture()});
  assert.equal(i18n.t('sample.pair',{first:'{second}',second:'x'}),'{second} and x');
  assert.equal(i18n.t('sample.greeting'),'Hello {name}','No values leaves the template intact');
  assert.equal(i18n.t('sample.greeting',{other:1}),'Hello {name}','Unknown placeholders stay literal');
  assert.equal(i18n.t('sample.greeting',{}),'Hello {name}');
  assert.equal(i18n.t('sample.pair',{first:'a'}),'a and {second}');
  assert.equal(i18n.t('sample.greeting',Object.create({name:'inherited'})),'Hello {name}','Only own values are used');
  assert.equal(i18n.t('sample.greeting',{name:0}),'Hello 0');
});

test('plural forms follow the shared i18n.py rule',()=>{
  const root=fixture();
  i18n.configure({catalog:'en-US',directory:root});
  assert.equal(i18n.plural('sample.items',1),'1 item');
  assert.equal(i18n.plural('sample.items',0),'0 items');
  assert.equal(i18n.plural('sample.items',2),'2 items');
  i18n.configure({catalog:'fr-FR'});
  assert.equal(i18n.plural('sample.items',0),'0 élément','French uses one for zero');
  assert.equal(i18n.plural('sample.items',1),'1 élément');
  assert.equal(i18n.plural('sample.items',2),'2 éléments');
  i18n.configure({catalog:'ja-JP'});
  assert.equal(i18n.plural('sample.items',1),'1 件','Japanese always uses other');
  for(const [identity,count,one] of [['pt-BR',0,true],['pt-BR',2,false],['zh-CN',1,false],['de-DE',1,true],['es-ES',0,false]])
    assert.equal(i18n.usesOne(identity,count),one,identity+' '+count);
});

test('page strings are resolved, frozen and limited to the requested names',()=>{
  i18n.configure({catalog:'fr-FR',directory:fixture()});
  const strings=i18n.strings({greeting:['sample.greeting',{name:'{x}'}],only:'sample.only'});
  assert.deepEqual({...strings},{greeting:'Bonjour {x}',only:'English only'});
  assert.ok(Object.isFrozen(strings));
});
