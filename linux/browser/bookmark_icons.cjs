'use strict';
const {nativeImage} = require('electron');
const {randomUUID}=require('node:crypto');
const {validUrl} = require('./urls.cjs');

/** Local, bounded, profile-scoped presentation cache. Never follow synced file paths or use a favicon service. */
class BookmarkIcons {
  constructor(profile, changed, decode = null) {
    this.profile = profile;
    this.decode=decode;
    this.changed = changed;
    this.entries = new Map();
    this.aliases = new Map();
    this.queue = [];
    this.active = 0;
  }

  peek(address) {
    return validUrl(address) ? this.entries.get(this.origin(address))?.image || null : null;
  }

  get(address) {
    if (!validUrl(address)) return null;
    const origin = this.origin(address);
    if (!this.entries.has(origin) && this.entries.size < 128) this.schedule(origin, origin + '/favicon.ico');
    return this.entries.get(origin)?.image || null;
  }

  origin(address) {
    let origin=new URL(address).origin;const visited=new Set();
    while(this.aliases.has(origin)&&!visited.has(origin)){visited.add(origin);origin=this.aliases.get(origin);}
    return origin;
  }

  observe(address, icons, redirects=[]) {
    if (!validUrl(address)) return;
    const final=new URL(address),origin=final.origin;
    // Only a real browser redirect can link www/non-www or HTTP/HTTPS forms.
    // Do not adopt a third-party login page's icon for the original bookmark.
    this.aliases.delete(origin);
    for(const address of redirects.slice(0,16)){
      if(!validUrl(address))continue;
      const source=new URL(address);
      if(source.origin===origin||source.username||source.password||source.hostname.replace(/^www\./,'')!==final.hostname.replace(/^www\./,'')||(source.protocol==='https:'&&final.protocol!=='https:'))continue;
      if(this.aliases.has(source.origin)||this.aliases.size<128){this.aliases.set(source.origin,origin);this.entries.delete(source.origin);}
    }
    const candidates = icons.filter(icon => {
      if(!validUrl(icon)||icon.length>8192)return false;
      const parsed=new URL(icon);
      return !parsed.username&&!parsed.password&&!(address.startsWith('https:')&&parsed.protocol!=='https:');
    }).slice(0,3);
    const previous = this.entries.get(origin);
    if (candidates.length && !(previous?.pending && JSON.stringify(previous.candidates)===JSON.stringify(candidates)) && (previous || this.entries.size < 128)) this.schedule(origin, candidates);
  }

  accept(token,png) {
    const entry=[...this.entries.values()].find(item=>item.token===token);
    if(!entry||typeof png!=='string'||png.length>100000)return;
    const image=nativeImage.createFromBuffer(Buffer.from(png,'base64')),bounds=image.getSize();
    if(image.isEmpty()||bounds.width>128||bounds.height>128)return;
    entry.image=image.resize({width:32,height:32}).toDataURL();this.changed();
  }

  schedule(origin, urls) {
    const candidates=Array.isArray(urls)?urls:[urls];
    const entry = {url:candidates[0],candidates, token:randomUUID(), pending:true, image: this.entries.get(origin)?.image || null};
    this.entries.set(origin, entry);
    // Only one queued request per site; visited-page metadata may replace a fallback.
    this.queue = this.queue.filter(item => item.origin !== origin);
    this.queue.push({origin, entry});
    this.pump();
  }

  pump() {
    while (this.active < 2 && this.queue.length) {
      const {origin, entry} = this.queue.shift();
      this.active++;
      void (async()=>{
        for(const url of entry.candidates){
          try{const image=await this.load(url,32,entry.token);if(image)return image;}catch{}
          if(this.entries.get(origin)!==entry)break;
        }
        return null;
      })().then(image => {
        if (this.entries.get(origin) !== entry) return;
        if (image) {entry.image = image;this.changed();}
      }).catch(() => {}).finally(() => {entry.pending=false;this.active--;this.pump();});
    }
  }

  async loadBytes(url) {
    const response = await this.profile.fetch(url, {credentials: 'omit', redirect: 'error', cache:'no-cache', signal: AbortSignal.timeout(5000)});
    if (!response.ok || !/image\/(?:png|x-icon|vnd\.microsoft\.icon|jpeg|webp)(?:;|$)/i.test(response.headers.get('content-type') || '')) {
      await response.body?.cancel(); return null;
    }
    const reader = response.body.getReader();
    const chunks = []; let size = 0;
    try {
      while (true) {
        const {value, done} = await reader.read();
        if (done) break;
        size += value.length;
        if (size > 256 * 1024) return null;
        chunks.push(Buffer.from(value));
      }
    } finally {await reader.cancel();}
    return Buffer.concat(chunks);
  }

  async load(url, pixels = 32, token = null) {
    const bytes = await this.loadBytes(url);
    if (!bytes) return null;
    const image = nativeImage.createFromBuffer(bytes);
    const bounds = image.getSize();
    if (image.isEmpty()) {if(token&&this.decode)this.decode(bytes,token);return null;}
    if (bounds.width > 1024 || bounds.height > 1024) return null;
    return image.resize({width: pixels, height: pixels}).toDataURL();
  }
}
module.exports = {BookmarkIcons};
