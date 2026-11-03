'use strict';
/** Report the Chromium engine actually shipped here, without Electron/app product tokens. */
function browserUserAgent(version = process.versions.chrome, architecture = process.arch) {
  if (!/^\d+(\.\d+){1,3}$/.test(version || '')) throw new Error('Chromium version is unavailable');
  const cpu = architecture === 'arm64' ? 'aarch64' : 'x86_64';
  return `Mozilla/5.0 (X11; Linux ${cpu}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${version} Safari/537.36`;
}
// Explicit user-requested compatibility experiment: only Google's sign-in host.
function googleSignIn(address) {
  try {const url=new URL(address);return url.protocol==='https:'&&url.hostname==='accounts.google.com'&&!url.username&&!url.password;}
  catch {return false;}
}
const overrides=new WeakMap();
/** CDP changes the active navigation identity without setUserAgent reloading the previous page. */
function applyNavigationIdentity(contents,address) {
  const enabled=googleSignIn(address);
  if(!enabled&&!overrides.has(contents))return Promise.resolve();
  const state=overrides.get(contents)||{enabled:false,pending:Promise.resolve()};overrides.set(contents,state);
  state.pending=state.pending.catch(()=>{}).then(async()=>{
    if(contents.isDestroyed()||(state.enabled===enabled&&(!enabled||contents.debugger.isAttached())))return;
    if(!contents.debugger.isAttached())contents.debugger.attach('1.3');
    await contents.debugger.sendCommand('Emulation.setUserAgentOverride',{userAgent:enabled?'Chrome':''});
    state.enabled=enabled;
  });
  return state.pending;
}
module.exports = {browserUserAgent,googleSignIn,applyNavigationIdentity};
