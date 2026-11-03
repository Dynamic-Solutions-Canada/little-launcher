"""Actual native group drags in either shared host, inside the private compositor only."""
import argparse
from copy import deepcopy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QPoint, QTimer
from PySide6.QtWidgets import QApplication
from little_launcher.controller import LauncherController
from little_launcher.desktop import DesktopIntegration
from little_launcher.editor import ItemsEditor
from little_launcher.flyout import ShortcutFlyout
from compositor_probe import CompositorProbe

from little_launcher.item_button import ItemButton
from little_launcher.item_group import ItemGroup
if '--trace' in sys.argv:
    original_start = ItemButton.start_drag
    def trace_start(self):
        print('START', self.path, flush=True)
        return original_start(self)
    ItemButton.start_drag = trace_start
    original_move = ItemGroup.drag_move
    def trace_move(self, widget, event):
        original_move(self, widget, event)
        print('TARGET', self.path, self.intent, event.position().toTuple(), flush=True)
    ItemGroup.drag_move = trace_move
    original_drop = ItemGroup.drop
    def trace_drop(self, widget, event):
        print('DROP', self.path, event.position().toTuple(), flush=True)
        return original_drop(self, widget, event)
    ItemGroup.drop = trace_drop

parser = argparse.ArgumentParser()
parser.add_argument('--trace', action='store_true')
parser.add_argument('--capture-drag', action='store_true')
parser.add_argument('--mode', type=int, choices=(0, 1, 2), default=0)
parser.add_argument('--host', choices=('editor', 'flyout'), default='editor')
options = parser.parse_args()
app = QApplication([])
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
root = Path(tempfile.mkdtemp(prefix='group-drag-', dir='/work'))
controller = LauncherController(root)
launcher = {'Id': 'group-drag-test', 'Name': 'Group drag', 'ViewMode': options.mode, 'IconModeIconsPerRow': 3, 'Items': [
    {'Name': 'A', 'IsGroup': True, 'FutureGroup': 9, 'Children': [
        {'Name': 'First', 'Path': '/first'}, {'Name': 'Hidden', 'Path': 'C:\\hidden.exe', 'Future': 3},
        {'Name': 'Last', 'Path': '/last'}]},
    {'Name': 'B', 'IsGroup': True, 'Children': [{'Name': 'Other', 'Path': '/other'}]},
    {'Name': 'Loose', 'Path': '/loose'}]}
controller.edit(lambda doc: doc['Launchers'].append(deepcopy(launcher)))
probe = CompositorProbe('QAGroup:' if options.host == 'editor' else 'LittleLauncherFlyout:group-drag-test:')
if options.host == 'editor':
    window = ItemsEditor(controller, launcher)
    window.setWindowTitle('QAGroup:editor')
else:
    DesktopIntegration().enable()
    window = ShortcutFlyout(launcher, [1100, 770], controller=controller)
window.show()
result = {'mode': options.mode, 'host': options.host}
worker = None
first_drag = True

def drag(source, destination, callback):
    global worker, first_drag
    print('DRAG', source.toTuple(), destination.toTuple(), flush=True)
    command = ['python3', '/source/linux/dev/private_input.py', 'drag', str(source.x()), str(source.y()), str(destination.x()), str(destination.y())]
    if first_drag and options.capture_drag:
        command.append('1.2')
        def capture_drag():
            subprocess.run(['python3', '/source/linux/dev/capture_wayland.py', str(root/'drag.png')], check=True)
        QTimer.singleShot(800, capture_drag)
    first_drag = False
    worker = subprocess.Popen(command)
    def ready():
        if worker.poll() is None:
            QTimer.singleShot(100, ready)
        else:
            try:
                assert worker.returncode == 0
                callback()
            except Exception as error:
                result['error'] = str(error)
                finish()
    QTimer.singleShot(200, ready)

def position(widget, local):
    point = widget.mapTo(window, local)
    return QPoint(round(probe.latest['x']) + point.x(), round(probe.latest['y']) + point.y())

def items(): return controller.launcher('group-drag-test')['Items']
def group(index): return window.view.group_containers[(index,)]
def begin():
    if not probe.latest:
        QTimer.singleShot(100, begin)
        return
    if options.host == 'flyout': window.enter_edit()
    QTimer.singleShot(300, move_group)
def move_group():
    source, target = group(0), group(1)
    point = position(source.header, QPoint(20, source.header.height()//2))
    end = position(target, QPoint(target.width()//2, target.height()-16))
    drag(point, end, after_group)
def after_group():
    assert [item['Name'] for item in items()] == ['B','A','Loose'], items()
    assert items()[1]['FutureGroup'] == 9
    assert items()[1]['Children'][1]['Future'] == 3
    result['wholeGroupAcrossChildInterior'] = True
    owner = group(1)
    child = owner.child_buttons[-1]
    drag(position(child, child.rect().center()), position(owner, QPoint(owner.width()//2, owner.height()-2)), after_extract)
def after_extract():
    assert [item['Name'] for item in items()] == ['B','A','Last','Loose'], items()
    assert [item['Name'] for item in items()[1]['Children']] == ['First','Hidden']
    result['childThroughOuterBandBecomesSibling'] = True
    owner = group(1)
    source = next(button for button in window.view.findChildren(__import__('little_launcher.item_button',fromlist=['ItemButton']).ItemButton) if button.path == [3])
    drag(position(source, source.rect().center()), position(owner.header, QPoint(20, owner.header.height()//2)), after_append)
def after_append():
    assert [item['Name'] for item in items()] == ['B','A','Last'], items()
    assert [item['Name'] for item in items()[1]['Children']] == ['First','Hidden','Loose']
    assert items()[1]['Children'][1]['Future'] == 3
    result['headerInteriorAppendsToFullChildren'] = True
    result['hiddenMetadataPreserved'] = True
    finish()
def finish():
    window.grab().save(str(root/'window.png'))
    (root/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    print(str(root),json.dumps(result),flush=True)
    window.hide();app.quit()
QTimer.singleShot(500, begin)
QTimer.singleShot(15000, app.quit)
app.exec()
probe.close()
assert 'error' not in result and result.get('hiddenMetadataPreserved'), result
