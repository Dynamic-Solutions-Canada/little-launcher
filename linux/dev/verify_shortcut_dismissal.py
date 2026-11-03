"""Real focus/click-away checks, including non-activating Done, in the private compositor."""
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
from little_launcher.flyout import ShortcutFlyout
from compositor_probe import CompositorProbe

app = QApplication([])
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
controller = LauncherController(Path(tempfile.mkdtemp(dir='/work')))
launcher = {'Id': 'dismiss-test', 'Name': 'Dismissal', 'Items': [{'Name': 'Test', 'Path': '/not-launched'}]}
controller.edit(lambda doc: doc['Launchers'].append(launcher))
integration = DesktopIntegration()
integration.enable()
probe = CompositorProbe(['Foreign click-away target', 'LittleLauncherFlyout:dismiss-test:', 'LittleLauncherToolbar:dismiss-test'], resource_class=None)
host_source = """
from PySide6.QtWidgets import QApplication,QMainWindow,QLabel
app=QApplication([]);app.setDesktopFileName('org.littlelauncher.ForeignFixture')
window=QMainWindow();window.setWindowTitle('Foreign click-away target')
window.setCentralWidget(QLabel('Click target'));window.resize(400,300);window.show();app.exec()
"""
host = subprocess.Popen(['python3', '-c', host_source])
flyout = ShortcutFlyout(launcher, [1100, 770], controller=controller)
result = {}
worker = None

def click(x, y, callback):
    global worker
    worker = subprocess.Popen(['python3', '/source/linux/dev/private_input.py', 'click', str(x), str(y)])
    def ready():
        if worker.poll() is None: QTimer.singleShot(100, ready)
        else: QTimer.singleShot(200, callback)
    QTimer.singleShot(100, ready)

def click_host(callback):
    row = probe.find('Foreign click-away target')
    assert row
    click(round(row['x'] + row['width']/2), round(row['y'] + row['height']/2), callback)

def open_normal():
    if not probe.find('Foreign click-away target'):
        QTimer.singleShot(100, open_normal);return
    flyout.show();QTimer.singleShot(400, check_initial_focus)

def check_initial_focus():
    result['activeOnOpen'] = flyout.isActiveWindow()
    click_host(check_dismissed)

def check_dismissed():
    result['normalClickAwayHides'] = not flyout.isVisible()
    flyout.show();QTimer.singleShot(400, enter_edit)

def enter_edit():
    flyout.enter_edit();QTimer.singleShot(300, lambda: click_host(check_editing))

def check_editing():
    result['editingClickAwayKeepsOpen'] = flyout.isVisible() and flyout.editing
    row = probe.find('LittleLauncherToolbar:dismiss-test')
    assert row
    button = flyout.toolbar.controls.buttons[-1]
    local = button.mapTo(flyout.toolbar, button.rect().center())
    click(round(row['x']) + local.x(), round(row['y']) + local.y(), after_done)

def after_done():
    result['doneKeepsSameFlyoutOpen'] = flyout.isVisible() and not flyout.editing
    result['doneRestoresOwnerActivation'] = flyout.isActiveWindow()
    click_host(finish)

def finish():
    result['clickAwayAfterDoneHides'] = not flyout.isVisible()
    print(json.dumps(result, indent=2), flush=True)
    Path('/work/shortcut-dismissal.json').write_text(json.dumps(result, indent=2)+'\n')
    flyout.hide();app.quit()
QTimer.singleShot(800, open_normal)
QTimer.singleShot(15000, app.quit)
app.exec()
host.terminate();host.wait(timeout=5)
probe.close()
assert result and all(result.values()), result
