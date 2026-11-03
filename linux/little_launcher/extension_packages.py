"""Managed extension packages: bounded local ZIP/folder import and private, traversal-safe extraction."""
import hashlib
import io
import json
import os
from pathlib import Path,PurePosixPath
import shutil
import stat
import tempfile
import zipfile
from .document import DocumentError

MAX_PACKAGE=64*1024*1024
MAX_EXPANDED=256*1024*1024

def manifest(directory):
    file=Path(directory)/'manifest.json'
    if not file.is_file() or file.stat().st_size>1024*1024:raise DocumentError('Choose an extension folder containing manifest.json.')
    try:data=json.loads(file.read_text())
    except (ValueError,UnicodeError):raise DocumentError('The extension manifest is unreadable.') from None
    if not isinstance(data,dict) or data.get('manifest_version') not in (2,3) or not isinstance(data.get('name'),str) or not isinstance(data.get('version'),str):raise DocumentError('The extension manifest is not supported.')
    for key in ('permissions','host_permissions'):
        values=data.get(key,[])
        if not isinstance(values,list) or len(values)>256 or any(not isinstance(value,str) or len(value)>2048 for value in values):raise DocumentError('The extension permission list is invalid.')
    return data

def extract_package(archive,root):
    root=Path(root);root.mkdir(parents=True,exist_ok=True)
    archive=Path(archive)
    if not archive.is_file() or archive.stat().st_size>MAX_PACKAGE:raise DocumentError('Choose an extension ZIP smaller than 64 MiB.')
    with archive.open('rb') as source:raw=source.read(MAX_PACKAGE+1)
    if len(raw)>MAX_PACKAGE:raise DocumentError('The extension ZIP is too large.')
    digest=hashlib.sha256(raw).hexdigest()
    destination=root/digest
    if destination.exists():manifest(destination);return destination
    with tempfile.TemporaryDirectory(prefix='.extension-',dir=root) as temporary:
        target=Path(temporary)
        with zipfile.ZipFile(io.BytesIO(raw)) as package:
            entries=package.infolist();total=0;names=set()
            if len(entries)>10000:raise DocumentError('The extension contains too many files.')
            for entry in entries:
                name=PurePosixPath(entry.filename)
                mode=(entry.external_attr>>16)&0xffff
                if name.is_absolute() or '..' in name.parts or '\\' in entry.filename or '\x00' in entry.filename or entry.filename in names or stat.S_ISLNK(mode):raise DocumentError('The extension package contains unsafe paths.')
                names.add(entry.filename);total+=entry.file_size
                if entry.file_size>64*1024*1024 or total>MAX_EXPANDED or entry.flag_bits&1:raise DocumentError('The extension package exceeds the supported size.')
                path=target.joinpath(*name.parts)
                if entry.is_dir():path.mkdir(parents=True,exist_ok=True);continue
                path.parent.mkdir(parents=True,exist_ok=True)
                remaining=entry.file_size
                with package.open(entry) as source,path.open('xb') as output:
                    while remaining:
                        block=source.read(min(65536,remaining))
                        if not block:raise DocumentError('The extension package is incomplete.')
                        output.write(block);remaining-=len(block)
                path.chmod(0o600)
        manifest(target);os.rename(target,destination)
    return destination

def import_folder(folder,root):
    folder=Path(folder).resolve();manifest(folder);root=Path(root);root.mkdir(parents=True,exist_ok=True)
    with tempfile.NamedTemporaryFile(suffix='.zip',dir=root) as temporary:
        total=0;count=0
        with zipfile.ZipFile(temporary.name,'w',compression=zipfile.ZIP_DEFLATED) as package:
            for base,dirs,files in os.walk(folder,followlinks=False):
                for name in dirs+files:
                    if (Path(base)/name).is_symlink():raise DocumentError('Extension folders must not contain symbolic links.')
                for name in files:
                    file=Path(base)/name
                    if not stat.S_ISREG(file.stat().st_mode):raise DocumentError('Extension folders may contain only regular files.')
                    total+=file.stat().st_size;count+=1
                    if total>MAX_EXPANDED or count>10000:raise DocumentError('The extension folder is too large.')
                    package.write(file,file.relative_to(folder).as_posix())
        return extract_package(temporary.name,root)
