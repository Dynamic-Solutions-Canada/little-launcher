"""Render Windows-reference shortcut scenarios only in the private KDE fixture."""
import argparse
import json
from pathlib import Path
import os
import sys
import tempfile

from PySide6.QtCore import QTimer
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.controller import LauncherController
from little_launcher.desktop import DesktopIntegration, ICON
from little_launcher.editor import ItemsEditor
from little_launcher.flyout import ShortcutFlyout

parser = argparse.ArgumentParser()
parser.add_argument('--mode', type=int, choices=(0, 1, 2), default=0)
parser.add_argument('--surface', choices=('editor', 'flyout', 'edit'), default='editor')
options = parser.parse_args()
app = QApplication([])
app.setApplicationName('little-launcher')
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
app.setWindowIcon(QIcon(str(ICON)))
root = Path(tempfile.mkdtemp(prefix='shortcut-layout-', dir='/work'))
controller = LauncherController(root)

def site(name):
    return {'Name': name, 'Path': 'https://example.com', 'IsWebsite': True,
            'OpenInAppWindow': True, 'SupportedOperatingSystems': []}

launcher = {'Id': 'layout-test', 'Name': 'Everyday', 'ViewMode': options.mode,
            'IconModeIconsPerRow': 3, 'ShowTitle': True, 'Items': [
                {'Name': 'Work', 'IsGroup': True, 'Children': [site('Mail'), site('Calendar'), site('Tasks')]},
                {'Name': 'Personal', 'IsGroup': True, 'Children': [site('Notes'), site('Photos')]},
                site('Reference library'),
                {'Name': 'Hidden Windows entry', 'Path': 'C:\\Windows\\notepad.exe', 'Future': 9},
                {'IsColumnBreak': True},
                {'Name': 'Reference', 'IsGroup': True, 'Children': [site('Documentation'), site('Help')]},
            ]}
controller.edit(lambda document: document['Launchers'].append(launcher))
if options.surface == 'editor':
    window = ItemsEditor(controller, launcher)
else:
    DesktopIntegration().enable()
    window = ShortcutFlyout(launcher, [1100, 770], controller=controller)
    controller.changed.connect(lambda: window.sync_launcher(controller.launcher('layout-test')))
window.show()
if options.surface == 'edit':
    QTimer.singleShot(500, window.enter_edit)

def evidence():
    """Record widget extents and rendered geometry without inspecting unrelated host windows."""
    window.grab().save(str(root / 'window.png'))
    view = window.view
    result = {'mode': options.mode, 'surface': options.surface,
              'window': [window.width(), window.height()], 'view': [view.width(), view.height()],
              'cells': [{'path': list(row.path), 'kind': row.kind, 'rect': [row.x, row.y, row.width, row.height]}
                        for row in view.metrics.entries]}
    (root / 'geometry.json').write_text(json.dumps(result, indent=2))
    print(str(root), flush=True)
QTimer.singleShot(1000, evidence)
QTimer.singleShot(180000, app.quit)
raise SystemExit(app.exec())
