"""Real native conflict-review UI, using synthetic accounts/files and an in-memory cloud."""
import base64,json,os,sys,tempfile,time
from copy import deepcopy
from pathlib import Path
from unittest.mock import patch
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer,QBuffer,QIODevice
from PySide6.QtGui import QImage,QColor
from PySide6.QtWidgets import QApplication,QMessageBox
from little_launcher.controller import LauncherController
from little_launcher.settings import SettingsWindow
from little_launcher.sync import fingerprint
from little_launcher.sync_page import Destination
from little_launcher.portable_icons import pack_icons
app=QApplication([]);root=Path(tempfile.mkdtemp(prefix='/work/sync-review-'));controller=LauncherController(root)
baseline={'Launchers':[{'Id':'side-browser-fixture','Name':'Side browser','Kind':1,'WebHomeUrl':'https://example.com','TrayIconMode':'Blue'}]}
controller.edit(lambda doc:doc.update(baseline));remote={**deepcopy(baseline),'PlatformVisibilityVersion':2};remote['Launchers'][0]['WebZoomPercent']=110
image=root/'selected.png';sample=QImage(32,32,QImage.Format_ARGB32);sample.fill(QColor('#16a765'));buffer=QBuffer();buffer.open(QIODevice.WriteOnly);sample.save(buffer,'PNG');raw=bytes(buffer.data());image.write_bytes(raw)
controller.edit(lambda doc:doc['Launchers'][0].update(TrayIconMode='Custom',CustomTrayIconPath=str(image)))
controller.preferences.set('OneDriveSync',{'SignedIn':True,'AccountName':'Synthetic account','Enabled':True,'Compatible':True,'CompatibilityVersion':2,'ETag':'old','Baseline':fingerprint(baseline)})
class Transport:
 writes=[]
 def account_state(self):return {'signedIn':True,'name':'Synthetic account'}
 def download(self):return deepcopy(remote),'cloud-fresh'
 def upload(self,document,etag):assert etag=='cloud-fresh';self.writes.append(deepcopy(document));return 'uploaded'
class Integration:
 enabled=False;available=True;supported=True;reason=''
 def refresh(self):pass
 def supports_launcher(self,identifier):return False
transport=Transport()
with patch('little_launcher.sync.OneDrive',return_value=transport):window=SettingsWindow(controller,Integration())
window.navigation.setCurrentRow(2);window.show();result={};phase=0;dialogs=0;deadline=time.monotonic()+15

def tick():
 global phase,dialogs
 try:
  if time.monotonic()>deadline:raise AssertionError('Native review did not complete')
  modal=QApplication.activeModalWidget()
  if modal is not None:
   assert isinstance(modal,QMessageBox)and modal.windowTitle()=='Sync conflict'
   assert 'Both copies changed'in modal.text()and'Side browser'in modal.informativeText()
   dialogs+=1
   if phase==1:
    assert modal.grab().save('/work/sync-review-dialog.png');modal.button(QMessageBox.Cancel).click();phase=2
   elif phase==3:
    next(button for button in modal.buttons()if button.text()=='Upload this device').click();phase=4
   else:raise AssertionError('Unexpected extra confirmation')
   return # Let the modal event loop unwind and commit its selected action.
  if phase==0 and window.sync.job is None:
   for destination in window.findChildren(Destination):assert not destination.expand.isChecked()and destination.body.isHidden()
   assert window.startup_control.isChecked();assert window.grab().save('/work/sync-destinations-collapsed.png')
   window.upload_cloud();phase=1
  elif phase==2 and window.sync.job is None:
   assert not transport.writes and window.sync._upload_review is None
   window.upload_cloud();phase=3
  elif phase==4 and window.sync.job is None:
   assert dialogs==2 and len(transport.writes)==1
   assert base64.b64decode(transport.writes[0]['Launchers'][0]['CustomTrayIconData'])==raw
   assert list((root/'backups').glob('before-onedrive-upload-*'))
   result.update(passed=True,oneReviewedConfirmationPerAttempt=True,cancelPreservesBoth=True,currentCloudRevisionUsed=True,newWebIconBytesUploaded=True,collapsedDestinations=True,startupDefaultOn=True)
   timer.stop();window.sync.timer.stop();window.sync.debounce.stop();window.close();app.quit()
 except Exception as error:
  import traceback;traceback.print_exc();result['error']=str(error)or'Native assertion failed';timer.stop();app.exit(1)
timer=QTimer();timer.timeout.connect(tick);timer.start(25);app.exec()
Path('/work/sync-review-result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result));assert result.get('passed'),result
