'use strict';
const {Notification,webContents}=require('electron');
const path=require('node:path'),crypto=require('node:crypto');
const CHANNEL='little-launcher:worker-notification';

async function notificationClientAction(tasks,worker,origin,method,value){
 const pending=tasks.get(value?.token);
 if(!['focus','open'].includes(method)||pending?.worker!==worker||pending.type!=='notificationclick'||pending.origin!==origin||!pending.candidate?.notificationClient)throw Error('A current notification click is required.');
 let url;try{url=new URL(value.url);if((method==='focus'&&url.origin!==origin)||!['http:','https:'].includes(url.protocol)||url.username||url.password||url.href.length>8192)throw Error();}catch{throw Error('Invalid notification client.');}
 if(!await pending.candidate.notificationClient(method,url.href))throw Error('The launcher is no longer available.');
 return true;
}

/** Implement only the granted origin's Web Notifications API, authenticated by native worker identity. */
function installWorkerNotifications(profile,grants,ownerFor,onError=()=>{}){
 const records=new Map(),tasks=new Map(),workers=new Map(),registered=new WeakSet(),recent=[];
 profile.registerPreloadScript({type:'service-worker',filePath:path.join(__dirname,'notification_worker_preload.cjs')});
 function originOf(worker){try{const scope=new URL(worker.scope),script=new URL(worker.scriptURL);return ['http:','https:'].includes(scope.protocol)&&scope.origin===script.origin?scope.origin:null;}catch{return null;}}
 function owner(origin){return webContents.getAllWebContents().filter(contents=>!contents.isDestroyed()&&contents.session===profile).map(contents=>({contents,owner:ownerFor(contents)})).find(value=>{try{return value.owner&&new URL(value.contents.getURL()).origin===origin;}catch{return false;}})?.owner;}
 async function dispatch(record,type,action=''){
  if(grants.get(record.origin)!==true)return;
  const candidate=owner(record.origin);if(type==='notificationclick'&&candidate&&!candidate.window.isDestroyed()){if(candidate.focusNotification)candidate.focusNotification();else candidate.show();}
  try{
   let worker=workers.get(record.scope);
   if(!worker||worker.isDestroyed()){const started=await profile.serviceWorkers.startWorkerForScope(record.scope);worker=workers.get(record.scope)||started;}
   if(!worker||worker.isDestroyed())return;
   const token=crypto.randomUUID(),task=worker.startTask();const timer=setTimeout(()=>{tasks.delete(token);try{task.end();}catch{}},10000);timer.unref();
   tasks.set(token,{worker,task,timer,type,origin:record.origin,candidate});worker.send(CHANNEL+'-event',{type,action,notification:record.data,token});
  }catch{onError('The notification action could not reach its website. Reopen the launcher to continue.');}
 }
 profile.serviceWorkers.on('running-status-changed',details=>{
  if(details.runningStatus==='stopped'){for(const [scope,worker]of workers)if(worker.versionId===details.versionId)workers.delete(scope);for(const [token,pending]of tasks)if(pending.worker.versionId===details.versionId){tasks.delete(token);clearTimeout(pending.timer);try{pending.task.end();}catch{}}return;}
  if(details.runningStatus!=='starting')return;
  const worker=profile.serviceWorkers.getWorkerFromVersionID(details.versionId);
  if(!worker||registered.has(worker)||!originOf(worker))return;registered.add(worker);workers.set(worker.scope,worker);
  worker.ipc.handle(CHANNEL,async(event,input)=>{
   const origin=originOf(worker);
   if(worker.isDestroyed()||event.serviceWorker!==worker||event.session!==profile||!origin||grants.get(origin)!==true||!owner(origin))throw Error('Notification permission is required.');
   if(typeof input!=='string'||input.length>65536)throw Error('Invalid notification request');
   input=JSON.parse(input);
   if(!input||!['show','list','close','focus','open'].includes(input.method))throw Error('Unknown notification method.');
   const value=input.value;
   if(input.method==='focus'||input.method==='open'){
    return notificationClientAction(tasks,worker,origin,input.method,value);
   }
   if(input.method==='list')return [...records.values()].filter(record=>record.scope===worker.scope&&(typeof value?.tag!=='string'||record.data.tag===value.tag)).map(record=>record.data);
   if(input.method==='close'){const record=records.get(value?.id);if(record?.scope===worker.scope){records.delete(record.data.id);record.native.close();}return;}
   if(!value||typeof value.title!=='string'||value.title.length>256||!value.options||typeof value.options!=='object'||Buffer.byteLength(JSON.stringify(value))>65536)throw Error('Invalid notification.');
   const now=Date.now();while(recent.length&&recent[0]<now-60000)recent.shift();if(recent.length>=60||records.size>=64)throw Error('Notification limit reached.');recent.push(now);
   const options=value.options,body=typeof options.body==='string'?options.body.slice(0,4096):'',tag=typeof options.tag==='string'?options.tag.slice(0,256):'';
   const actions=(Array.isArray(options.actions)?options.actions:[]).slice(0,2).filter(item=>item&&typeof item.action==='string'&&typeof item.title==='string').map(item=>({action:item.action.slice(0,128),title:item.title.slice(0,128)}));
   for(const previous of records.values())if(tag&&previous.scope===worker.scope&&previous.data.tag===tag){records.delete(previous.data.id);previous.native.close();}
   const notification=new Notification({title:value.title,body,silent:options.silent===true,timeoutType:options.requireInteraction?'never':'default',actions:actions.map(item=>({type:'button',text:item.title}))});
   const id=crypto.randomUUID();const data={id,title:value.title,body,tag,actions,data:options.data??null,dir:options.dir||'auto',lang:options.lang||'',icon:'',badge:'',image:'',silent:options.silent===true,renotify:options.renotify===true,requireInteraction:options.requireInteraction===true,timestamp:typeof options.timestamp==='number'?options.timestamp:now};
   const record={native:notification,scope:worker.scope,origin,data};records.set(id,record);
   notification.on('click',()=>{if(!records.delete(id))return;void dispatch(record,'notificationclick');notification.close();});
   notification.on('action',(_event,index)=>{if(actions[index]&&records.delete(id)){void dispatch(record,'notificationclick',actions[index].action);notification.close();}});
   notification.on('close',()=>{if(records.delete(id))void dispatch(record,'notificationclose');});
   await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{records.delete(id);reject(Error('Notification backend did not respond.'));},5000);timer.unref();
    notification.once('show',()=>{clearTimeout(timer);resolve();});notification.once('failed',()=>{clearTimeout(timer);records.delete(id);onError('KDE could not display a website notification. Check desktop notification settings.');reject(Error('Notification backend failed.'));});notification.show();
   });
  });
  worker.ipc.on(CHANNEL+'-done',(event,token)=>{const pending=tasks.get(token);if(pending?.worker!==worker||event.session!==profile||event.serviceWorker!==worker)return;tasks.delete(token);clearTimeout(pending.timer);try{pending.task.end();}catch{}});
 });
 return {diagnostics(){return {active:records.size,pending:tasks.size,workers:workers.size};},reset(){const old=[...records.values()];records.clear();for(const record of old)record.native.close();for(const pending of tasks.values()){clearTimeout(pending.timer);try{pending.task.end();}catch{}}tasks.clear();}};
}
module.exports={installWorkerNotifications,notificationClientAction};
