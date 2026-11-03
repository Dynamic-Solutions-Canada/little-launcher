"""Pending Unicode/native icon chooser in the private compositor, with synthetic launcher data."""
from copy import deepcopy
import json
import os
from pathlib import Path
import sys
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication
from little_launcher.desktop import ICON
from little_launcher.icon_picker import IconPicker
app=QApplication([]);app.setWindowIcon(QIcon(str(ICON)))
original={'Id':'icon-picker-qa','TrayIconMode':'Glyph:#ff0000:A','CustomTrayIconPath':'','Future':7};before=deepcopy(original)
window=IconPicker(original)
result={}
def verify():
 try:
  assert window.text.text()=='A' and window.tabs.currentIndex()==1
  assert window.value()['TrayIconMode']=='Glyph:#ff0000:A'
  family='👨\u200d👩\u200d👧\u200d👦';window.choose('Glyph:'+family)
  assert window.value()['TrayIconMode']=='Glyph:'+family
  assert window.confirm.isEnabled() and original==before
  assert window.grab().save('/work/icon-picker.png')
  result.update(passed=True,currentSelectionRestored=True,fullEmojiSequencePreserved=True,pendingSelectionOnly=True)
 except Exception as error:result['error']=str(error)
 finally:
  window.reject();assert original==before
  print(json.dumps(result,indent=2),flush=True);app.quit()
window.show();QTimer.singleShot(400,verify)
app.exec();assert result.get('passed') and 'error' not in result,result
