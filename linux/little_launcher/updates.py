"""AppImage-only release discovery, verified staging and atomic replacement; no Qt or credentials."""
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

from . import i18n

REPOSITORY='Dynamic-Solutions-Canada/little-launcher'
LATEST='https://api.github.com/repos/'+REPOSITORY+'/releases/latest'
MAX_IMAGE=1024*1024*1024
MAX_METADATA=2*1024*1024

class UpdateError(Exception):pass

@dataclass(frozen=True)
class Release:
    version: str
    name: str
    url: str
    digest: str
    size: int


def version_tuple(value):
    if not isinstance(value,str) or len(value)>40 or not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?',value):raise UpdateError(i18n.t('linuxUpdates.error.unsupportedVersion'))
    parts=tuple(int(part) for part in value.split('.'))
    if any(part>999999 for part in parts):raise UpdateError(i18n.t('linuxUpdates.error.invalidVersion'))
    return parts+(0,)*(4-len(parts))


def release_from_metadata(data,current,architecture='x64'):
    if architecture not in ('x64','arm64'):raise UpdateError(i18n.t('linuxUpdates.error.unsupportedArchitecture'))
    if not isinstance(data,dict) or data.get('draft') or data.get('prerelease'):return None
    tag=data.get('tag_name','')
    if not isinstance(tag,str) or not tag.startswith('v'):raise UpdateError(i18n.t('linuxUpdates.error.invalidTag'))
    version=tag[1:]
    if version_tuple(version)<=version_tuple(current):return None
    name=f'LittleLauncher-{version}-linux-{architecture}.AppImage'
    assets=data.get('assets',[])
    if not isinstance(assets,list):raise UpdateError(i18n.t('linuxUpdates.error.invalidAssets'))
    matches=[asset for asset in assets if isinstance(asset,dict) and asset.get('name')==name]
    if not matches:return None
    if len(matches)!=1:raise UpdateError(i18n.t('linuxUpdates.error.ambiguousAssets'))
    asset=matches[0];url=f'https://github.com/{REPOSITORY}/releases/download/{tag}/{name}'
    digest=asset.get('digest','');size=asset.get('size')
    if asset.get('browser_download_url')!=url or not isinstance(digest,str) or not re.fullmatch(r'sha256:[0-9a-fA-F]{64}',digest) or type(size) is not int or not 0<size<=MAX_IMAGE:
        raise UpdateError(i18n.t('linuxUpdates.error.missingVerification'))
    return Release(version,name,url,digest[7:].lower(),size)


class ReleaseRedirect(HTTPRedirectHandler):
    def redirect_request(self,request,fp,code,msg,headers,newurl):
        parts=urlsplit(newurl)
        if parts.scheme!='https' or parts.username is not None or parts.password is not None or parts.hostname not in ('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') or parts.port not in (None,443):
            raise UpdateError(i18n.t('linuxUpdates.error.unsupportedRedirect'))
        return super().redirect_request(request,fp,code,msg,headers,newurl)


def network_request(url):
    return build_opener(ReleaseRedirect()).open(Request(url,headers={'User-Agent':'LittleLauncher-Linux-Updater','Accept':'application/vnd.github+json' if url==LATEST else 'application/octet-stream'}),timeout=30)


def check_release(current,architecture='x64',opener=network_request,cancelled=lambda:False):
    try:
        blocks=[];size=0;deadline=time.monotonic()+30
        with opener(LATEST) as response:
            read=getattr(response,'read1',response.read)
            while True:
                if cancelled():raise UpdateError(i18n.t('linuxUpdates.error.checkCanceled'))
                if time.monotonic()>deadline:raise UpdateError(i18n.t('linuxUpdates.error.checkTimedOut'))
                block=read(min(65536,MAX_METADATA+1-size))
                if not block:break
                blocks.append(block);size+=len(block)
                if size>MAX_METADATA:break
        raw=b''.join(blocks)
        if len(raw)>MAX_METADATA:raise UpdateError(i18n.t('linuxUpdates.error.responseTooLarge'))
        return release_from_metadata(json.loads(raw),current,architecture)
    except UpdateError:raise
    except HTTPError as error:
        if error.code==404:return None
        raise UpdateError(i18n.t('linuxUpdates.error.serverFailed')) from None
    except (OSError,ValueError,URLError):raise UpdateError(i18n.t('linuxUpdates.error.unreachable')) from None


def valid_image_header(raw,architecture='x64'):
    machine={'x64':62,'arm64':183}.get(architecture)
    return len(raw)>=20 and raw[:4]==b'\x7fELF' and raw[4]==2 and raw[5]==1 and raw[8:11]==b'AI\x02' and int.from_bytes(raw[18:20],'little')==machine


