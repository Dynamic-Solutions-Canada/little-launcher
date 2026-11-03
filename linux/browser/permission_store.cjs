'use strict';
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');

/** Device-local notification decisions only; never persist media/capture grants. */
class PermissionStore {
  constructor(file){
    this.file=file;this.data={};this.unreadable=false;
    try {
      if(fs.lstatSync(file).isSymbolicLink() || fs.statSync(file).size>1024*1024)throw Error('Invalid permission file');
      const data=JSON.parse(fs.readFileSync(file,'utf8'));
      if(data.version!==1 || !data.notifications || typeof data.notifications!=='object' || Array.isArray(data.notifications))throw Error('Invalid permission file');
      for(const [profile,origins] of Object.entries(data.notifications)){
        if(!/^(shared|[a-f0-9]{64})$/.test(profile) || !origins || typeof origins!=='object' || Array.isArray(origins) || Object.keys(origins).length>256)throw Error('Invalid permission file');
        for(const [origin,decision] of Object.entries(origins))if(!validOrigin(origin) || typeof decision!=='boolean')throw Error('Invalid permission file');
      }
      if(Object.keys(data.notifications).length>256)throw Error('Invalid permission file');
      this.data=data.notifications;
    }catch(error){if(error.code!=='ENOENT')this.unreadable=true;}
  }
  entries(profile){return Object.entries(this.data[profile]||{});}
  set(profile,origin,value){
    if(!/^(shared|[a-f0-9]{64})$/.test(profile)||!validOrigin(origin)||typeof value!=='boolean')throw Error('Invalid notification decision');
    const next=structuredClone(this.data);next[profile]={...next[profile],[origin]:value};
    if(Object.keys(next).length>256||Object.keys(next[profile]).length>256)throw Error('Notification decision limit reached');
    this.write(next);
  }
  clear(profile){const next=structuredClone(this.data);delete next[profile];this.write(next);}
  write(next){
    if(this.unreadable)throw Error('Stored notification permissions are unreadable; the original file was preserved.');
    fs.mkdirSync(path.dirname(this.file),{recursive:true,mode:0o700});
    const temporary=this.file+'.'+crypto.randomUUID()+'.tmp';let fd;
    try{
      fd=fs.openSync(temporary,'wx',0o600);fs.writeFileSync(fd,JSON.stringify({version:1,notifications:next})+'\n');fs.fsyncSync(fd);fs.closeSync(fd);fd=null;
      fs.renameSync(temporary,this.file);const directory=fs.openSync(path.dirname(this.file),'r');try{fs.fsyncSync(directory);}finally{fs.closeSync(directory);}
      this.data=next;
    }finally{if(fd!==null&&fd!==undefined)fs.closeSync(fd);try{fs.unlinkSync(temporary);}catch{}}
  }
}
function validOrigin(value){try{const parsed=new URL(value);return ['https:','http:'].includes(parsed.protocol)&&parsed.origin===value;}catch{return false;}}
module.exports={PermissionStore};
