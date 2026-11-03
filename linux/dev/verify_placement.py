"""Observe only owned test windows in the private compositor; no host bus/display access."""
import asyncio
import json
import os
from pathlib import Path
import sys
import threading

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication
from dbus_next.aio import MessageBus
from dbus_next.service import ServiceInterface, method
from little_launcher.desktop import DesktopIntegration
from little_launcher.flyout import ShortcutFlyout

records = []
ready = threading.Event()
class Observer(ServiceInterface):
    def __init__(self):
        super().__init__('org.littlelauncher.Fixture')
    @method()
    def Record(self, value: 's'):
        records.append(json.loads(value))
async def observe():
    bus = await MessageBus().connect()
    bus.export('/Fixture', Observer())
    await bus.request_name('org.littlelauncher.Fixture')
    ready.set()
    await asyncio.Future()
threading.Thread(target=lambda: asyncio.run(observe()), daemon=True).start()
assert ready.wait(5)
app = QApplication([])
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
integration = DesktopIntegration()
script = Path('/work/placement-observer.js')
script.write_text('''function report(w) {
if (String(w.caption).indexOf('LittleLauncherFlyout:placement-test:') !== 0) return;
callDBus('org.littlelauncher.Fixture','/Fixture','org.littlelauncher.Fixture','Record',
JSON.stringify({caption:w.caption,x:w.frameGeometry.x,y:w.frameGeometry.y,width:w.frameGeometry.width,height:w.frameGeometry.height}));
}
workspace.windowAdded.connect(function(w){report(w);w.frameGeometryChanged.connect(function(){report(w);});});''')
integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.loadScript', str(script), 'll-placement-observer')
integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.start')
integration.enable()
launcher = {'Id': 'placement-test', 'Name': 'Placement', 'ViewMode': 1, 'Items': [{'Name': 'Test', 'Path': '/not-launched'}]}
native = '--native' in sys.argv
window = ShortcutFlyout(launcher, [900, 500, 'native'] if native else [900, 500])
window.show()
result = {}
def verify():
    try:
        assert records, 'No compositor observations'
        row = records[-1]
        x, y = (600, 500/1.5) if native else (900, 500)
        assert row['x'] == round(x - row['width']/2), row
        assert row['y'] == round(y - row['height'] - 8), row
        result.update(overflowAboveActualClick=True, nativeCoordinates=native, compositor=row)
    except Exception as error:
        result['error'] = str(error)
    finally:
        print(json.dumps(result, indent=2), flush=True)
        Path('/work/placement-result.json').write_text(json.dumps(result, indent=2)+'\n')
        window.close()
        integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.unloadScript', 'll-placement-observer')
        app.quit()
QTimer.singleShot(1000, verify)
app.exec()
assert 'error' not in result, result
