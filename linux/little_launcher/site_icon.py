"""Explicit public favicon preview with no browser state and bounded raster decoding."""
from urllib.parse import urlsplit
from PySide6.QtCore import QByteArray, QBuffer, QIODevice, QObject, QTimer, QUrl, Qt, Signal, Slot
from PySide6.QtGui import QImageReader
from PySide6.QtNetwork import QNetworkAccessManager, QNetworkReply, QNetworkRequest
from .document import DocumentError

MAX_BYTES=256*1024


def favicon_url(address):
    if not isinstance(address,str) or len(address)>8192:
        raise DocumentError('Choose an HTTP or HTTPS website address first.')
    try:
        parts=urlsplit(address)
        if parts.scheme not in ('http','https') or not parts.hostname or parts.username is not None or parts.password is not None or any(character.isspace() or ord(character)<32 for character in parts.hostname):raise ValueError()
        result=QUrl();result.setScheme(parts.scheme);result.setHost(parts.hostname)
        if parts.port is not None:result.setPort(parts.port)
        result.setPath('/favicon.ico')
        if not result.isValid():raise ValueError()
        return result
    except ValueError:
        raise DocumentError('Website icon preview needs an HTTP or HTTPS address without embedded credentials.') from None


def normalized_png(raw):
    if not isinstance(raw,bytes) or not raw or len(raw)>MAX_BYTES:return None
    source=QBuffer();source.setData(QByteArray(raw));source.open(QIODevice.ReadOnly)
    reader=QImageReader(source)
    if bytes(reader.format()).lower() not in (b'png',b'ico',b'jpeg',b'webp'):return None
    size=reader.size()
    if not size.isValid() or size.width()>1024 or size.height()>1024:return None
    image=reader.read()
    if image.isNull():return None
    image=image.scaled(64,64,Qt.KeepAspectRatio,Qt.SmoothTransformation)
    output=QBuffer();output.open(QIODevice.WriteOnly)
    return bytes(output.data()) if image.save(output,'PNG') else None


class SiteIconPreview(QObject):
    ready=Signal(bytes)
    failed=Signal(str)

    def __init__(self,parent=None):
        super().__init__(parent)
        self.manager=QNetworkAccessManager(self)
        self.manager.authenticationRequired.connect(lambda reply,auth:reply.abort())
        self.reply=None;self.raw=bytearray();self.generation=0
        self.timer=QTimer(self);self.timer.setSingleShot(True);self.timer.timeout.connect(self.timeout)

    def fetch(self,address):
        self.cancel();self.generation+=1
        try:url=favicon_url(address)
        except DocumentError as error:self.failed.emit(str(error));return
        request=QNetworkRequest(url)
        request.setAttribute(QNetworkRequest.RedirectPolicyAttribute,QNetworkRequest.ManualRedirectPolicy)
        for attribute in (QNetworkRequest.CookieLoadControlAttribute,QNetworkRequest.CookieSaveControlAttribute,QNetworkRequest.AuthenticationReuseAttribute):
            request.setAttribute(attribute,QNetworkRequest.Manual)
        request.setRawHeader(b'Accept',b'image/png,image/x-icon,image/vnd.microsoft.icon,image/jpeg,image/webp')
        reply=self.manager.get(request);self.reply=reply;self.raw=bytearray()
        reply.readyRead.connect(self.read_ready)
        reply.finished.connect(self.finished_reply)
        self.timer.start(5000)

    @Slot()
    def read_ready(self):
        reply=self.sender()
        if reply is self.reply:self.read(reply)

    def read(self,reply):
        if self.reply is not reply:return
        if len(self.raw)+reply.bytesAvailable()>MAX_BYTES:
            self.cancel();self.failed.emit('The website icon is too large. Your existing icon is unchanged.');return
        self.raw.extend(bytes(reply.readAll()))

    @Slot()
    def finished_reply(self):
        reply=self.sender()
        try:
            if self.reply is not reply:return
            self.read(reply)
            if self.reply is not reply:return
            self.timer.stop();self.reply=None
            content=str(reply.header(QNetworkRequest.ContentTypeHeader) or '').split(';',1)[0].strip().lower()
            valid=reply.error()==QNetworkReply.NoError and reply.attribute(QNetworkRequest.HttpStatusCodeAttribute)==200 and content in ('image/png','image/x-icon','image/vnd.microsoft.icon','image/jpeg','image/webp')
            image=normalized_png(bytes(self.raw)) if valid else None
            self.raw=bytearray()
            if image:self.ready.emit(image)
            else:self.failed.emit('No supported public website icon was found. Your existing icon is unchanged; you can browse for an image instead.')
        finally:
            reply.deleteLater()

    def timeout(self):
        if self.reply:
            self.cancel();self.failed.emit('Website icon preview timed out. Your existing icon is unchanged.')

    def cancel(self):
        self.generation+=1;self.timer.stop();reply=self.reply;self.reply=None;self.raw=bytearray()
        if reply:
            reply.abort()
