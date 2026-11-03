"""Capture native KDE Wayland window icons using synthetic launchers and no desktop entries."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication
from PIL import Image
from little_launcher.browser import BrowserHost
from little_launcher.controller import LauncherController
from little_launcher.icons import LauncherIcons
from compositor_probe import CompositorProbe
from preview_integration import TemporaryPreviewIntegration
app=QApplication([]);app.setQuitOnLastWindowClosed(False)
root=Path(tempfile.mkdtemp(prefix='window-icons-',dir='/work'))
controller=LauncherController(root)
launchers=[{'Id':'icon-'+color.lower(),'Name':'Window icon fixture '+color,'Kind':1,'TrayIconMode':color,
 'WebHomeUrl':'about:blank','WebRegularWindow':True,'WebAnchor':anchor,'WebFlyoutWidth':400,'WebFlyoutHeight':320}
 for color,anchor in [('Green',7),('Purple',9)]]
# The standard model permits an empty home; no network sites are used here.
for launcher in launchers:launcher['WebHomeUrl']=''
controller.edit(lambda doc:doc['Launchers'].extend(launchers))
helper=TemporaryPreviewIntegration(root,launchers);helper.load()
os.environ['LITTLE_LAUNCHER_PREVIEW_ID']=helper.preview_id
os.environ['LITTLE_LAUNCHER_ELECTRON']='/opt/electron/electron'
probe=CompositorProbe(['LittleLauncherWindow:icon-','Window icon fixture'],resource_class=None)
browser=BrowserHost(root);browser.quit_ready.connect(app.quit)
errors=[];browser.failed.connect(errors.append)
result={}
def finish():
 try:
  rows=[probe.find(launcher['Name']) for launcher in launchers]
  assert all(rows),rows
  assert all('XdgToplevelWindow' in row['clientType'] for row in rows),rows
  subprocess.run([sys.executable,'/source/linux/dev/capture_wayland.py','/work/window-icons.png'],check=True,capture_output=True)
  image=Image.open('/work/window-icons.png').convert('RGB')
  for color,row in zip(['Green','Purple'],rows):
   frame=row['frame']
   pixels=list(image.crop((frame['x']+5,frame['y']+3,frame['x']+25,frame['y']+25)).getdata())
   matches=sum((g>r*1.3 and g>b*1.3) if color=='Green' else (r>g*1.3 and b>g*1.3) for r,g,b in pixels)
   assert matches>=8,(color,matches)
  result.update(titlebarIconPixelsVerified=True,nativeWayland=True,windows=rows,iconPayloadsDistinct=LauncherIcons.window_png(launchers[0])!=LauncherIcons.window_png(launchers[1]),errors=errors)
 except Exception as error:result['error']=str(error)
 finally:
  (root/'result.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2),flush=True)
  probe.close();helper.close();browser.request_quit()
QTimer.singleShot(100,lambda:browser.show(launchers[0],[1100,770]))
QTimer.singleShot(800,lambda:browser.show(launchers[1],[1100,770]))
QTimer.singleShot(3000,finish)
app.exec()
assert 'error' not in result,result
