"""Real edge drag in the private Wayland compositor, never the user's desktop."""
import argparse,json,os,subprocess,sys,tempfile
from copy import deepcopy
from pathlib import Path
assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QPoint,QTimer
from PySide6.QtWidgets import QApplication
from little_launcher.controller import LauncherController
from little_launcher.editor import ItemsEditor
from little_launcher.flyout import ShortcutFlyout
from little_launcher.desktop import DesktopIntegration
from compositor_probe import CompositorProbe
parser=argparse.ArgumentParser();parser.add_argument('--mode',type=int,choices=(0,2),default=0);parser.add_argument('--host',choices=('editor','flyout'),default='editor');parser.add_argument('--trace-resize',action='store_true');parser.add_argument('--left',action='store_true');options=parser.parse_args()
if '--trace-resize' in sys.argv:
 from little_launcher.item_resize import IconResizeGrip
 original_move=IconResizeGrip.mouseMoveEvent
 def traced(self,event):
  print('MOVE',event.globalPosition().toTuple(),event.position().toTuple(),self.view.window().pos().toTuple(),self.view.width(),self.start_x,flush=True)
  original_move(self,event)
 IconResizeGrip.mouseMoveEvent=traced
 sys.argv.remove('--trace-resize')
app=QApplication([]);app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
root=Path(tempfile.mkdtemp(prefix='/work/icon-resize-'));controller=LauncherController(root)
launcher={'Id':'icon-resize-test','Platform':'linux','Name':'Grid resize','ViewMode':options.mode,'IconModeIconsPerRow':3,'Opaque':9,'Items':[{'Name':str(i),'Path':'/app','Future':i}for i in range(7)]}
assert controller.edit(lambda doc:doc['Launchers'].append(deepcopy(launcher)))
if options.host=='editor':
 window=ItemsEditor(controller,launcher);window.setWindowTitle('QAIconResize:editor');prefix='QAIconResize:'
else:
 DesktopIntegration().enable();window=ShortcutFlyout(launcher,[1100,770],controller=controller);prefix='LittleLauncherFlyout:icon-resize-test:'
probe=CompositorProbe(prefix);window.show();result={'host':options.host,'mode':options.mode,'edge':'left' if options.left else 'right'};worker=None

def begin():
 if not probe.latest:QTimer.singleShot(100,begin);return
 if options.host=='flyout':window.enter_edit()
 QTimer.singleShot(400,drag)
def drag():
 global worker
 view=window.view;grip=view.resize_grips[0 if options.left else 1];p=grip.mapTo(window,grip.rect().center());x=round(probe.latest['x'])+p.x();y=round(probe.latest['y'])+p.y();step=view.metrics.cell_width
 worker=subprocess.Popen(['python3','/source/linux/dev/private_input.py','drag',str(x),str(y),str(x+step if options.left else x-step),str(y),'0.7'])
 QTimer.singleShot(650,held)
def held():
 try:
  assert controller.launcher('icon-resize-test')['IconModeIconsPerRow']==3
  assert window.view.resize_preview is not None
  assert window.view.resize_preview.metrics.per_row==2
  result['livePreviewBeforeSave']=True
  QTimer.singleShot(750,finished)
 except Exception as error:fail(error)
def finished():
 try:
  if worker.poll()is None:QTimer.singleShot(100,finished);return
  assert worker.returncode==0
  saved=controller.launcher('icon-resize-test');assert saved['IconModeIconsPerRow']==2,saved
  assert saved['Items']==launcher['Items']and saved['Opaque']==9
  assert window.view.metrics.per_row==2
  result.update(passed=True,realWaylandEdgeDrag=True,releaseOnlySave=True,fullSourceRetained=True)
  window.grab().save(str(root/'window.png'));finish()
 except Exception as error:fail(error)
def fail(error):result['error']=repr(error);finish()
def finish():
 (root/'result.json').write_text(json.dumps(result,indent=2)+'\n');print(root,json.dumps(result),flush=True);window.hide();app.quit()
QTimer.singleShot(600,begin);QTimer.singleShot(12000,app.quit);app.exec();probe.close();assert result.get('passed'),result
