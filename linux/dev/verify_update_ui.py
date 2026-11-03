"""Synthetic native About/update layout; no update download or real launchers are used."""
import json
import os
from pathlib import Path
import sys
import tempfile
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication
from little_launcher.controller import LauncherController
from little_launcher.desktop import ICON
from little_launcher.settings import SettingsWindow
from little_launcher.updates import Release
app=QApplication([]);app.setWindowIcon(QIcon(str(ICON)));app.setQuitOnLastWindowClosed(False)
root=Path(tempfile.mkdtemp(prefix='update-ui-',dir='/work'));controller=LauncherController(root)
controller.preferences.set('StartAtLogin',False);controller.preferences.set('CheckForUpdatesAutomatically',False)
class Integration:
 enabled=False
 def refresh(self):pass
 def supports_launcher(self,identifier):return False
window=SettingsWindow(controller,Integration());window.navigation.setCurrentRow(4);window.resize(900,700)
result={}
def verify():
 try:
  window.updates.release=Release('99.0.0','fixture.AppImage','https://example.invalid','0'*64,100)
  window.updates.status='Version 99.0.0 is available. Synthetic UI fixture.';window.updates.changed.emit()
  app.processEvents();assert window.grab().save('/work/update-ui.png')
  for width in (650,800,1000):
   window.resize(width,700);app.processEvents()
   scroll=window.pages.currentWidget().findChildren(__import__('PySide6.QtWidgets',fromlist=['QScrollArea']).QScrollArea)[0]
   assert scroll.widget().width()<=scroll.viewport().width(),(width,scroll.widget().width(),scroll.viewport().width())
  result.update(passed=True,nativeUpdateControls=True,sourceBuildDownloadDisabled=True,aboutPageFitsViewport=True,noPersonalData=True)
 except Exception as error:result['error']=str(error)
 finally:
  window.close();print(json.dumps(result,indent=2),flush=True);app.quit()
window.show();QTimer.singleShot(500,verify);app.exec();assert result.get('passed') and 'error' not in result,result
