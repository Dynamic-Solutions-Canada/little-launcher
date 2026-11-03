"""Present representative native surfaces only inside the private KDE fixture."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import sys

from PySide6.QtCore import QPoint, QTimer
from PySide6.QtWidgets import QApplication
from PySide6.QtGui import QIcon

sys.path.insert(0, '/source/linux')
from little_launcher.controller import LauncherController
from little_launcher.desktop import DesktopIntegration, ICON, native_theme
from little_launcher.editor import ItemsEditor
from little_launcher.items_view import ItemButton
from little_launcher.settings import SettingsWindow

app = QApplication([])
app.setApplicationName('little-launcher')
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
app.setWindowIcon(QIcon(str(ICON)))
directory = Path(tempfile.mkdtemp(prefix='native-preview-', dir='/work'))
controller = LauncherController(directory)
launcher = {'Id': 'preview-shortcuts', 'Name': 'Everyday', 'ViewMode': 1, 'Items': [
    {'Name': 'Websites', 'Path': ''},
    {'Name': 'Example', 'Path': 'https://example.com', 'IsWebsite': True},
    {'Name': 'Windows editor', 'Path': 'C:\\Windows\\notepad.exe'},
    {'Name': 'Tools', 'IsGroup': True, 'Children': [
        {'Name': 'Files', 'Path': '/usr/bin/dolphin'},
        {'Name': 'Terminal', 'Path': '/usr/bin/konsole'}]},
    {'IsColumnBreak': True},
    {'Name': 'Linux tools', 'Path': ''},
    {'Name': 'Text editor', 'Path': '/usr/bin/kate'},
]}
controller.edit(lambda document: document['Launchers'].extend([deepcopy(launcher), {
    'Id': 'preview-web', 'Name': 'Example web', 'Kind': 1, 'WebUrl': 'https://example.com',
    'Items': [], 'WebBookmarks': []}]))
settings = SettingsWindow(controller, DesktopIntegration())
settings.navigation.setCurrentRow(1)
settings.show()
editor = None
if '--editor' in sys.argv:
    editor = ItemsEditor(controller, launcher, settings)
    editor.show()
    editor.view.move_requested.connect(lambda *args: print('MOVE ' + repr(args), flush=True))
(directory / 'theme.json').write_text(json.dumps(native_theme(), indent=2))
def geometry():
    if editor is None:
        return
    rows = []
    for button in editor.view.findChildren(ItemButton):
        point = button.mapTo(editor, QPoint(0, 0))
        rows.append({'coordinateSpace': 'client', 'path': button.path, 'x': point.x(), 'y': point.y(),
                     'width': button.width(), 'height': button.height()})
    (directory / 'buttons.json').write_text(json.dumps(rows, indent=2))

QTimer.singleShot(500, geometry)
print(str(directory), flush=True)
QTimer.singleShot(180000, app.quit)
raise SystemExit(app.exec())
