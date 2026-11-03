"""Bounded, credential-free selfh.st catalog and raster browsing cache."""
import json,re,time
from pathlib import Path
from PySide6.QtCore import QObject,QTimer,QUrl,Signal,Slot,QBuffer,QIODevice,QByteArray
from PySide6.QtGui import QImageReader
from PySide6.QtNetwork import QNetworkAccessManager,QNetworkRequest,QNetworkReply
from .storage import atomic_write
INDEX='https://raw.githubusercontent.com/selfhst/icons/refs/heads/main/index.json'
PNG='https://cdn.jsdelivr.net/gh/selfhst/icons@main/png/'
TTL=6*3600

def catalog(raw):
    if len(raw)>3*1024*1024:raise ValueError('Catalog too large')
    values=json.loads(raw)
    if not isinstance(values,list)or len(values)>10000:raise ValueError('Invalid catalog')
    result=[]
    for value in values:
        if not isinstance(value,dict):continue
        ref=value.get('Reference','');name=value.get('Name','')
        if not isinstance(ref,str)or not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}',ref)or '..'in ref or not isinstance(name,str)or not name.strip()or len(name)>256:continue
        result.append({key:str(value.get(key,'') or '')[:512]for key in ('Name','Reference','Tags','Category')})
    if not result:raise ValueError('Empty catalog')
    return sorted(result,key=lambda value:value['Name'].casefold())

def raster(raw):
    if not raw or len(raw)>256*1024:return None
    source=QBuffer();source.setData(QByteArray(raw));source.open(QIODevice.ReadOnly);reader=QImageReader(source)
    size=reader.size()
    if bytes(reader.format()).lower()!=b'png'or not size.isValid()or size.width()>1024 or size.height()>1024 or reader.read().isNull():return None
    return raw

class SelfhStCatalog(QObject):
    loaded=Signal(object)
    icon_ready=Signal(str,bytes)
    failed=Signal(str)
    icon_failed=Signal(str)
    def __init__(self,directory,parent=None):
        super().__init__(parent);self.directory=Path(directory);self.manager=QNetworkAccessManager(self)
        self.manager.authenticationRequired.connect(lambda reply,auth:reply.abort())
        self.pending={};self.queue=[];self.active=0;self.closed=False;self.images={};self.cached=None
    def load(self,force=False):
        if self.cached is not None and not force:self.loaded.emit(self.cached);return
        path=self.directory/'index.json'
        try:
            if path.stat().st_size<=3*1024*1024:self.cached=catalog(path.read_bytes())
            if self.cached is not None and not force and time.time()-path.stat().st_mtime<TTL:self.loaded.emit(self.cached);return
        except (OSError,ValueError):pass
        if not any(item['key']=='catalog'for item in self.pending.values()):self.fetch('catalog',INDEX,3*1024*1024)
    def icon(self,reference,priority=False):
        if not isinstance(reference,str)or not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}',reference)or '..'in reference:return None
        if priority and reference in self.queue:self.queue.remove(reference);self.queue.insert(0,reference)
        if reference in self.images:return self.images[reference]
        path=self.directory/(reference+'.png')
        try:
            if path.stat().st_size<=256*1024:
                raw=raster(path.read_bytes())
                if raw:self.images[reference]=raw;return raw
        except OSError:pass
        if reference not in self.queue and not any(item['key']==reference for item in self.pending.values()):self.queue.append(reference);self.pump()
        return None
    def prioritize(self,references):
        self.queue=[ref for ref in self.queue if ref in references]
    def pump(self):
        while not self.closed and self.active<4 and self.queue:
            reference=self.queue.pop(0);self.active+=1;self.fetch(reference,PNG+reference+'.png',256*1024)
    def fetch(self,key,url,limit):
        if self.closed:return
        request=QNetworkRequest(QUrl(url));request.setAttribute(QNetworkRequest.RedirectPolicyAttribute,QNetworkRequest.ManualRedirectPolicy)
        for attribute in (QNetworkRequest.CookieLoadControlAttribute,QNetworkRequest.CookieSaveControlAttribute,QNetworkRequest.AuthenticationReuseAttribute):request.setAttribute(attribute,QNetworkRequest.Manual)
        reply=self.manager.get(request);timer=QTimer(reply);timer.setSingleShot(True);timer.timeout.connect(reply.abort);timer.start(10000)
        self.pending[reply]={'key':key,'raw':bytearray(),'limit':limit,'timer':timer}
        reply.readyRead.connect(self.read);reply.finished.connect(self.finish)
    @Slot()
    def read(self):
        reply=self.sender();item=self.pending.get(reply)
        if item:
            if len(item['raw'])+reply.bytesAvailable()>item['limit']:reply.abort();return
            item['raw'].extend(bytes(reply.readAll()))
    @Slot()
    def finish(self):
        reply=self.sender();item=self.pending.pop(reply,None)
        try:
            if item is None:return
            self.read_reply(reply,item);item['timer'].stop()
            key=item['key'];raw=bytes(item['raw']);ok=reply.error()==QNetworkReply.NoError and reply.attribute(QNetworkRequest.HttpStatusCodeAttribute)==200
            if key=='catalog':
                try:
                    if not ok:raise ValueError()
                    values=catalog(raw);self.cached=values
                    try:atomic_write(self.directory/'index.json',raw)
                    except OSError:pass
                    self.loaded.emit(values)
                except (ValueError,UnicodeError):
                    if self.cached is not None:self.loaded.emit(self.cached)
                    else:self.failed.emit('The selfh.st catalog is unavailable. Retry or choose another icon source.')
            else:
                self.active-=1;image=raster(raw)if ok else None
                if image:
                    if len(self.images)>=128:self.images.pop(next(iter(self.images)))
                    self.images[key]=image
                    try:atomic_write(self.directory/(key+'.png'),image)
                    except OSError:pass
                    self.icon_ready.emit(key,image)
                else:self.icon_failed.emit(key)
                self.pump()
        finally:reply.deleteLater()
    def read_reply(self,reply,item):
        if len(item['raw'])+reply.bytesAvailable()<=item['limit']:item['raw'].extend(bytes(reply.readAll()))
        else:reply.abort()
    def close(self):
        self.closed=True;self.queue.clear()
        for reply in list(self.pending):reply.abort()
