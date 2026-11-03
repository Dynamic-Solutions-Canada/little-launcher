"""Actual KDE titlebar drag and last-position restoration, using private synthetic settings."""
import http.server
import threading
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication
from little_launcher.browser import BrowserHost
from little_launcher.controller import LauncherController
from little_launcher.desktop import ICON
from little_launcher.positions import PlacementBridge
from preview_integration import TemporaryPreviewIntegration
from compositor_probe import CompositorProbe
app=QApplication([]);app.setQuitOnLastWindowClosed(False);app.setWindowIcon(QIcon(str(ICON)))
root=Path(tempfile.mkdtemp(prefix='saved-position-',dir='/work'))
controller=LauncherController(root)
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*_):pass
 def do_GET(self):
  self.send_response(200);self.end_headers();self.wfile.write(b'Disposable position fixture')
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
launcher={'Id':'saved-position-qa','Name':'Saved position fixture','Kind':1,'WebAnchor':10,'WebRegularWindow':True,'WebFlyoutWidth':440,'WebFlyoutHeight':300,'WebHomeUrl':f'http://127.0.0.1:{server.server_port}/','WebFlyoutPosition':'10000,9000'}
controller.edit(lambda document:document.update(Launchers=[launcher]))
helper=TemporaryPreviewIntegration(root,controller.launchers)
app.setDesktopFileName(helper.application_id)
os.environ['LITTLE_LAUNCHER_PREVIEW_ID']=helper.preview_id
os.environ['LITTLE_LAUNCHER_ELECTRON']='/opt/electron/electron'
bridge=PlacementBridge(controller);assert bridge.start()
helper.load();probe=CompositorProbe(['Saved position fixture','LittleLauncherWindow:saved-position-qa:'],resource_class=None)
browser=BrowserHost(root);browser.position_for=bridge.get;browser.position_supported=helper.supports_launcher
errors=[];browser.failed.connect(errors.append);browser.quit_ready.connect(app.quit)
result={};original=controller.store.path.read_bytes();revision=controller.revision
start=time.monotonic()
def drag():
 try:
  row=probe.find('Saved position fixture')
  if not row or not browser.ready:
   assert time.monotonic()-start<12,'Initial window did not map';QTimer.singleShot(100,drag);return
  frame=row['frame'];sx=int(frame['x']+frame['width']/2);sy=int(frame['y']+12)
  subprocess.Popen(['python3','/source/linux/dev/private_input.py','drag',str(sx),str(sy),'450','180'])
  QTimer.singleShot(1600,check_drag)
 except Exception as error:result['error']=str(error);finish()
def check_drag():
 try:
  position=bridge.get(launcher['Id']);assert position,'Real titlebar drag did not save coordinates'
  result['saved']=position
  assert controller.store.path.read_bytes()==original and controller.revision==revision
  assert bridge.get(launcher['Id'])==PlacementBridge(LauncherController(root)).get(launcher['Id'])
  browser.show(launcher,[1100,770])
  QTimer.singleShot(300,reopen)
 except Exception as error:result['error']=str(error);finish()
def reopen():
 browser.show(launcher,[1100,770]);QTimer.singleShot(1000,check_reopen)
def check_reopen():
 try:
  row=probe.find('Saved position fixture');saved=result['saved'];assert abs(row['frame']['x']-saved[0])<=1 and abs(row['frame']['y']-saved[1])<=1,row
  result.update(passed=True,realKdeTitlebarDrag=True,reopenAtSavedPosition=True,portableSettingsUnchanged=True,windowsCoordinatesIgnored=True,nativeWayland='XdgToplevelWindow' in row['clientType'])
 except Exception as error:result['error']=str(error)
 finally:finish()
def finish():
 if result.get('finished'):return
 result['finished']=True
 if errors:result['browserErrors']=errors
 print(json.dumps(result,indent=2),flush=True)
 probe.close();helper.close();bridge.close();server.shutdown();browser.request_quit()
QTimer.singleShot(100,lambda:browser.show(launcher,[1100,770]))
QTimer.singleShot(700,drag)
QTimer.singleShot(20000,lambda:(result.update(error='Saved position fixture deadline'),finish()))
app.exec();assert result.get('passed') and 'error' not in result,result
