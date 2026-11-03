"""Run representative real application flows inside the private KDE fixture only."""
import json
from pathlib import Path
import sys
import tempfile

from PySide6.QtCore import QTimer
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.app import LauncherApplication
from little_launcher.desktop import ICON

app = QApplication([])
app.setApplicationName('little-launcher')
app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
app.setWindowIcon(QIcon(str(ICON)))
app.setQuitOnLastWindowClosed(False)
directory = Path(tempfile.mkdtemp(prefix='app-preview-', dir='/work'))
owner = LauncherApplication(app, directory)
owner.controller.edit(lambda document: document['Launchers'].extend([
    {'Id': 'preview-web', 'Name': 'Example web', 'Kind': 1, 'TrayIconMode': 'Blue',
     'WebHomeUrl': 'https://example.com', 'WebShowBookmarkBar': True,
     'WebBookmarks': [{'Name': 'Example', 'Url': 'https://example.com'},
                      {'Name': 'Docs', 'IsFolder': True, 'Children': [
                          {'Name': 'IANA', 'Url': 'https://www.iana.org/domains/reserved'}]}], 'Items': []},
    {'Id': 'preview-items', 'Name': 'Everyday', 'TrayIconMode': 'Green', 'ViewMode': 1,
     'Items': [{'Name': 'Example', 'Path': 'https://example.com', 'IsWebsite': True},
               {'Name': 'Windows editor', 'Path': 'C:\\Windows\\notepad.exe'}]},
]))
print(str(directory), flush=True)
if '--settings' in sys.argv:
    owner.settings.navigation.setCurrentRow(1)
    owner.show_settings()
else:
    owner.open_launcher('preview-web', 1100, 770)
QTimer.singleShot(300000, owner.browser.request_quit)
raise SystemExit(app.exec())
