"""Private inherited-pipe control of the isolated Electron browser process."""
import hashlib
import base64
from urllib.parse import urlsplit
import json
import os
from pathlib import Path

from PySide6.QtCore import QObject, QProcess, Qt, Signal
from PySide6.QtWidgets import QMessageBox

from .desktop import ROOT, native_theme
from .document import DocumentError, normalize_web_address, shows_bookmark_bar, web_address
from .icons import LauncherIcons
from .web_sessions import WebSessions


class BrowserHost(QObject):
    settings_requested = Signal(str)
    section_requested = Signal(str, str)
    app_settings_requested = Signal()
    extension_settings_requested = Signal(str)
    extensions_state = Signal(dict)
    preference_requested = Signal(dict)
    size_requested = Signal(dict)
    position_picker_requested = Signal(dict)
    bookmark_requested = Signal(str, str, str)
    bookmark_action_requested = Signal(dict)
    quit_ready = Signal()
    quit_cancelled = Signal()
    failed = Signal(str)
    notice = Signal(str)

    def __init__(self, directory, parent=None):
        super().__init__(parent)
        self.directory = directory
        self.sessions = WebSessions(directory)
        self.position_for = lambda identifier: None
        self.position_supported = lambda identifier: False
        self.session_warning_shown = False
        self.process = QProcess(self)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.finished.connect(self._finished)
        self.process.errorOccurred.connect(self._error)
        self.process.readyReadStandardError.connect(self._drain_stderr)
        self.buffer = b''
        self.pending = []
        self.ready = False
        self.quitting = False
        self.launchers = {}
        self.page_icons = {}
        self.activity = {}

    def _start(self):
        """Use a packaged engine or explicit development path, never an arbitrary shell."""
        engine = os.environ.get('LITTLE_LAUNCHER_ELECTRON', str(ROOT / 'browser/node_modules/electron/dist/electron'))
        if not Path(engine).is_file():
            self.failed.emit('The packaged Electron engine is missing. Set LITTLE_LAUNCHER_ELECTRON for development.')
            self.pending.clear()
            return
        self.process.setProgram(engine)
        arguments = [str(ROOT / 'browser/main.cjs'), '--launcher-data=' + str(self.directory / 'browser')]
        if os.environ.get('XDG_SESSION_TYPE') == 'wayland':
            arguments.append('--ozone-platform=wayland')
        if os.environ.get('APPIMAGE'):
            arguments.append('--disable-setuid-sandbox')
        preview = os.environ.get('LITTLE_LAUNCHER_PREVIEW_ID', '')
        if preview and all(character in '0123456789abcdef' for character in preview):
            arguments.append('--launcher-desktop-id=io.github.RyanEwen.LittleLauncher.Web.Preview.' + preview)
        self.process.setArguments(arguments)
        self.process.start()

    def send(self, message):
        """Queue until the browser is ready; all messages originate in trusted native UI."""
        if self.quitting:
            if self.process.state() == QProcess.NotRunning or message.get('type') not in ('quit', 'reply'):
                return
        window_icon = ''
        if message.get('type') in ('show', 'configure'):
            window_icon = LauncherIcons.window_png(message['launcher'])
        if isinstance(message.get('launcher'), dict):
            message = {**message, 'launcher': {key: value for key, value in message['launcher'].items() if key not in ('CustomTrayIconData', 'Items')}}
        if message.get('type') in ('show', 'configure'):
            launcher = message['launcher']
            try:
                address = normalize_web_address(web_address(launcher), allow_empty=True)
            except DocumentError as error:
                if message['type'] == 'show':
                    self.failed.emit(str(error))
                return
            message = {**message, 'presentation': {'homeUrl': address,
                       'windowIcon': window_icon, 'position': self.position_for(launcher['Id']),
                       'positionSupported': self.position_supported(launcher['Id']), 'session': self.sessions.get(launcher['Id']),
                       'showBookmarkBar': shows_bookmark_bar(launcher)}}
        if not self.ready:
            self.pending.append(message)
            if self.process.state() == QProcess.NotRunning:
                self._start()
            return
        self.process.write((json.dumps(message) + '\n').encode())

    def configure(self, launchers, disabled=frozenset()):
        previous = set(self.launchers)
        self.launchers = {item['Id']: item for item in launchers if item.get('Kind') == 1}
        removed = previous.difference(self.launchers)
        for identifier in removed:
            self.page_icons.pop(identifier, None)
        self.pending = [message for message in self.pending if message.get('launcher', {}).get('Id') not in removed]
        if removed and (self.ready or self.pending or self.process.state() != QProcess.NotRunning):
            self.send({'type': 'remove', 'ids': sorted(removed), 'confirmedIds': sorted(removed.intersection(disabled))})
        for launcher in self.launchers.values():
            if self.ready or launcher.get('WebHiddenPolicy') == 2:
                self.send({'type': 'configure', 'launcher': launcher, 'theme': native_theme()})

    def show(self, launcher, anchor):
        self.launchers[launcher['Id']] = launcher
        self.send({'type': 'show', 'launcher': launcher, 'anchor': anchor, 'theme': native_theme()})

    def show_shortcut(self, item):
        """Use a stable URL-private Linux session for the portable app-window preference."""
        identifier = 'shortcut-' + hashlib.sha256(item['Path'].encode()).hexdigest()[:32]
        launcher = {'Id': identifier, 'Name': item.get('Name') or 'Website', 'Kind': 1,
                    'WebUrl': item['Path'], 'WebRegularWindow': True, 'WebSharedProfile': False}
        self.show(launcher, None)

    def refresh_theme(self):
        """Update all live browser hosts, including URL app windows, without reconfiguring sites."""
        theme = native_theme()
        if self.ready:
            self.send({'type': 'theme', 'theme': theme})
        else:
            for message in self.pending:
                if message.get('type') in ('show', 'configure'):
                    message['theme'] = theme

    def request_quit(self):
        if self.process.state() == QProcess.NotRunning:
            self.quit_ready.emit()
        else:
            self.quitting = True
            self.send({'type': 'quit'})

    def _read(self):
        self.buffer += bytes(self.process.readAllStandardOutput())
        if len(self.buffer) > 2 * 1024 * 1024:
            self.buffer = b''
            self.failed.emit('The browser sent an oversized response.')
            return
        while b'\n' in self.buffer:
            line, self.buffer = self.buffer.split(b'\n', 1)
            try:
                message = json.loads(line)
            except (ValueError, UnicodeError):
                continue
            if isinstance(message, dict):
                self._message(message)

    def page_icon(self, identifier):
        cached = self.page_icons.get(identifier)
        launcher = self.launchers.get(identifier)
        if not cached or not launcher:
            return None
        try:
            if (urlsplit(cached[0]).scheme, urlsplit(cached[0]).netloc) != (urlsplit(normalize_web_address(web_address(launcher))).scheme, urlsplit(normalize_web_address(web_address(launcher))).netloc):
                return None
        except ValueError:
            return None
        return cached[1]

    def _message(self, message):
        kind = message.get('type')
        if kind == 'ready':
            self.ready = True
            pending, self.pending = self.pending, []
            for command in pending:
                self.send(command)
        elif kind == 'bookmark-icon':
            identifier,token,encoded=message.get('id'),message.get('token'),message.get('imageBase64')
            if identifier not in self.launchers or not isinstance(token,str) or len(token)>64 or not isinstance(encoded,str) or len(encoded)>350000:return
            try:
                from .site_icon import normalized_png
                png=normalized_png(base64.b64decode(encoded,validate=True))
                if png:self.send({'type':'bookmark-icon','id':identifier,'token':token,'png':base64.b64encode(png).decode()})
            except ValueError:pass
        elif kind == 'page-icon':
            identifier = message.get('id')
            address, encoded = message.get('url'), message.get('imageBase64')
            if encoded is None and isinstance(message.get('png'), str) and message['png'].startswith('data:image/png;base64,'):
                encoded = message['png'].split(',', 1)[1]
            launcher = self.launchers.get(identifier)
            if not launcher or not isinstance(address, str) or not isinstance(encoded, str) or len(encoded) > 350000:
                return
            try:
                origin = urlsplit(normalize_web_address(web_address(launcher)))
                actual = urlsplit(address)
                if actual.scheme != origin.scheme or actual.netloc != origin.netloc:
                    return
                from .site_icon import normalized_png
                png = normalized_png(base64.b64decode(encoded, validate=True))
                if png:
                    self.page_icons[identifier] = (address, png)
            except (ValueError, DocumentError):
                return
        elif kind == 'extensions-state':
            self.extensions_state.emit(message)
        elif kind == 'extension-settings':
            self.extension_settings_requested.emit(message.get('id', ''))
        elif kind == 'permission':
            label = self.launchers.get(message.get('id'), {}).get('Name', 'Website')
            permission = message.get('permission', 'unknown permission')
            explanation = f"{label}\n{message.get('origin', '')}\n\nAllow {permission}?"
            if permission == 'media':
                explanation += '\n\nThis page will stay protected from sleep until navigation or reload, even after capture ends.'
            box = QMessageBox(QMessageBox.Question, 'Website permission', explanation,
                              QMessageBox.Yes | QMessageBox.No)
            box.setTextFormat(Qt.PlainText)
            box.setDefaultButton(QMessageBox.No)
            allowed = box.exec() == QMessageBox.Yes
            self.send({'type': 'reply', 'token': message.get('token'), 'allowed': allowed})
        elif kind == 'close-launcher':
            box = QMessageBox(QMessageBox.Warning, 'Active website activity',
                'This launcher was removed. Closing its page may interrupt media or transfers. Close it now?',
                QMessageBox.Yes | QMessageBox.No)
            box.setTextFormat(Qt.PlainText)
            box.setDefaultButton(QMessageBox.No)
            self.send({'type': 'reply', 'token': message.get('token'),
                       'allowed': box.exec() == QMessageBox.Yes})
        elif kind == 'quit-blocked':
            allowed = QMessageBox.warning(None, 'Active website activity',
                'Quitting may interrupt media, camera/microphone access, sign-in windows, or downloads. Quit anyway?',
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No) == QMessageBox.Yes
            if allowed:
                self.send({'type': 'quit', 'confirmed': True})
            else:
                self.quitting = False
                self.quit_cancelled.emit()
        elif kind == 'session':
            identifier = message.get('id')
            if identifier not in self.launchers:
                return
            try:
                self.sessions.save(identifier, message.get('session'))
            except (DocumentError, OSError):
                if not self.session_warning_shown:
                    self.session_warning_shown = True
                    self.notice.emit('Browser tabs could not be saved on this device. Existing session data was preserved.')
        elif kind == 'activity':
            self.activity[message.get('id')] = message.get('busy', False)
        elif kind == 'bookmark-add':
            self.bookmark_requested.emit(message.get('id', ''), message.get('url', ''), message.get('title', ''))
            self.send({'type': 'reply', 'token': message.get('token'), 'allowed': True})
        elif kind == 'bookmark-action':
            self.bookmark_action_requested.emit(message)
            self.send({'type': 'reply', 'token': message.get('token'), 'allowed': True})
        elif kind == 'position-picker':
            self.position_picker_requested.emit(message)
        elif kind == 'web-size':
            self.size_requested.emit(message)
        elif kind == 'web-preference':
            self.preference_requested.emit(message)
        elif kind == 'app-settings':
            self.app_settings_requested.emit()
        elif kind == 'settings':
            self.section_requested.emit(message.get('id', ''), message.get('section', ''))
            if message.get('token'):
                self.send({'type': 'reply', 'token': message['token'], 'allowed': True})
        elif kind == 'notice':
            self.notice.emit(message.get('message', 'Browser settings updated.'))
        elif kind in ('error', 'warning'):
            self.failed.emit(message.get('message', 'The browser reported a problem.'))

    def _drain_stderr(self):
        # Browser diagnostics can contain private URLs; do not retain them or grow an idle buffer.
        self.process.readAllStandardError()

    def _error(self, error):
        if error == QProcess.FailedToStart:
            self.pending.clear()
            if self.quitting:
                self.quit_ready.emit()
                return
        if not self.quitting:
            self.failed.emit('The Electron browser could not be started.')

    def _finished(self, code, status):
        self.ready = False
        if self.quitting:
            self.quit_ready.emit()
        elif code != 0:
            self.failed.emit('The browser stopped unexpectedly. Local sign-ins have been retained.')
