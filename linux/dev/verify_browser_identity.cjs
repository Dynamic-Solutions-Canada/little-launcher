'use strict';
// Private fixture only; local diagnostics and optional public, unsigned-in service pages.
const assert = require('node:assert/strict');
const {app} = require('electron');
const fs = require('node:fs');
const http = require('node:http');
assert.equal(process.env.XDG_RUNTIME_DIR, '/tmp/ll-runtime');
app.setPath('userData', fs.mkdtempSync('/work/browser-identity-'));
const {WebLauncher} = require('../browser/window.cjs');
const {Profiles} = require('../browser/profiles.cjs');
let owner, server;
const delay = ms => new Promise(resolve=>setTimeout(resolve,ms));
app.whenReady().then(async()=>{
  const headers=[];
  server=http.createServer((req,res)=>{headers.push(req.headers);res.setHeader('Content-Type','text/html');res.end('<title>Browser identity fixture</title>Browser fixture');});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const services={isQuitting:()=>true,request:async()=>false,emit:()=>{}};
  services.profiles=new Profiles(services.request,()=>null);
  owner=new WebLauncher({Id:'browser-identity',Name:'Browser identity',WebHiddenPolicy:2},
    {window:'#ffffff',text:'#000000',button:'#eeeeee',highlight:'#3daee9',font:'Noto Sans',fontSize:10},null,
    {homeUrl:`http://127.0.0.1:${server.address().port}`,showBookmarkBar:false},services);
  owner.show();
  const contents=owner.view.webContents;
  for(let i=0;i<100&&(!headers.length||contents.isLoading());i++)await delay(50);
  const result={engine:process.versions.chrome,identity:await contents.executeJavaScript(`({ua:navigator.userAgent,brands:navigator.userAgentData?.brands,platform:navigator.platform,secure:window.isSecureContext,crypto:typeof crypto.subtle,customElements:typeof customElements.define,websocket:typeof WebSocket,indexedDB:typeof indexedDB.open,process:typeof process,bridge:typeof window.launcher})`),requestUserAgent:headers[0]?.['user-agent']};
  if(process.argv.includes('--public-sites') || process.argv.includes('--edsby-only')){
    result.publicSites=[];
    const targets=process.argv.includes('--edsby-only') ? [['Edsby',process.env.LL_EDSBY_TEST_URL]] : [['WhatsApp','https://web.whatsapp.com/'],['Home Assistant demo','https://demo.home-assistant.io/']];
    for(const [name,url]of targets){
      if(name==='Edsby' && (!url || !/^https:\/\/[a-zA-Z0-9.-]+\.edsby\.com\/$/.test(url)))throw Error('Expected only a public Edsby HTTPS origin');
      const faults=[];
      const exception=(_event,...args)=>{if(args[0]&&typeof args[0]==='object'){faults.push({level:args[0].level,message:args[0].message?.slice(0,200)});}};
      contents.on('console-message',exception);
      let loaded=true;try{await Promise.race([contents.loadURL(url),delay(15000).then(()=>{throw Error('Timeout');})]);}catch{loaded=false;contents.stop();}
      await delay(6000);
      const state=loaded?await Promise.race([contents.executeJavaScript(`({title:document.title,unsupported:/unsupported browser|browser.*not supported|update.*browser/i.test(document.body?.innerText||''),text:(document.body?.innerText||'').slice(0,1000),root:!!document.querySelector('home-assistant')})`),delay(5000).then(()=>({diagnostic:'Renderer evaluation timed out'}))]).catch(()=>({})):{};
      result.publicSites.push(name==='Edsby'?{name,loaded,unsupported:state.unsupported===true,faultCount:faults.length}: {name,loaded,...state,faults});
      contents.removeListener('console-message',exception);
    }
  }
  console.log(JSON.stringify(result,null,2));
}).catch(error=>{console.error(error.message);process.exitCode=1;}).finally(()=>{
  if(owner){for(const tab of owner.tabs){tab.closing=true;tab.view?.webContents.close({waitForBeforeUnload:false});}owner.window.destroy();}
  server?.close();app.exit(process.exitCode||0);
});
