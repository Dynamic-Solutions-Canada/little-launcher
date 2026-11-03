'use strict';
const {Notification}=require('electron');
const crypto=require('node:crypto');
const net=require('node:net');
const CONTROL='little-launcher.notification-presentation';

/** Standard Notification/Permissions APIs only: no Node, preload, files or general app bridge. */
function installPageNotifications(bindingName, decisions) {
 const send=globalThis[bindingName];delete globalThis[bindingName];
 if(!globalThis.Notification || !globalThis.isSecureContext || !['http:','https:'].includes(location.protocol))return;
 try{if(top.location.origin!==location.origin)return;}catch{return;}
 const key=Symbol.for('little-launcher.notification-presentation');
 const permission=decisions[location.origin]===true?'granted':decisions[location.origin]===false?'denied':'default';
 if(Notification[key]){Notification[key].update(permission);return;}
 if(typeof send!=='function')return;
 const NativeNotification=Notification,NativeEvent=Event,Target=EventTarget;
 const nativeQuery=navigator.permissions.query.bind(navigator.permissions);
 const notices=new Map(),pending=new Map(),statuses=new Set(),collected=new FinalizationRegistry(ref=>statuses.delete(ref));let sequence=0,state=permission;
 function changed(){for(const ref of statuses){const status=ref.deref();if(status)status.dispatchEvent(new NativeEvent('change'));else statuses.delete(ref);}}
 function update(value){if(value===state)return;state=value;changed();}
 function request(method,value){return new Promise((resolve,reject)=>{if(pending.size>=256){reject(new DOMException('Too many notification requests.','QuotaExceededError'));return;}const id=++sequence;let payload;try{payload=JSON.stringify({id,method,value});}catch{reject(new DOMException('Notification data cannot be copied.','DataCloneError'));return;}if(payload.length>65536){reject(new DOMException('Notification is too large.','DataCloneError'));return;}const timer=setTimeout(()=>{pending.delete(id);reject(new DOMException('Notification request expired.','AbortError'));},125000);pending.set(id,{resolve,reject,timer});send(payload);});}
 class PageNotification extends Target {
  constructor(title,options={}){
   super();if(options.actions?.length)throw new TypeError('Notification actions require a service worker.');if(notices.size>=128)throw new DOMException('Too many notifications.','QuotaExceededError');
   const id=++sequence;Object.defineProperty(this,'__id',{value:id});notices.set(id,this);
   for(const [name,value]of Object.entries({title:String(title),body:options.body||'',tag:options.tag||'',data:options.data??null,icon:options.icon||'',badge:options.badge||'',image:options.image||'',dir:options.dir||'auto',lang:options.lang||'',silent:options.silent===true,requireInteraction:options.requireInteraction===true,renotify:options.renotify===true,timestamp:options.timestamp??Date.now()}))Object.defineProperty(this,name,{value,enumerable:true});
   void request('show',{notice:id,title:String(title),options}).catch(()=>{notices.delete(id);this.dispatchEvent(new NativeEvent('error'));});
  }
  close(){void request('close',{notice:this.__id}).catch(()=>{});}
  static get permission(){return state;}
  static requestPermission(callback){return request('permission',{}).then(value=>{const before=state;state=value.state;callback?.(state);if(before!==state)changed();return state;});}
 }
 Object.defineProperty(PageNotification,'maxActions',{get:()=>NativeNotification.maxActions});
 // Keep the familiar prototype chain while EventTarget supplies the real event slots.
 Object.setPrototypeOf(PageNotification.prototype,NativeNotification.prototype);
 for(const type of ['click','show','close','error'])Object.defineProperty(PageNotification.prototype,'on'+type,{configurable:true,get(){return this['_on'+type]||null;},set(value){if(this['_on'+type])this.removeEventListener(type,this['_on'+type]);this['_on'+type]=typeof value==='function'?value:null;if(this['_on'+type])this.addEventListener(type,this['_on'+type]);}});
 Object.defineProperty(PageNotification,key,{value:{update,receive(value){
  if(value.reply){const task=pending.get(value.reply);if(!task)return;pending.delete(value.reply);clearTimeout(task.timer);value.error?task.reject(new DOMException('Notification permission is required.','NotAllowedError')):task.resolve(value.result);return;}
  const notice=notices.get(value.notice);if(!notice)return;notice.dispatchEvent(new NativeEvent(value.event));if(value.event==='close'||value.event==='error')notices.delete(value.notice);
 }}});
 navigator.permissions.query=async function(descriptor){const result=await nativeQuery(descriptor);if(descriptor?.name!=='notifications')return result;Object.defineProperty(result,'state',{configurable:true,get:()=>state==='default'?'prompt':state});const ref=new WeakRef(result);statuses.add(ref);collected.register(result,ref);return result;};
 Object.defineProperty(globalThis,'Notification',{configurable:true,writable:true,value:PageNotification});
}
function secureOrigin(value){try{const url=new URL(value);return url.protocol==='https:'||url.protocol==='http:'&&(url.hostname==='localhost'||url.hostname.endsWith('.localhost')||url.hostname==='[::1]'||net.isIP(url.hostname)===4&&url.hostname.startsWith('127.'));}catch{return false;}}

