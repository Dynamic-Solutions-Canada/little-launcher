'use strict';
/** Standard web APIs only: remote pages receive neither Node nor application IPC. */
function installNotificationFrame(){
 if(!globalThis.ServiceWorkerRegistration||ServiceWorkerRegistration.prototype.__littleLauncherNotifications)return;
 const transfer=(registration,method,value)=>new Promise((resolve,reject)=>{
  const worker=registration.active;
  if(!worker){reject(new DOMException('No active service worker.','InvalidStateError'));return;}
  const channel=new MessageChannel();const timer=setTimeout(()=>{channel.port1.close();reject(new DOMException('Notification delivery did not respond.','NotSupportedError'));},6000);
  channel.port1.onmessage=event=>{clearTimeout(timer);channel.port1.close();event.data?.error?reject(new DOMException('Notification permission is required.','NotAllowedError')):resolve(event.data.result);};
  worker.postMessage({type:'little-launcher-notification-v1',method,value},[channel.port2]);
 });
 Object.defineProperty(ServiceWorkerRegistration.prototype,'__littleLauncherNotifications',{value:true});
 Object.defineProperty(ServiceWorkerRegistration.prototype,'showNotification',{configurable:true,writable:true,value:function(title,options={}){return transfer(this,'show',{title:String(title),options});}});
 Object.defineProperty(ServiceWorkerRegistration.prototype,'getNotifications',{configurable:true,writable:true,value:async function(options={}){
  const registration=this;
  return (await transfer(this,'list',{tag:options.tag})).map(value=>{
   const result=Object.create(Notification.prototype);for(const[key,item]of Object.entries(value))Object.defineProperty(result,key,{value:item,enumerable:true});
   Object.defineProperty(result,'close',{value:()=>{void transfer(registration,'close',{id:value.id});}});return result;
  });
 }});
}
module.exports={installNotificationFrame};
