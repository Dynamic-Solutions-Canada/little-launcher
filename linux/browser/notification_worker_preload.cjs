'use strict';
const {contextBridge,ipcRenderer}=require('electron');
const web=contextBridge.executeInMainWorld({func:()=>['https:','http:'].includes(globalThis.location?.protocol)});
if(web){
 contextBridge.exposeInMainWorld('__littleLauncherNotificationAPI',{
  call:(method,value)=>{const input=JSON.stringify({method,value});if(input.length>65536)return Promise.reject(Error('Notification is too large'));return ipcRenderer.invoke('little-launcher:worker-notification',input);},
  listen:callback=>ipcRenderer.on('little-launcher:worker-notification-event',(_event,value)=>{void Promise.resolve(callback(value)).finally(()=>ipcRenderer.send('little-launcher:worker-notification-done',value.token));}),
 });
 contextBridge.executeInMainWorld({func:()=>{
  const api=globalThis.__littleLauncherNotificationAPI;
  const record=value=>{
   const result=Object.create(Notification.prototype);
   for(const [key,item]of Object.entries(value))Object.defineProperty(result,key,{value:item,enumerable:true});
   Object.defineProperty(result,'close',{value:()=>{void api.call('close',{id:value.id});}});return result;
  };
  const call=async(method,value)=>{
   try{const result=await api.call(method,value);return method==='list'?result.map(record):result;}
   catch{throw new DOMException('This origin is not allowed to deliver this notification.','NotAllowedError');}
  };
  // Native worker activation is absent for compatibility-dispatched events. Limit
  // the standard client operations to a real, live notification-click task.
  const clicks=new Set(),nativeOpen=clients.openWindow.bind(clients);
  const nativeFocus=WindowClient.prototype.focus;
  clients.openWindow=async function(address){
   const token=[...clicks].at(-1);if(!token)return nativeOpen(address);
   const url=new URL(String(address),location.href).href;
   await call('open',{token,url});
   if(new URL(url).origin!==location.origin)return null;
   for(let attempt=0;attempt<40;attempt++){
    const all=await clients.matchAll({type:'window',includeUncontrolled:true});const client=all.find(value=>value.url===url);if(client)return client;
    await new Promise(resolve=>setTimeout(resolve,50));
   }
   return null;
  };
  WindowClient.prototype.focus=async function(){
   const token=[...clicks].at(-1);if(!token)return Reflect.apply(nativeFocus,this,[]);
   await call('focus',{token,url:this.url});return this;
  };
  Object.defineProperty(ServiceWorkerRegistration.prototype,'showNotification',{configurable:true,writable:true,value:function(title,options={}){return call('show',{title:String(title),options});}});
  Object.defineProperty(ServiceWorkerRegistration.prototype,'getNotifications',{configurable:true,writable:true,value:function(options={}){return call('list',{tag:options.tag});}});
  self.addEventListener('message',event=>{
   if(event.data?.type!=='little-launcher-notification-v1'||!event.ports?.[0]||event.origin!==location.origin)return;
   event.stopImmediatePropagation();const port=event.ports[0];
   void call(event.data.method,event.data.value).then(result=>port.postMessage({result:event.data.method==='list'?result.map(value=>({...value})):result}),()=>port.postMessage({error:true}));
  });
  api.listen(async value=>{
   if(value.type==='notificationclick')clicks.add(value.token);
   const pending=[];const event=new Event(value.type);
   if(typeof NotificationEvent!=='undefined')Object.setPrototypeOf(event,NotificationEvent.prototype);
   Object.defineProperties(event,{notification:{value:record(value.notification)},action:{value:value.action||''},waitUntil:{value:promise=>pending.push(Promise.resolve(promise))}});
   try{self.dispatchEvent(event);await Promise.allSettled(pending);}finally{clicks.delete(value.token);}
  });
 }});
}