/** A binding is scoped to native frame identity and implements only Web Notifications. */
class PageNotifications {
 constructor(contents,{decisions,permission,activate,NotificationType=Notification}){
  Object.assign(this,{contents,decisions,permission,activate,NotificationType});this.binding='__llNotification_'+crypto.randomUUID().replaceAll('-','');this.contexts=new Map();this.records=new Map();this.tags=new Map();this.recent=[];this.pending=Promise.resolve();
  contents.once('destroyed',()=>{this.close();this.contexts.clear();});
 }
 source(){return '('+installPageNotifications.toString()+')('+JSON.stringify(this.binding)+','+JSON.stringify(this.decisions())+');';}
 async initialize(){
  const debug=this.contents.debugger;if(!debug.isAttached())debug.attach('1.3');
  debug.on('message',(_event,method,params)=>{
   if(method==='Runtime.executionContextCreated')this.contexts.set(params.context.id,params.context);
   if(method==='Runtime.executionContextDestroyed')this.contexts.delete(params.executionContextId);
   if(method==='Runtime.executionContextsCleared'){this.contexts.clear();this.close();}
   if(method==='Runtime.bindingCalled'&&params.name===this.binding)void this.handle(params);
  });
  await debug.sendCommand('Page.enable');await debug.sendCommand('Runtime.enable');
  await debug.sendCommand('Runtime.addBinding',{name:this.binding});await this.update();
 }
 valid(context){try{return context?.auxData?.isDefault&&context.origin===new URL(this.contents.getURL()).origin&&secureOrigin(context.origin)&&!this.contents.isDestroyed();}catch{return false;}}
 async deliver(context,value){
  if(this.contexts.get(context?.id)!==context||!this.valid(context))return;
  try{await this.contents.debugger.sendCommand('Runtime.evaluate',{expression:'globalThis.Notification?.[Symbol.for('+JSON.stringify(CONTROL)+')]?.receive('+JSON.stringify(value)+')',contextId:context.id});}catch{}
 }
 async handle(event){
  const context=this.contexts.get(event.executionContextId);
  if(!this.valid(context)||typeof event.payload!=='string'||event.payload.length>65536)return;
  let input;try{input=JSON.parse(event.payload);}catch{return;}
  if(!Number.isSafeInteger(input?.id)||input.id<=0||!['show','close','permission'].includes(input.method))return;
  const reply=value=>this.deliver(context,{reply:input.id,...value});
  try{
   if(input.method==='permission'){const state=await this.permission(context.origin);await reply({result:{state}});return;}
   if(this.decisions()[context.origin]!==true)throw Error('Permission required');
   const value=input.value;if(!value||!Number.isSafeInteger(value.notice)||value.notice<=0)throw Error('Invalid notification');
   const key=context.uniqueId+':'+value.notice;
   if(input.method==='close'){this.records.get(key)?.close();await reply({result:true});return;}
   if(typeof value.title!=='string'||value.title.length>256||!value.options||typeof value.options!=='object'||this.records.has(key))throw Error('Invalid notification');
   const now=Date.now();this.recent=this.recent.filter(time=>time>now-60000);if(this.recent.length>=60||this.records.size>=64)throw Error('Notification limit');this.recent.push(now);
   const options=value.options;
   const tag=typeof options.tag==='string'&&options.tag?context.uniqueId+':'+options.tag.slice(0,256):null;
   if(tag)this.tags.get(tag)?.close();
   const notice=new this.NotificationType({title:value.title,body:typeof options.body==='string'?options.body.slice(0,4096):'',silent:options.silent===true,timeoutType:options.requireInteraction?'never':'default'});
   this.records.set(key,notice);
   if(tag)this.tags.set(tag,notice);
   const emit=type=>this.deliver(context,{notice:value.notice,event:type});
   const remove=()=>{this.records.delete(key);if(tag&&this.tags.get(tag)===notice)this.tags.delete(tag);};
   notice.once('show',()=>void emit('show'));
   notice.once('click',()=>{if(this.contexts.get(context.id)!==context||!this.valid(context)||this.decisions()[context.origin]!==true)return;this.activate();void emit('click');notice.close();});
   notice.once('close',()=>{remove();void emit('close');});
   notice.once('failed',()=>{remove();void emit('error');});
   notice.show();await reply({result:true});
  }catch{await reply({error:true});}
 }
 close(){const old=[...this.records.values()];this.records.clear();this.tags.clear();for(const record of old)record.close();}
 update(){
  this.pending=this.pending.catch(()=>{}).then(async()=>{
   if(this.contents.isDestroyed())return;
   const debug=this.contents.debugger;if(this.script)await debug.sendCommand('Page.removeScriptToEvaluateOnNewDocument',{identifier:this.script});
   const source=this.source();this.script=(await debug.sendCommand('Page.addScriptToEvaluateOnNewDocument',{source})).identifier;
   for(const context of this.contexts.values())if(context.auxData?.isDefault){try{await debug.sendCommand('Runtime.evaluate',{expression:source,contextId:context.id});}catch{}}
  });return this.pending;
 }
}
module.exports={PageNotifications,installPageNotifications,secureOrigin};
