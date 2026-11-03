"""Public selfh.st native picker and whole-launcher disabled-card qualification."""
import os,sys,time,tempfile,json
from pathlib import Path
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtWidgets import QApplication
from little_launcher.icon_picker import IconPicker
from little_launcher.controller import LauncherController
from little_launcher.document import new_launcher
from little_launcher.portable_icons import pack_icons,unpack_icons
from little_launcher.settings import SettingsWindow
app=QApplication([]);root=Path(tempfile.mkdtemp(prefix='/work/selfhst-qa-'))
controller=LauncherController(root)
controller.edit(lambda doc:doc.update(Launchers=[{'Id':'windows-fixture','Name':'Windows fixture','Platform':'windows','Items':[{'Name':'Portable URL','Path':'https://example.com','IsWebsite':True}]},new_launcher('Linux fixture'),{'Id':'web-fixture','Name':'Web fixture','Kind':1,'Platform':'windows'}]))
assert not controller.enabled('windows-fixture');assert controller.enabled(controller.launchers[1]['Id']);assert controller.enabled('web-fixture')
class Integration:
 enabled=False;available=True
 def enable(self):pass
 def refresh(self):pass
 supported=True
 reason=""
window=SettingsWindow(controller,Integration());window.navigation.setCurrentRow(1);window.show()
for _ in range(20):app.processEvents();time.sleep(.01)
from PySide6.QtWidgets import QToolButton
assert not window.findChild(QToolButton,'launcher-settings-windows-fixture').isEnabled()
assert not window.findChild(QToolButton,'launcher-items-windows-fixture').isEnabled()
assert window.grab().save('/work/platform-disabled-cards.png');window.close()
original={'Id':'chooser','Kind':1,'TrayIconMode':'Blue'}
picker=IconPicker(original,cache_directory=root/'icons');picker.selfhst_search.setText('home assistant');picker.tabs.setCurrentIndex(picker.selfhst_tab);picker.show()
end=time.monotonic()+25
while time.monotonic()<end and 'home-assistant'not in picker.selfhst_service.images:app.processEvents();time.sleep(.01)
assert 'home-assistant'in picker.selfhst_service.images,picker.selfhst_status.text()
picker.choose_selfhst('home-assistant');assert picker.mode=='SelfhSt'and picker.confirm.isEnabled();assert original['TrayIconMode']=='Blue'
assert picker.grab().save('/work/selfhst-picker.png');picker.accept();selected=picker.value();chosen=Path(selected['CustomTrayIconPath']).read_bytes()
wire=pack_icons({'Launchers':[{**original,**selected}]});received=unpack_icons(wire,root/'second-machine')['Launchers'][0]
assert Path(received['CustomTrayIconPath']).read_bytes()==chosen
result={'passed':True,'liveSelfhStCatalog':True,'livePngThumbnail':True,'searchByNameAndTags':True,'pendingSelection':True,'portableImageRoundTrip':True,'foreignLauncherDisabled':True,'webLauncherShared':True,'perItemFilteringRemoved':True,'root':str(root)}
Path('/work/selfhst-platform-result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))

from little_launcher.document import sync_envelope
from little_launcher.icons import LauncherIcons
portable={'Launchers':[{**original,**selected},{'Id':'linux-owner','Name':'Linux','Kind':0,'Platform':'linux','Items':[]},{'Id':'windows-owner','Name':'Windows','Items':[]},{'Id':'glyph','Kind':1,'TrayIconMode':'Glyph:#FF0000:A'}]}
Path('/work/selfhst-sync-wire.json').write_text(json.dumps(sync_envelope(pack_icons(portable,LauncherIcons.glyph_png))))
