'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
const {PageNotifications,secureOrigin}=require('../browser/page_notifications.cjs');
function fixture(allowed=true){
 const notices=[],commands=[];let activations=0,address='https://fixture.test/page';
 class NativeNotice extends EventEmitter{constructor(options){super();this.options=options;notices.push(this);}show(){this.emit('show');}close(){this.emit('close');}}
 const contents=new EventEmitter();contents.getURL=()=>address;contents.isDestroyed=()=>false;contents.debugger={sendCommand:async(method,params)=>commands.push({method,params})};
 const decisions={'https://fixture.test':allowed};
 const client=new PageNotifications(contents,{decisions:()=>decisions,permission:async()=>allowed?'granted':'denied',activate:()=>activations++,NotificationType:NativeNotice});
 const context={id:1,uniqueId:'actual-native-context',origin:'https://fixture.test',auxData:{isDefault:true}};client.contexts.set(1,context);
 const send=(method,value={},id=1)=>client.handle({executionContextId:1,payload:JSON.stringify({id,method,value})});
 return {client,context,contents,decisions,notices,commands,send,activations:()=>activations,navigate:value=>address=value};
}
test('notification origin trust accepts real loopback, never lookalike insecure hosts',()=>{
 for(const url of ['https://fixture.test','http://localhost','http://a.localhost','http://127.0.0.1','http://127.5.6.7','http://[::1]'])assert.equal(secureOrigin(url),true,url);
 for(const url of ['http://127.evil.test','http://127.0.0.1.evil.test','http://fixture.test','file:///tmp/a','data:text/html,x'])assert.equal(secureOrigin(url),false,url);
});
test('only an allowed native-frame notification creates desktop UI and native click activates it',async()=>{
 const f=fixture();await f.send('show',{notice:7,title:'Fixture',options:{body:'Synthetic'}});
 assert.equal(f.notices.length,1);assert.equal(f.activations(),0);
 f.notices[0].emit('click');assert.equal(f.activations(),1);assert.equal(f.client.records.size,0);
 assert.ok(f.commands.some(value=>value.params.expression.includes('"event":"click"')));
});
test('unknown and denied origins cannot display notifications or activate a launcher',async()=>{
 for(const value of [false,undefined]){const f=fixture(value);if(value===undefined)delete f.decisions['https://fixture.test'];await f.send('show',{notice:1,title:'No permission',options:{}});assert.equal(f.notices.length,0);assert.equal(f.activations(),0);assert.ok(f.commands.some(v=>v.params.expression.includes('"error":true')));}
});
test('foreign or isolated execution contexts and general application commands are rejected',async()=>{
 for(const change of [{origin:'https://foreign.test'},{auxData:{isDefault:false}}]){const f=fixture();Object.assign(f.context,change);await f.send('show',{notice:1,title:'Spoof',options:{}});assert.equal(f.notices.length,0);assert.equal(f.commands.length,0);}
 const f=fixture();for(const method of ['activate','open-file','launch','settings','grant'])await f.send(method);assert.equal(f.commands.length,0);assert.equal(f.activations(),0);
});
test('stale documents and revoked permissions cannot receive or act on old notices',async()=>{
 const f=fixture();await f.send('show',{notice:1,title:'Fixture',options:{}});f.decisions['https://fixture.test']=false;f.notices[0].emit('click');assert.equal(f.activations(),0);
 const count=f.commands.length;f.navigate('https://different.test/');await f.client.deliver(f.context,{notice:1,event:'click'});assert.equal(f.commands.length,count);
});
test('one native context cannot close another document notification, and display rate is bounded',async()=>{
 const f=fixture();await f.send('show',{notice:1,title:'Fixture',options:{}});f.context.uniqueId='different-document';await f.send('close',{notice:1});assert.equal(f.client.records.size,1);
 f.client.recent=Array(60).fill(Date.now());await f.send('show',{notice:2,title:'Rate limited',options:{}});assert.equal(f.notices.length,1);f.client.close();assert.equal(f.client.records.size,0);
});
const {notificationClientAction}=require('../browser/worker_notifications.cjs');
test('worker client actions require a live native click; focus cannot cross origins',async()=>{
 const worker={},calls=[],tasks=new Map([['live',{worker,type:'notificationclick',origin:'https://fixture.test',candidate:{notificationClient:async(...args)=>{calls.push(args);return true;}}}]]);
 for(const method of ['focus','open'])assert.equal(await notificationClientAction(tasks,worker,'https://fixture.test',method,{token:'live',url:'https://fixture.test/inbox'}),true);
 assert.equal(calls.length,2);
 await assert.rejects(notificationClientAction(tasks,worker,'https://fixture.test','focus',{token:'live',url:'https://foreign.test/'}));
 assert.equal(await notificationClientAction(tasks,worker,'https://fixture.test','open',{token:'live',url:'https://foreign.test/'}),true);
 for(const value of [{token:'expired',url:'https://fixture.test/'},{token:'live',url:'file:///tmp/file'},{token:'live',url:'https://user:pass@fixture.test/'},{token:'live',url:'javascript:alert(1)'}])await assert.rejects(notificationClientAction(tasks,worker,'https://fixture.test','open',value));
 await assert.rejects(notificationClientAction(tasks,{},'https://fixture.test','focus',{token:'live',url:'https://fixture.test/'}));
 tasks.get('live').type='notificationclose';await assert.rejects(notificationClientAction(tasks,worker,'https://fixture.test','focus',{token:'live',url:'https://fixture.test/'}));
 assert.equal(calls.length,3);
});
test('tag replacement closes only the previous notification in the same native context',async()=>{
 const f=fixture();await f.send('show',{notice:1,title:'First',options:{tag:'conversation'}});await f.send('show',{notice:2,title:'Replacement',options:{tag:'conversation'}});assert.equal(f.client.records.size,1);assert.equal(f.client.tags.size,1);await f.send('close',{notice:2});assert.equal(f.client.records.size,0);assert.equal(f.client.tags.size,0);
});
