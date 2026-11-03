"""Open an explicitly authorized real-desktop preview with disposable application data.

Unlike the private fixture drivers, this uses the current user session. It never
installs desktop integration or changes login startup. Quit through its tray menu.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
from uuid import uuid4

from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.app import LauncherApplication
from little_launcher.desktop import ICON, prepare_desktop_environment


def main():
    """Present representative native UI while keeping all launcher/browser writes disposable."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--engine', required=True, type=Path)
    parser.add_argument('--data-dir', type=Path)
    parser.add_argument('--temporary-integration', action='store_true',
                        help='Requires explicit authorization to load the scoped, unsaved preview helper')
    options = parser.parse_args()
    if not options.engine.is_file():
        parser.error('The project Electron engine is missing.')
    directory = options.data_dir or Path(tempfile.mkdtemp(prefix='little-launcher-desktop-preview-'))
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.environ['LITTLE_LAUNCHER_ELECTRON'] = str(options.engine.resolve())
    preview_id = hashlib.sha256(str(directory.resolve()).encode()).hexdigest()[:16]
    os.environ['LITTLE_LAUNCHER_PREVIEW_ID'] = preview_id

    prepare_desktop_environment()
    app = QApplication([])
    app.setApplicationName('little-launcher')
    app.setDesktopFileName('io.github.RyanEwen.LittleLauncher.Preview.' + preview_id)
    app.setWindowIcon(QIcon(str(ICON)))
    app.setQuitOnLastWindowClosed(False)
    owner = LauncherApplication(app, directory)
    owner.tray.events.activated.connect(lambda identifier, x, y: print(json.dumps(
        {'event': 'preview-tray-activation', 'id': identifier, 'x': x, 'y': y}), flush=True))
    owner.controller.preferences.set('StartAtLogin', False)
    if not owner.controller.launchers:
        suffix = uuid4().hex[:8]
        web_id = 'preview-web-' + suffix
        owner.controller.edit(lambda document: document['Launchers'].extend([
            {'Id': web_id, 'Name': 'Preview: Web', 'Kind': 1, 'TrayIconMode': 'Blue',
             'WebHomeUrl': 'https://example.com', 'WebShowBookmarkBar': True,
             'WebBookmarks': [{'Name': 'Example', 'Url': 'https://example.com'},
                              {'Name': 'Reference', 'IsFolder': True, 'Children': [
                                  {'Name': 'IANA', 'Url': 'https://www.iana.org/domains/reserved'}]}],
             'Items': []},
            {'Id': 'preview-items-' + suffix, 'Name': 'Preview: Shortcuts', 'TrayIconMode': 'Green',
             'ViewMode': 0, 'ShowTitle': True, 'Items': [
                 {'Name': 'Web', 'IsGroup': True, 'Children': [
                     {'Name': name, 'Path': url, 'IsWebsite': True, 'OpenInAppWindow': True,
                      'SupportedOperatingSystems': []}
                     for name, url in [('Example', 'https://example.com'), ('IANA', 'https://www.iana.org/domains/reserved')]
                 ]},
                 {'Name': 'Local', 'IsGroup': True, 'Children': [
                     {'Name': 'Terminal', 'Path': 'org.kde.konsole.desktop', 'SupportedOperatingSystems': ['linux']},
                 ]},
                 {'Name': 'Windows-only example', 'Path': 'C:\\Windows\\notepad.exe',
                  'SupportedOperatingSystems': ['windows']},
             ]},
        ]))
    # These controls write machine-wide user choices, rather than the disposable document.
    if options.temporary_integration:
        from preview_integration import TemporaryPreviewIntegration
        integration = TemporaryPreviewIntegration(directory, owner.controller.launchers)
        integration.load()
        owner.integration = integration
        owner.settings.integration = integration
        app.aboutToQuit.connect(integration.close)
        owner.settings.refresh()
    owner.settings.startup_control.setEnabled(False)
    owner.settings.integration_button.setEnabled(False)
    owner.settings.setWindowTitle('Little Launcher · Temporary preview')
    owner.settings.statusBar().showMessage('Temporary data. Login startup and desktop setup are disabled in this preview.')
    owner.settings.navigation.setCurrentRow(1)
    owner.show_settings()
    print(str(directory), flush=True)
    return app.exec()


if __name__ == '__main__':
    raise SystemExit(main())
