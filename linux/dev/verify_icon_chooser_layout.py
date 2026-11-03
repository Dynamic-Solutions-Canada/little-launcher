"""Compact native chooser, search and pending-selection parity on the isolated compositor."""
import json,os,sys,tempfile
from copy import deepcopy
from pathlib import Path
assert os.environ.get('XDG_RUNTIME_DIR')=='/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtGui import QColor,QImage
from PySide6.QtWidgets import QApplication,QScrollArea
from little_launcher.icon_picker import IconPicker
app=QApplication([]);root=Path(tempfile.mkdtemp(prefix='/work/chooser-layout-'))
original={'Id':'web-fixture','Kind':1,'WebHomeUrl':'https://example.com','TrayIconMode':'Blue','Future':7};before=deepcopy(original)
window=IconPicker(original,cache_directory=root/'icons');window.show();result={}
def verify():
 try:
  assert [window.tabs.tabText(i)for i in range(window.tabs.count())]==['Launcher presets','Symbols','Emoji','selfh.st']
  assert window.tabs.currentIndex()==0 and window.width()==420
  assert window.site_tab==0 and window.browse_button.isVisible()
  assert not any(mode=='Composite'for _,mode in window.tiles)
  assert window.grab().save('/work/icon-chooser-presets.png')
  window.search.setText('rocket');window.tabs.setCurrentIndex(2);app.processEvents()
  visible=[button for button,_ in window.tiles if button.isVisible()]
  assert len(visible)==1 and 'Rocket'in visible[0].accessibleName()
  visible[0].click();assert window.value()['TrayIconMode']=='Glyph:🚀'
  window.search.clear();window.tabs.setCurrentIndex(1);window.text.setText('A');window.set_color('#0078d4');window.choose('Glyph:\ue840')
  assert window.value()['TrayIconMode']=='Glyph:#0078d4:\ue840'
  app.processEvents()
  for button,color in window.swatches:
   if color:
    actual=button.grab().toImage().pixelColor(button.width()//2,button.height()//2)
    assert actual.name()==color,(actual.name(),color)
  assert window.grab().save('/work/icon-chooser-symbols.png')
  for i in (0,1,2):
   window.tabs.setCurrentIndex(i);app.processEvents();scroll=window.tabs.widget(i);assert isinstance(scroll,QScrollArea);assert scroll.widget().width()<=scroll.viewport().width()
  assert original==before
  window.reject();assert original==before and not list((root/'icons').glob('chosen-*'))
  result.update(passed=True,compactWindowsReferenceLayout=True,globalSearch=True,websiteAndBrowseInFirstTab=True,noWebComposite=True,symbolColorPreserved=True,actualNativeSwatchPixels=True,noHorizontalOverflow=True,cancelPreservesSource=True)
 except Exception as error:result['error']=str(error)
 finally:
  Path('/work/icon-chooser-layout.json').write_text(json.dumps(result,indent=2));print(json.dumps(result),flush=True);app.quit()
QTimer.singleShot(250,verify);app.exec();assert result.get('passed'),result
