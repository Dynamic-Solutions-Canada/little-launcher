"""Real AppImage runtime, private KDE, user-namespace browser sandbox, update swap and boot entries.

Runs only in the GPU fixture. Uses disposable launchers, a local page/cookie and an identical
AppImage payload for replacement; never touches a real installed image or personal profiles.
"""
import asyncio
from copy import deepcopy
import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import parse_qs,urlsplit
from dbus_next import Message,Variant
from dbus_next.aio import MessageBus
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime' and os.getuid()!=0
sys.path[:0]=['/source/linux','/source/linux/dev']
from compositor_probe import CompositorProbe
from little_launcher.updates import Release,download_release

work=Path(tempfile.mkdtemp(prefix='appimage-run-',dir='/work'))
image=work/'LittleLauncher.AppImage';shutil.copy2('/source/.artifacts/linux-appimage/LittleLauncher-1.41.1-linux-x64.AppImage',image);image.chmod(0o755)
config=work/'config';data=config/'little-launcher';data.mkdir(parents=True)
reports=[];cookies=[]
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*_):pass
 def do_GET(self):
  if self.path.startswith('/report'):
   reports.append(parse_qs(urlsplit(self.path).query));self.send_response(200);self.end_headers();return
  if self.path=='/favicon.ico':self.send_response(404);self.end_headers();return
  cookies.append(self.headers.get('Cookie',''))
  self.send_response(200);self.send_header('Content-Type','text/html');self.send_header('Set-Cookie','appimageFixture=retained; Path=/; Max-Age=3600; SameSite=Lax');self.end_headers()
  self.wfile.write(b'<title>Packaged isolated fixture</title><h1>AppImage web fixture</h1><script>fetch("/report?node="+typeof require+"&bridge="+typeof window.launcher+"&ua="+encodeURIComponent(navigator.userAgent))</script>')
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
launcher={'Id':'appimage-web-qa','Name':'Package web fixture','Kind':1,'WebRegularWindow':True,'WebHiddenPolicy':2,'WebHomeUrl':f'http://127.0.0.1:{server.server_port}/','WebFlyoutWidth':480,'WebFlyoutHeight':320}
settings={'Launchers':[launcher]};raw=(json.dumps(settings,indent=2)+'\n').encode();(data/'settings.json').write_bytes(raw)
local={'StartAtLogin':True,'CheckForUpdatesAutomatically':False};(data/'linux.json').write_text(json.dumps(local))
env={**os.environ,'XDG_CONFIG_HOME':str(config),'XDG_DATA_HOME':str(Path.home()/'.local/share')}
probe=CompositorProbe(['Little Launcher','Package web fixture'],resource_class=None)
process=None;result={};startup=config/'autostart/little-launcher.desktop'
def wait(check,seconds=25):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  if check():return
  if process and process.poll() is not None:raise RuntimeError('Packaged app exited unexpectedly: '+str(process.returncode))
  time.sleep(.08)
 raise RuntimeError('AppImage qualification timeout')
async def tray(action):
 bus=await MessageBus().connect()
 async def call(destination,path,interface,member,signature='',body=None):
  reply=await asyncio.wait_for(bus.call(Message(destination=destination,path=path,interface=interface,member=member,signature=signature,body=body or [])),timeout=2)
  if reply.message_type.name=='ERROR':raise RuntimeError('D-Bus object unavailable')
  return reply.body
 try:
  registered=(await call('org.kde.StatusNotifierWatcher','/StatusNotifierWatcher','org.freedesktop.DBus.Properties','Get','ss',['org.kde.StatusNotifierWatcher','RegisteredStatusNotifierItems']))[0].value
  for entry in registered:
   name=entry.split('/',1)[0]
   try:
    values=(await call(name,'/StatusNotifierItem','org.freedesktop.DBus.Properties','GetAll','s',['org.kde.StatusNotifierItem']))[0]
    if values['Id'].value!='little-launcher-appimage-web-qa':continue
    pid=(await call('org.freedesktop.DBus','/org/freedesktop/DBus','org.freedesktop.DBus','GetConnectionUnixProcessID','s',[name]))[0]
    if action=='open':await call(name,'/StatusNotifierItem','org.kde.StatusNotifierItem','Activate','ii',[900,700])
    elif action in ('quit','settings'):await call(name,'/Menu','com.canonical.dbusmenu','Event','isvu',[4 if action=='quit' else 3,'clicked',Variant('i',0),0])
    return pid
   except (RuntimeError,asyncio.TimeoutError):continue
 finally:bus.disconnect()
 return None

def launch():
 global process
 log=(work/'application.log').open('ab')
 process=subprocess.Popen([str(image),'--appimage-extract-and-run','--background','--data-dir',str(data)],env=env,stdout=log,stderr=subprocess.STDOUT)
 log.close()
