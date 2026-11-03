"""Native gallery pending selection, cache bounds and portable selected images."""
import base64,json,tempfile,unittest
from pathlib import Path
try:
 from PySide6.QtWidgets import QApplication
 from PySide6.QtCore import QBuffer,QIODevice
 from PySide6.QtGui import QImage,QColor
 from little_launcher.icon_picker import IconPicker
 from little_launcher.selfhst import catalog,raster
 from little_launcher.portable_icons import pack_icons,unpack_icons
 QT=True
except ImportError:QT=False
@unittest.skipUnless(QT,'Native icon gallery requires Qt')
class SelfhStTests(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.app=QApplication.instance()or QApplication([])
  image=QImage(32,32,QImage.Format_ARGB32);image.fill(QColor('#1ab278'));buffer=QBuffer();buffer.open(QIODevice.WriteOnly);image.save(buffer,'PNG');cls.png=bytes(buffer.data())
 def test_catalog_rejects_unsafe_references_and_bounds_input(self):
  self.assertEqual(catalog(json.dumps([{'Name':'Safe','Reference':'safe'},{'Name':'Bad','Reference':'../outside'}]).encode()),[{'Name':'Safe','Reference':'safe','Tags':'','Category':''}])
  for raw in [b'{}',b'[]',b'x'*(3*1024*1024+1)]:
   with self.assertRaises(ValueError):catalog(raw)
  self.assertEqual(raster(self.png),self.png);self.assertIsNone(raster(b'<svg/>'));self.assertIsNone(raster(b'x'*(256*1024+1)))
 def test_gallery_search_cancel_confirm_and_two_machine_sync(self):
  with tempfile.TemporaryDirectory()as temporary:
   root=Path(temporary);cache=root/'icons'/'selfhst';cache.mkdir(parents=True)
   entries=[{'Name':'Home Assistant','Reference':'home-assistant','Tags':'automation','Category':'Self-Hosted'},{'Name':'Other','Reference':'other','Tags':'','Category':''}]
   (cache/'index.json').write_text(json.dumps(entries))
   for entry in entries:(cache/(entry['Reference']+'.png')).write_bytes(self.png)
   original={'Id':'icon','Kind':1,'TrayIconMode':'Blue'}
   picker=IconPicker(original,cache_directory=root/'icons');picker.tabs.setCurrentIndex(picker.selfhst_tab);self.app.processEvents()
   picker.selfhst_search.setText('automation');picker.selfhst_filter();self.assertEqual(list(picker.selfhst_buttons),['home-assistant'])
   picker.choose_selfhst('home-assistant');self.assertTrue(picker.confirm.isEnabled());self.assertEqual(original['TrayIconMode'],'Blue');picker.reject()
   self.assertEqual(list((root/'icons').glob('selfhst-*.png')),[])
   picker=IconPicker(original,cache_directory=root/'icons');picker.tabs.setCurrentIndex(picker.selfhst_tab);picker.choose_selfhst('home-assistant')
   picker.choose('Red');picker.selfhst_icon_ready('home-assistant',self.png);self.assertEqual(picker.mode,'Red','Late thumbnail arrival cannot replace a newer choice')
   picker.choose_selfhst('home-assistant');picker.accept();selected=picker.value();self.assertEqual(selected['TrayIconMode'],'Custom')
   launcher={**original,**selected}
   expanded=[{'Name':f'A {index:03d}','Reference':f'fixture-{index}','Tags':'','Category':''}for index in range(140)]+entries
   (cache/'index.json').write_text(json.dumps(expanded))
   reopened=IconPicker(launcher,cache_directory=root/'icons')
   self.assertEqual(reopened.tabs.currentIndex(),reopened.selfhst_tab)
   self.assertEqual(reopened.selfhst_selected,'home-assistant')
   self.assertLessEqual(len(reopened.selfhst_buttons),120)
   self.assertTrue(reopened.selfhst_buttons['home-assistant'].isChecked())
   self.assertEqual(reopened.value(),selected);reopened.reject()
   wire=pack_icons({'Launchers':[launcher]});self.assertEqual(base64.b64decode(wire['Launchers'][0]['CustomTrayIconData']),self.png)
   other=unpack_icons(wire,root/'second-machine')['Launchers'][0];self.assertEqual(Path(other['CustomTrayIconPath']).read_bytes(),self.png)
   self.assertNotEqual(other['CustomTrayIconPath'],launcher['CustomTrayIconPath']);self.assertEqual(pack_icons({'Launchers':[other]})['Launchers'][0]['CustomTrayIconData'],wire['Launchers'][0]['CustomTrayIconData'])
 def test_foreign_font_preview_is_preserved_without_replacing_saved_glyph(self):
  source={'Id':'glyph','TrayIconMode':'Glyph:#FFC83D:\ue728','TrayIconPreviewMode':'Glyph:#FFC83D:\ue728','TrayIconPreviewData':base64.b64encode(self.png).decode()}
  from little_launcher.icons import LauncherIcons
  self.assertFalse(LauncherIcons.resolve(source).isNull())
  wire=pack_icons({'Launchers':[source]})['Launchers'][0];self.assertEqual(wire,source)

 def test_old_peer_cannot_erase_available_local_custom_or_font_preview(self):
  from little_launcher.portable_icons import preserve_icons
  with tempfile.TemporaryDirectory()as temporary:
   path=Path(temporary)/'original.png';path.write_bytes(self.png)
   existing={'Id':'same','TrayIconMode':'Custom','CustomTrayIconPath':str(path)}
   merged=preserve_icons(existing,{'Id':'same','TrayIconMode':'Custom','CustomTrayIconPath':'C:\\foreign.png'})
   from little_launcher.sync import apply_remote
   result=apply_remote({'Launchers':[existing]},{'Launchers':[{'Id':'same','TrayIconMode':'Custom','CustomTrayIconPath':'C:\\foreign.png'}]})
   self.assertEqual(result['Launchers'][0]['CustomTrayIconPath'],str(path))
   self.assertEqual(merged['CustomTrayIconPath'],str(path));self.assertEqual(path.read_bytes(),self.png)
   glyph={'Id':'same','TrayIconMode':'Glyph:A','TrayIconPreviewMode':'Glyph:A','TrayIconPreviewData':base64.b64encode(self.png).decode()}
   self.assertEqual(preserve_icons(glyph,{'Id':'same','TrayIconMode':'Glyph:A'})['TrayIconPreviewData'],glyph['TrayIconPreviewData'])
   self.assertNotIn('TrayIconPreviewData',preserve_icons(glyph,{'Id':'same','TrayIconMode':'Red'}))
