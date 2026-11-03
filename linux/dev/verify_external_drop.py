"""Real pointer URI drop between native windows, only inside the private KDE fixture."""
from copy import deepcopy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from uuid import uuid4
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QMimeData,QPoint,QTimer,QUrl,Qt
from PySide6.QtGui import QDrag,QIcon
from PySide6.QtWidgets import QApplication,QLabel,QVBoxLayout,QWidget
from little_launcher.controller import LauncherController
from little_launcher.desktop import DesktopIntegration,ICON
from little_launcher.editor import ItemsEditor
from compositor_probe import CompositorProbe
app=QApplication([]);app.setQuitOnLastWindowClosed(False);app.setDesktopFileName('io.github.RyanEwen.LittleLauncher');app.setWindowIcon(QIcon(str(ICON)))
root=Path(tempfile.mkdtemp(prefix='external-drop-',dir='/work'))
file=root/'document.txt';file.write_text('untouched file')
controller=LauncherController(root)
launcher={'Id':'external-qa','Name':'External drop','ViewMode':1,'Items':[{'Name':'Group','IsGroup':True,'Children':[
 {'Name':'Foreign child','Path':'C:\\app.exe','SupportedOperatingSystems':['windows'],'Future':7},
 {'Name':'Local child','Path':'/bin/echo','SupportedOperatingSystems':['linux']}]}]}
controller.edit(lambda document:document['Launchers'].append(deepcopy(launcher)))
integration=DesktopIntegration();script_id='external-drop-qa-'+uuid4().hex
script=root/'position.js';script.write_text("""workspace.windowAdded.connect(function(window){
 if(String(window.resourceClass)!=='io.github.RyanEwen.LittleLauncher')return;
 if(window.caption==='QAExternal:Target')window.frameGeometry={x:80,y:60,width:560,height:620};
 if(window.caption==='QAExternal:Source')window.frameGeometry={x:930,y:70,width:260,height:140};
});""")
integration._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.loadScript',str(script),script_id)
integration._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.start')
probe=CompositorProbe('QAExternal:')
target=ItemsEditor(controller,launcher);target.setWindowTitle('QAExternal:Target')
result={};worker=None
class Source(QWidget):
    def mousePressEvent(self,event):self.origin=event.position().toPoint()
    def mouseMoveEvent(self,event):
        if not event.buttons() & Qt.LeftButton:return
        if (event.position().toPoint()-self.origin).manhattanLength()<QApplication.startDragDistance():return
        mime=QMimeData();mime.setUrls([QUrl.fromLocalFile(str(file)),QUrl('https://example.com/new')])
        drag=QDrag(self);drag.setMimeData(mime)
        result['returnedCopyAction']=drag.exec(Qt.CopyAction|Qt.MoveAction,Qt.MoveAction)==Qt.CopyAction
source=Source();source.setWindowTitle('QAExternal:Source');source.resize(260,140)
layout=QVBoxLayout(source);label=QLabel('Drag to add a file and a link');label.setAttribute(Qt.WA_TransparentForMouseEvents);layout.addWidget(label)
def finish():
 try:
  saved=controller.launcher(launcher['Id'])
  children=saved['Items'][0]['Children']
  assert len(children)==4,children
  assert children[:2]==launcher['Items'][0]['Children']
  assert children[2]['Path']==str(file) and children[2]['SupportedOperatingSystems']==['linux']
  assert children[3]['Path']=='https://example.com/new' and children[3]['SupportedOperatingSystems']==[]
  assert file.read_text()=='untouched file'
  assert result.get('returnedCopyAction'),result
  result.update(passed=True,realNativePointerDrop=True,sourceFileUnchanged=True,hiddenMetadataPreserved=True)
 except Exception as error:result['error']=str(error)
 finally:
  print(json.dumps(result,indent=2),flush=True)
  probe.close();integration._call('org.kde.KWin','/Scripting','org.kde.kwin.Scripting.unloadScript',script_id)
  target.close();source.close();app.quit()
def begin():
 global worker
 row=probe.find('QAExternal:Source');receiver=probe.find('QAExternal:Target')
 if not row or not receiver:QTimer.singleShot(100,begin);return
 header=target.view.group_containers[(0,)].header
 point=header.mapTo(target,QPoint(header.width()//2,header.height()//2))
 start=(round(row['x']+row['width']/2),round(row['y']+row['height']/2))
 end=(round(receiver['x']+point.x()),round(receiver['y']+point.y()))
 worker=subprocess.Popen(['python3','/source/linux/dev/private_input.py','drag',*[str(value) for value in (*start,*end)]])
 def wait():
  if worker.poll() is None:QTimer.singleShot(100,wait)
  else:QTimer.singleShot(200,finish)
 QTimer.singleShot(200,wait)
target.show();source.show();QTimer.singleShot(600,begin)
app.exec()
assert result.get('passed') and 'error' not in result,result
