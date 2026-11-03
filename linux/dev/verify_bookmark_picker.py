"""Synthetic native bookmark picker in the private KDE session only."""
import hashlib
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
from little_launcher.bookmark_picker import BookmarkPicker
from little_launcher.browser_bookmarks import BookmarkSource
from little_launcher.desktop import ICON
app=QApplication([]);app.setWindowIcon(QIcon(str(ICON)))
root=Path(tempfile.mkdtemp(prefix='bookmark-picker-',dir='/work'))
source=root/'Bookmarks'
source.write_text(json.dumps({'roots':{'bar':{'type':'folder','name':'Work','children':[
 {'type':'url','name':'Team wiki','url':'https://example.com/wiki'},
 {'type':'url','name':'Mail','url':'https://example.com/mail'}]}}}))
before=hashlib.sha256(source.read_bytes()).hexdigest()
window=BookmarkPicker(catalog=[BookmarkSource('Synthetic Edge','Default',source)])
result={}
def verify():
 try:
  assert window.list.topLevelItemCount()==2
  window.search.setText('wiki');assert window.list.topLevelItemCount()==1
  item=window.list.topLevelItem(0);window.list.setCurrentItem(item);item.setSelected(True)
  assert window.confirm.isEnabled()
  assert window.value()=={'Name':'Team wiki','Url':'https://example.com/wiki'}
  assert hashlib.sha256(source.read_bytes()).hexdigest()==before
  assert window.grab().save('/work/bookmark-picker.png')
  result.update(passed=True,searchAndSelection=True,sourceUnchanged=True)
 except Exception as error:result['error']=str(error)
 finally:
  print(json.dumps(result,indent=2),flush=True);window.reject();app.quit()
window.show();QTimer.singleShot(400,verify)
app.exec()
assert result.get('passed') and 'error' not in result,result