def image_identity(path):
    path=Path(path)
    try:
        information=path.lstat()
        if not stat.S_ISREG(information.st_mode) or information.st_uid!=os.getuid() or not information.st_mode&stat.S_IWUSR or not os.access(path.parent,os.W_OK):raise UpdateError(i18n.t('linuxUpdates.error.notWritable'))
        with path.open('rb') as source:header=source.read(20)
        if not valid_image_header(header,'arm64' if os.uname().machine in ('aarch64','arm64') else 'x64'):raise UpdateError(i18n.t('linuxUpdates.error.notAppImage'))
        return (information.st_dev,information.st_ino,information.st_size,information.st_mtime_ns)
    except OSError:raise UpdateError(i18n.t('linuxUpdates.error.imageUnavailable')) from None


def current_appimage():
    value=os.environ.get('APPIMAGE')
    if not value:return None
    path=Path(value)
    if not path.is_absolute():return None
    try:image_identity(path)
    except UpdateError:return None
    return path


def hash_file(path):
    digest=hashlib.sha256()
    with Path(path).open('rb') as source:
        for block in iter(lambda:source.read(1024*1024),b''):digest.update(block)
    return digest.hexdigest()


def sync_directory(path):
    descriptor=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
    try:os.fsync(descriptor)
    finally:os.close(descriptor)

@dataclass
class StagedUpdate:
    destination: Path
    staged: Path
    release: Release
    original_identity: tuple
    architecture: str

    def discard(self):
        self.staged.unlink(missing_ok=True)

    def install(self):
        """Called only after guarded normal quit, with the single-instance lock released."""
        if image_identity(self.destination)!=self.original_identity:raise UpdateError(i18n.t('linuxUpdates.error.imageChanged'))
        if self.staged.parent!=self.destination.parent or self.staged.is_symlink() or not self.staged.is_file() or self.staged.stat().st_uid!=os.getuid():raise UpdateError(i18n.t('linuxUpdates.error.stagedMissing'))
        if self.staged.stat().st_size!=self.release.size or hash_file(self.staged)!=self.release.digest:raise UpdateError(i18n.t('linuxUpdates.error.stagedVerification'))
        with self.staged.open('rb') as source:
            if not valid_image_header(source.read(20),self.architecture):raise UpdateError(i18n.t('linuxUpdates.error.stagedIncompatible'))
        backup=self.destination.with_name('.'+self.destination.name+'.previous')
        if backup.exists() or backup.is_symlink():
            if backup.is_symlink() or not backup.is_file() or backup.stat().st_uid!=os.getuid():raise UpdateError(i18n.t('linuxUpdates.error.backupUnavailable'))
            image_identity(backup)
            backup.unlink()
        os.link(self.destination,backup,follow_symlinks=False)
        sync_directory(self.destination.parent)
        try:
            os.replace(self.staged,self.destination)
            sync_directory(self.destination.parent)
        except OSError:
            try:
                if not self.staged.exists():os.replace(backup,self.destination)
                sync_directory(self.destination.parent)
            except OSError:
                raise UpdateError(i18n.t('linuxUpdates.error.checkBackup')) from None
            raise UpdateError(i18n.t('linuxUpdates.error.restored')) from None
        return backup


def download_release(release,destination,*,architecture='x64',opener=network_request,cancelled=lambda:False,progress=lambda done,total:None):
    destination=Path(destination);identity=image_identity(destination)
    descriptor,name=tempfile.mkstemp(prefix='.'+destination.name+'.download-',dir=destination.parent)
    staged=Path(name)
    try:
        digest=hashlib.sha256();size=0;header=b'';deadline=time.monotonic()+3600
        with os.fdopen(descriptor,'wb') as output,opener(release.url) as response:
            while True:
                if cancelled():raise UpdateError(i18n.t('linuxUpdates.error.downloadCanceled'))
                if time.monotonic()>deadline:raise UpdateError(i18n.t('linuxUpdates.error.downloadTimedOut'))
                block=getattr(response,'read1',response.read)(1024*1024)
                if not block:break
                size+=len(block)
                if size>release.size or size>MAX_IMAGE:raise UpdateError(i18n.t('linuxUpdates.error.downloadTooLarge'))
                if len(header)<20:header=(header+block)[:20]
                digest.update(block);output.write(block);progress(size,release.size)
            if size!=release.size or digest.hexdigest()!=release.digest or not valid_image_header(header,architecture):raise UpdateError(i18n.t('linuxUpdates.error.downloadVerification'))
            output.flush();os.fsync(output.fileno())
        os.chmod(staged,0o755);sync_directory(staged.parent)
        return StagedUpdate(destination,staged,release,identity,architecture)
    except BaseException:
        staged.unlink(missing_ok=True)
        raise
