"""Native shortcut icons, bookmark bulk selection and row context actions in private KDE."""
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QBuffer,QIODevice,QTimer,Qt
from PySide6.QtGui import QColor,QIcon,QImage
from PySide6.QtWidgets import QApplication,QVBoxLayout,QWidget
from little_launcher.desktop import ICON
from little_launcher.icon_picker import IconPicker
from little_launcher.bookmark_picker import BookmarkPicker
from little_launcher.browser_bookmarks import BookmarkSource
from little_launcher.app_picker import ApplicationList
from compositor_probe import CompositorProbe
import little_launcher.app_picker as app_picker
app=QApplication([]);app.setQuitOnLastWindowClosed(False);app.setWindowIcon(QIcon(str(ICON)))
root=Path(tempfile.mkdtemp(prefix='shortcut-parity-',dir='/work'))
image=QImage(32,32,QImage.Format_ARGB32);image.fill(QColor('#449911'));buffer=QBuffer();buffer.open(QIODevice.WriteOnly);image.save(buffer,'PNG');png=bytes(buffer.data())
requests=[]
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*_):pass
 def do_GET(self):
  requests.append((self.path,self.headers.get('Cookie'),self.headers.get('Authorization')))
  self.send_response(200);self.send_header('Content-Type','image/png');self.end_headers();self.wfile.write(png)
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
original={'Name':'Site','Path':f'http://127.0.0.1:{server.server_port}/page?ignored=1','IsWebsite':True,'IconGlyph':'A','IconColor':'#ff0000','Future':7}
chooser=IconPicker({},item=original,cache_directory=root/'icons');chooser.setWindowTitle('Shortcut icon fixture')
result={};probe=CompositorProbe('Application row fixture',resource_class=None)
window=QWidget();window.setWindowTitle('Application row fixture');window.resize(520,400);layout=QVBoxLayout(window)
rows=ApplicationList(entries=[{'Name':'Editor','Exec':'editor','_path':str(root/'editor.desktop')},{'Name':'Web app','Exec':'chromium --app-id=abc','_path':str(root/'web.desktop')}]);layout.addWidget(rows)
opened=[];app_picker.launch_item=lambda item:opened.append(item)
source=root/'Bookmarks';source.write_text(json.dumps({'roots':{'bar':{'type':'folder','name':'Bar','children':[{'type':'url','name':'Alpha','url':'https://alpha.example'},{'type':'url','name':'Beta','url':'https://beta.example'}]}}}));before=source.read_bytes()
picker=BookmarkPicker(catalog=[BookmarkSource('Chromium','Default',source)],multiple=True)
def finish(error=None):
 if result.get('finished'):return
 if error:result['error']=str(error)
 result['finished']=True
 print(json.dumps(result,indent=2),flush=True)
 probe.close();server.shutdown();chooser.reject();picker.reject();window.close();app.quit()
def icon_ready():
 try:
  assert chooser.site_png and not (root/'icons').exists()
  assert original['IconGlyph']=='A' and original['IconColor']=='#ff0000'
  chooser.tabs.setCurrentIndex(chooser.site_tab);assert chooser.grab().save('/work/shortcut-icon.png')
  chooser.accept();assert Path(chooser.value()['IconPath']).is_file()
  assert requests==[('/favicon.ico',None,None)]
  picker.show();QTimer.singleShot(200,bookmarks)
 except Exception as error:finish(error)
def bookmarks():
 try:
  picker.list.topLevelItem(0).setCheckState(0,Qt.Checked)
  picker.search.setText('Beta');assert len(picker.value())==2
  picker.search.clear();assert picker.grab().save('/work/bookmark-bulk.png');assert source.read_bytes()==before
  picker.reject();rows.list.setCurrentRow(0);window.show();QTimer.singleShot(600,right_click)
 except Exception as error:finish(error)
def right_click():
 try:
  geometry=probe.find('Application row fixture');assert geometry
  item=rows.list.item(1);point=rows.list.viewport().mapTo(window,rows.list.visualItemRect(item).center())
  subprocess.Popen(['python3','/source/linux/dev/private_input.py','right-click',str(int(geometry['x']+point.x())),str(int(geometry['y']+point.y()))])
  QTimer.singleShot(700,check_menu)
 except Exception as error:finish(error)
def check_menu():
 try:
  assert rows.context_menu.isVisible();assert rows.value()['Name']=='Editor'
  rows.context_menu.actions()[0].trigger();rows.context_menu.close()
  assert opened[0]['Name']=='Web app' and rows.value()['Name']=='Editor'
  rows.source.setCurrentIndex(2);assert rows.list.count()==1 and rows.value() is None
  result.update(passed=True,nativeIconPicker=True,publicFaviconPreview=True,confirmOnlyImageWrite=True,bulkFolderSelection=True,checksSurviveSearch=True,sourceFileUnchanged=True,realPointerContextMenu=True,contextActionsDoNotSelect=True,webAppSourceFilter=True)
  finish()
 except Exception as error:finish(error)
chooser.site_fetch.ready.connect(lambda *_:QTimer.singleShot(100,icon_ready))
chooser.show();QTimer.singleShot(100,chooser.fetch_site)
QTimer.singleShot(15000,lambda:finish('Native shortcut parity deadline'))
app.exec();assert result.get('passed') and 'error' not in result,result