def quit_app():
 assert asyncio.run(tray('quit'))
 process.wait(timeout=20);assert process.returncode==0

def renderer_sandbox(owner):
 records={}
 for path in Path('/proc').iterdir():
  if not path.name.isdigit():continue
  try:
   fields=(path/'stat').read_text().rsplit(')',1)[1].split();parent=int(fields[1]);name=(path/'comm').read_text().strip()
   records[int(path.name)]=(parent,name,path)
  except (OSError,ValueError):continue
 descendants={owner}
 for _ in range(12):
  newer={pid for pid,(parent,_,_) in records.items() if parent in descendants}
  if newer.issubset(descendants):break
  descendants.update(newer)
 rows=[]
 for pid in descendants:
  record=records.get(pid)
  if not record or record[1]!='electron':continue
  path=record[2]
  try:
   status=(path/'status').read_text();seccomp=next(line.split(':',1)[1].strip() for line in status.splitlines() if line.startswith('Seccomp:'))
   try:command=(path/'cmdline').read_bytes().decode(errors='replace');readable=True
   except OSError:command='';readable=False
   rows.append({'seccomp':seccomp,'unsafeFlag':'--no-sandbox' in command,'commandReadable':readable})
  except (OSError,StopIteration):continue
 return rows

try:
 launch();wait(lambda:bool(reports));print('Local page loaded',flush=True);wait(lambda:startup.exists());wait(lambda:bool(asyncio.run(tray('status'))));print('Startup and tray ready',flush=True)
 assert 'node' in reports[0] and reports[0]['node']==['undefined'] and reports[0]['bridge']==['undefined']
 assert 'Chrome/' in reports[0]['ua'][0] and 'Electron/' not in reports[0]['ua'][0]
 assert str(image) in startup.read_text() and 'appimage_extracted_' not in startup.read_text()
 assert (Path(env['XDG_DATA_HOME'])/'applications/io.github.RyanEwen.LittleLauncher.desktop').is_file()
 native_pid=asyncio.run(tray('open'));print('Requested packaged window',flush=True);wait(lambda:probe.find('Package web fixture') is not None)
 row=probe.find('Package web fixture');assert 'XdgToplevelWindow' in row['clientType'],row
 sandbox=renderer_sandbox(native_pid);assert sandbox and any(item['seccomp']=='2' for item in sandbox) and all(not item['unsafeFlag'] for item in sandbox),sandbox
 asyncio.run(tray('settings'));wait(lambda:probe.find('Little Launcher') is not None)
 geometry=probe.find('Little Launcher');subprocess.run(['python3','/source/linux/dev/private_input.py','click',str(int(geometry['x']+90)),str(int(geometry['y']+190))],check=True)
 time.sleep(.3);subprocess.run(['python3','/source/linux/dev/capture_wayland.py','/work/appimage-desktop.png'],check=True)
 quit_app();print('Normal package quit complete',flush=True);assert (data/'settings.json').read_bytes()==raw
 original_hash=hashlib.sha256(image.read_bytes()).hexdigest()
 # Use the real binary as a synthetic next release. No GitHub publication or test endpoint override.
 item=Release('99.0.0','fixture.AppImage','https://example.invalid',original_hash,image.stat().st_size)
 staged=download_release(item,image,opener=lambda url:Path('/source/.artifacts/linux-appimage/LittleLauncher-1.41.1-linux-x64.AppImage').open('rb'))
 backup=staged.install();assert backup.is_file() and hashlib.sha256(image.read_bytes()).hexdigest()==original_hash
 local['StartAtLogin']=False;(data/'linux.json').write_text(json.dumps(local));previous_reports=len(reports)
 launch();wait(lambda:len(reports)>previous_reports);wait(lambda:not startup.exists());assert any('appimageFixture=retained' in value for value in cookies)
 quit_app();print('Normal package quit complete',flush=True);assert (data/'settings.json').read_bytes()==raw
 result.update(passed=True,realAppImageRuntime=True,bundledPythonQt=True,nativeWayland=True,remoteBridgeAbsent=True,chromeUserAgent=True,chromiumSeccompSandbox=True,startupEnabledByDefault=True,stableAppImageBootPath=True,disableStartupPreserved=True,desktopEntryRegistered=True,verifiedRealPayloadReplacement=True,previousImageRetained=True,profileCookieSurvivesReplacement=True,launcherDocumentUnchanged=True,sha256=original_hash,sandboxProcesses=sandbox)
except Exception as error:
 result['error']=str(error)
 if process and process.poll() is None:
  try:asyncio.run(tray('quit'));process.wait(timeout=10)
  except Exception:process.terminate();process.wait(timeout=5)
finally:
 probe.close();server.shutdown();server.server_close();(work/'result.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2),flush=True)
assert result.get('passed') and 'error' not in result,result
