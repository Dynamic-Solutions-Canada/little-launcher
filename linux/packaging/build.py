#!/usr/bin/env python3
"""Build the only Linux delivery format: a self-contained x64 AppImage, in an isolated container."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.request import Request,urlopen
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
SOURCE=ROOT/'linux'
BUILDER='little-launcher-appimage-builder:local'
# Host C runtime, graphics drivers and C++ runtime must match the host. Debian 12 is the baseline.
HOST_LIBRARIES=re.compile(r'^(?:ld-linux|lib(?:c|m|pthread|dl|rt|resolv|util|anl)\.so|libnss_(?:dns|files)\.|libstdc\+\+\.|libgcc_s\.|lib(?:GL|EGL|GLX|GLdispatch|gbm|drm|vulkan))')


def run(arguments,**kwargs):return subprocess.run([str(value) for value in arguments],check=True,**kwargs)


def runtime_file(output):
    lock=json.loads((SOURCE/'packaging/runtime-lock.json').read_text())
    destination=output/'build-inputs/runtime-x86_64';destination.parent.mkdir(parents=True,exist_ok=True)
    if not destination.exists():
        with urlopen(Request(lock['url'],headers={'User-Agent':'LittleLauncher-AppImage-builder'}),timeout=30) as response:raw=response.read(lock['size']+1)
        if len(raw)!=lock['size'] or hashlib.sha256(raw).hexdigest()!=lock['sha256']:raise RuntimeError('The official AppImage runtime does not match runtime-lock.json. Review and update the lock; no unverified runtime will be used.')
        destination.write_bytes(raw)
    if destination.stat().st_size!=lock['size'] or hashlib.sha256(destination.read_bytes()).hexdigest()!=lock['sha256']:raise RuntimeError('The cached AppImage runtime failed verification.')
    return destination


def stage(appdir,engine,version):
    app=appdir/'usr/lib/little-launcher';app.mkdir(parents=True)
    for name in ['bin','little_launcher','resources','desktop/kwin']:
        shutil.copytree(SOURCE/name,app/name,ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
    browser=app/'browser';browser.mkdir()
    for path in (SOURCE/'browser').iterdir():
        if path.suffix in ('.cjs','.js','.css','.html') or path.name=='package.json':shutil.copy2(path,browser/path.name)
    shutil.copytree(SOURCE/'browser/vendor/generated',browser/'vendor/generated')
    shutil.copytree(SOURCE/'browser/vendor/browser-extension-compat',browser/'vendor/browser-extension-compat',ignore=shutil.ignore_patterns('*.ts'))
    package_lock=json.loads((SOURCE/'browser/package-lock.json').read_text())
    for name,metadata in package_lock.get('packages',{}).items():
        if name.startswith('node_modules/') and not metadata.get('dev'):
            shutil.copytree(SOURCE/'browser'/name,browser/name,dirs_exist_ok=True)
    shutil.copytree(engine,browser/'node_modules/electron/dist')
    os.chmod(browser/'node_modules/electron/dist/chrome-sandbox',0o755)
    (app/'VERSION').write_text(version+'\n')
    shutil.copy2(SOURCE/'packaging/appimage_bootstrap.py',app/'appimage_bootstrap.py')
    shutil.copy2(SOURCE/'packaging/host_bootstrap.py',app/'host_bootstrap.py')
    shutil.copy2(SOURCE/'packaging/AppRun',appdir/'AppRun');(appdir/'AppRun').chmod(0o755)
    shutil.copy2(SOURCE/'desktop/io.github.RyanEwen.LittleLauncher.desktop',appdir/'io.github.RyanEwen.LittleLauncher.desktop')
    text=(appdir/'io.github.RyanEwen.LittleLauncher.desktop').read_text().replace('Exec=little-launcher','Exec=AppRun')
    (appdir/'io.github.RyanEwen.LittleLauncher.desktop').write_text(text)
    shutil.copy2(SOURCE/'resources/little-launcher.png',appdir/'io.github.RyanEwen.LittleLauncher.png')
    (appdir/'.DirIcon').symlink_to('io.github.RyanEwen.LittleLauncher.png')
    binary=appdir/'usr/bin';binary.mkdir(parents=True)
    shutil.copy2('/usr/bin/python3.11',binary/'python3.11')
    shutil.copytree('/usr/lib/python3.11',appdir/'usr/lib/python3.11',ignore=shutil.ignore_patterns('__pycache__','*.pyc','test','tests','idlelib','tkinter','ensurepip'))
    site=appdir/'usr/lib/python3.11/site-packages'
    shutil.copytree('/opt/little-launcher-site',site,ignore=shutil.ignore_patterns('__pycache__','*.pyc'))
    # This is a Widgets client, not a QML compositor or a Qt development toolkit.
    qt=site/'PySide6/Qt'
    for name in ['qml','libexec','bin']:
        shutil.rmtree(qt/name,ignore_errors=True)
    plugins={'platforms','platformthemes','styles','imageformats','iconengines','tls','wayland-graphics-integration-client','wayland-shell-integration','platforminputcontexts','xcbglintegrations','networkinformation'}
    (qt/'plugins/platforminputcontexts/libqtvirtualkeyboardplugin.so').unlink(missing_ok=True)
    (qt/'plugins/imageformats/libqpdf.so').unlink(missing_ok=True)
    for path in (qt/'plugins/platforms').glob('*.so'):
        if not (path.name.startswith('libqwayland') or path.name in ('libqxcb.so','libqoffscreen.so','libqminimal.so')):path.unlink()
    for path in (qt/'plugins').iterdir():
        if path.is_dir() and path.name not in plugins:shutil.rmtree(path)
    shutil.copy2('/usr/bin/secret-tool',binary/'secret-tool')
    qdbus=Path('/usr/lib/qt6/bin/qdbus')
    if not qdbus.is_file():qdbus=Path('/usr/lib/qt6/bin/qdbus6')
    shutil.copy2(qdbus,binary/'qdbus6')
    certificates=appdir/'usr/share/little-launcher';certificates.mkdir(parents=True)
    shutil.copy2('/etc/ssl/certs/ca-certificates.crt',certificates/'ca-certificates.crt')
    licenses=appdir/'usr/share/licenses/little-launcher';licenses.mkdir(parents=True)
    shutil.copy2(ROOT/'LICENSE.md',licenses/'LICENSE.md')
    shutil.copytree(SOURCE/'packaging/licenses',licenses/'bundled-runtime')
    system_licenses=appdir/'usr/share/licenses/system';system_licenses.mkdir(parents=True)
    for package in ['python3.11','python3.11-minimal','libpython3.11-stdlib','libpython3.11-minimal','qdbus-qt6','libsecret-tools']:
        notice=Path('/usr/share/doc')/package/'copyright'
        if notice.is_file():shutil.copy2(notice,system_licenses/(package+'.copyright'))


def elf_files(directory):
    for path in directory.rglob('*'):
        if path.is_file() and not path.is_symlink():
            with path.open('rb') as stream:
                if stream.read(4)==b'\x7fELF':yield path


def bundle_libraries(appdir):
    common=appdir/'usr/lib'
    qt=common/'python3.11/site-packages/PySide6/Qt/lib'
    env={**os.environ,'LD_LIBRARY_PATH':str(qt)+':'+str(common)}
    queue=list(elf_files(appdir));seen=set();external={};missing=set()
    # NSS loads these lazily; they are not present in Electron's static DT_NEEDED list.
    for name in ['libsoftokn3.so','libfreebl3.so','libfreeblpriv3.so','libnotify.so.4']:
        path=Path('/usr/lib/x86_64-linux-gnu')/name
        if path.is_file():
            target=common/name;shutil.copy2(path,target);queue.append(target);external[name]=str(path)
    while queue:
        path=queue.pop()
        if path in seen:continue
        seen.add(path)
        result=subprocess.run(['ldd',str(path)],env=env,text=True,capture_output=True)
        if result.returncode and ('not a dynamic executable' in result.stderr or 'statically linked' in result.stdout):continue
        for line in result.stdout.splitlines():
            if '=> not found' in line:
                if HOST_LIBRARIES.match(line.split('=>',1)[0].strip()):continue
                missing.add(f'{path.relative_to(appdir)}: {line.strip()}');continue
            match=re.match(r'\s*(\S+)\s+=>\s+(/\S+)\s+\(',line)
            if not match:continue
            name,origin=match.groups()
            if HOST_LIBRARIES.match(name):continue
            original=Path(origin).resolve();target=common/name
            if appdir in original.parents:
                if not target.exists() and original!=target:target.symlink_to(os.path.relpath(original,common))
                continue
            if target.exists():continue
            shutil.copy2(original,target);external[name]=str(original);queue.append(target)
    if missing:raise RuntimeError('Missing bundled runtime dependencies:\n'+'\n'.join(sorted(missing)))
    for path in elf_files(appdir):
        result=subprocess.run(['patchelf','--print-rpath',str(path)],text=True,capture_output=True)
        if result.returncode:continue
        existing=result.stdout.strip();relative='$ORIGIN/'+os.path.relpath(common,path.parent)
        search=existing.split(':') if existing else []
        if relative not in search:search.append(relative)
        run(['patchelf','--set-rpath',':'.join(search),path],stdout=subprocess.DEVNULL)
    # Preserve redistribution notices for every copied distro library/tool.
    license_root=appdir/'usr/share/licenses/system';license_root.mkdir(parents=True,exist_ok=True)
    for name,origin in external.items():
        result=subprocess.run(['dpkg-query','-S',origin],text=True,capture_output=True)
        if result.returncode:result=subprocess.run(['dpkg-query','-S','*/'+Path(origin).name],text=True,capture_output=True)
        for line in result.stdout.splitlines():
            package=line.split(': ',1)[0].split(':',1)[0];copyright=Path('/usr/share/doc')/package/'copyright'
            if copyright.is_file():shutil.copy2(copyright,license_root/(package+'.copyright'))
    manifest={'baseline':'glibc >= 2.36; host graphics and C++ runtime','python':'3.11','qt':'6.11.2','externalLibraries':external}
    (appdir/'usr/share/little-launcher/runtime-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine-dir',required=True,type=Path)
    parser.add_argument('--output',type=Path,default=ROOT/'.artifacts/linux-appimage')
    parser.add_argument('--inside-container',action='store_true',help=argparse.SUPPRESS)
    parser.add_argument('--runtime-file',type=Path,help=argparse.SUPPRESS)
    parser.add_argument('--keep-appdir',action='store_true',help='Keep the private intermediate AppDir for qualification')
    options=parser.parse_args();engine=options.engine_dir.resolve();output=options.output.resolve();output.mkdir(parents=True,exist_ok=True)
    for name in ['electron','chrome-sandbox','LICENSE','LICENSES.chromium.html']:
        if not (engine/name).is_file():parser.error('A complete locked Linux x64 Electron distribution is required: missing '+name)
    with (engine/'electron').open('rb') as executable:header=executable.read(20)
    if header[:4]!=b'\x7fELF' or int.from_bytes(header[18:20],'little')!=62:parser.error('This build target needs the Linux x64 Electron engine.')
    engine_version=(engine/'version').read_text().strip() if (engine/'version').is_file() else ''
    locked_engine=json.loads((SOURCE/'browser/package.json').read_text())['devDependencies']['electron']
    if engine_version!=locked_engine:parser.error('The supplied Electron distribution does not match the locked version '+locked_engine+'.')
    version=ET.parse(ROOT/'Directory.Build.props').findtext('.//Version')
    if not version or not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?',version):parser.error('The repository version is invalid.')
    destination=output/f'LittleLauncher-{version}-linux-x64.AppImage'
    if destination.exists():parser.error('Refusing to replace existing output: '+str(destination))
    if not options.inside_container:
        run(['node',SOURCE/'browser/build-vendor.cjs'])
        runtime=runtime_file(output)
        run(['docker','build','-t',BUILDER,'-f',SOURCE/'packaging/Dockerfile',SOURCE/'packaging'])
        arguments=['docker','run','--rm','--network','none','--memory','2g','--memory-swap','3g','--cpus','2','--pids-limit','256','--user',f'{os.getuid()}:{os.getgid()}',
            '-v',str(ROOT)+':/source:ro','-v',str(engine)+':/engine:ro','-v',str(output)+':/output','-v',str(runtime)+':/runtime:ro',
            BUILDER,'python3','/source/linux/packaging/build.py','--inside-container','--engine-dir','/engine','--output','/output','--runtime-file','/runtime']
        if options.keep_appdir:arguments.append('--keep-appdir')
        run(arguments);return
    if options.runtime_file is None:parser.error('The pinned runtime input is required.')
    lock=json.loads((SOURCE/'packaging/runtime-lock.json').read_text())
    raw=options.runtime_file.read_bytes()
    if hashlib.sha256(raw).hexdigest()!=lock['sha256']:parser.error('The runtime hash does not match the reviewed lock.')
    with tempfile.TemporaryDirectory(prefix='little-launcher-appimage-') as temporary:
        appdir=Path(temporary)/'LittleLauncher.AppDir';stage(appdir,engine,version);bundle_libraries(appdir)
        run(['desktop-file-validate',appdir/'io.github.RyanEwen.LittleLauncher.desktop'])
        # Verify relocated Python imports with no host Python/Qt paths.
        run([appdir/'usr/bin/python3.11','-I','-c',f'import sys;sys.path.insert(0,{str(appdir/"usr/lib/python3.11/site-packages")!r});from PySide6 import QtWidgets,QtNetwork,QtDBus;import dbus_next;print("Bundled Python/Qt imports verified")'])
        squashfs=Path(temporary)/'payload.squashfs'
        run(['mksquashfs',appdir,squashfs,'-noappend','-all-root','-comp','zstd','-Xcompression-level','15','-processors','1','-mem','256M','-no-progress'])
        with destination.open('xb') as image,options.runtime_file.open('rb') as runtime,squashfs.open('rb') as payload:
            shutil.copyfileobj(runtime,image);shutil.copyfileobj(payload,image);image.flush();os.fsync(image.fileno())
        destination.chmod(0o755)
        digest=hashlib.sha256(destination.read_bytes()).hexdigest();destination.with_suffix('.AppImage.sha256').write_text(digest+'  '+destination.name+'\n')
        if options.keep_appdir:shutil.copytree(appdir,output/'qualification.AppDir',symlinks=True)
        print(destination,flush=True)

if __name__=='__main__':main()
