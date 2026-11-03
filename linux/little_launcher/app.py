"""Native application lifetime, single instance, and centralized launcher routing."""
from copy import deepcopy
import argparse
import os
from pathlib import Path
import sys

from PySide6.QtCore import QLockFile, QTimer
from PySide6.QtGui import QIcon
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import QApplication, QMessageBox

from .browser import BrowserHost
from .controller import LauncherController
from .desktop import DesktopIntegration, ICON, NativeThemeMonitor, native_tray_coordinates, set_autostart, register_appimage, prepare_desktop_environment
from .document import DocumentError, launcher_hidden, web_address
from .flyout import ShortcutFlyout
from .icons import LauncherIcons
from .settings import SettingsWindow
from .tray import TrayBus
from .web_preferences import valid_preference
from .positions import PlacementBridge
from .anchor_picker import AnchorPicker
from PySide6.QtWidgets import QDialog


class LauncherApplication:
    """Keep Settings reachable independently of tray-host support or filtered launchers."""

    def __init__(self, app, directory):
        self.app = app
        self.controller = LauncherController(directory)
        self.integration = DesktopIntegration()
        self.settings = SettingsWindow(self.controller, self.integration)
        self.browser = BrowserHost(directory)
        self.settings.page_icon_for = self.browser.page_icon
        self.extensions_dialog=None
        self.settings.extensions_requested.connect(self.open_extensions)
        self.browser.extension_settings_requested.connect(self.open_extensions)
        self.positions = PlacementBridge(self.controller)
        self.positions.start()
        self.browser.position_for = self.positions.get
        self.browser.position_supported = lambda identifier: self.integration.supports_launcher(identifier)
        self.app.aboutToQuit.connect(self.positions.close)
        self.theme_monitor = NativeThemeMonitor()
        self.app.installEventFilter(self.theme_monitor)
        self.theme_monitor.changed.connect(self.refresh)
        self.flyouts = {}
        self.quit_after_sync = False
        self.quit_after_update = False
        self.settings.sync.busy_changed.connect(self.sync_busy_changed)
        self.settings.updates.install_requested.connect(self.request_quit)
        self.settings.updates.busy_changed.connect(self.update_busy_changed)
        self.browser.quit_cancelled.connect(self.settings.updates.cancel_restart)
        self.tray = TrayBus()
        self.tray.events.activated.connect(lambda identifier, x, y: self.open_launcher(identifier, x, y, native=native_tray_coordinates()))
        self.tray.events.menu_action.connect(self.menu_action)
        self.tray.events.unavailable.connect(self.tray_unavailable)
        self.settings.open_requested.connect(self.open_launcher)
        self.settings.items_editor_opened.connect(self.park_shortcut_flyout)
        self.browser.settings_requested.connect(self.settings.configure)
        self.browser.section_requested.connect(lambda identifier, section: self.settings.configure(identifier, section=section))
        self.browser.app_settings_requested.connect(self.show_settings)
        self.browser.preference_requested.connect(self.web_preference)
        self.browser.size_requested.connect(self.web_size)
        self.browser.position_picker_requested.connect(self.position_picker)
        self.browser.bookmark_requested.connect(self.settings.add_bookmark)
        self.browser.bookmark_action_requested.connect(self.settings.bookmark_actions.handle)
        self.settings.profile_action_requested.connect(self.profile_action)
        self.browser.notice.connect(lambda message: QMessageBox.information(self.settings, 'Browser settings', message))
        self.browser.failed.connect(lambda message: QMessageBox.warning(self.settings, 'Web launcher', message))
        self.browser.quit_ready.connect(self.app.quit)
        self.controller.changed.connect(self.refresh)
        self.controller.local_changed.connect(self.refresh)
        self.app.aboutToQuit.connect(self.tray.close)
        self.refresh()

    def refresh(self):
        if hasattr(self.integration, 'sync_launchers'):
            self.integration.sync_launchers(self.controller.launchers)
        icons = []
        active = [item for item in self.controller.launchers if self.controller.enabled(item['Id'])]
        for launcher in active:
            if not launcher.get('NIconHide') and not launcher_hidden(launcher):
                icons.append({'id': launcher['Id'], 'name': launcher.get('Name', 'Launcher'),
                              'icon': '', 'pixmaps': LauncherIcons.pixmaps(LauncherIcons.resolve(launcher))})
        # An empty document still needs a visible route to create the first launcher.
        if not icons:
            icons.append({'id': 'settings', 'name': 'Little Launcher', 'icon': str(ICON)})
        self.tray.update(icons)
        self.browser.configure(active, disabled=set(item['Id'] for item in self.controller.launchers if not self.controller.enabled(item['Id'])))
        for identifier, flyout in list(self.flyouts.items()):
            if not self.controller.enabled(identifier) or self.controller.launcher(identifier) != flyout.launcher:
                launcher = self.controller.launcher(identifier)
                if launcher is None or not self.controller.enabled(identifier) or launcher.get('Kind', 0) == 1:
                    flyout.close()
                    self.flyouts.pop(identifier, None)
                else:
                    flyout.sync_launcher(launcher)

    def profile_action(self, identifier, action, expected_shared):
        launcher = self.controller.launcher(identifier)
        if launcher and launcher.get('Kind') == 1 and action in ('clear-data', 'reset-permissions'):
            if type(expected_shared) is not bool or launcher.get('WebSharedProfile', False) != expected_shared:
                self.controller.failed.emit('This launcher’s sign-in profile changed. Reopen settings before changing its data.')
                return
            self.browser.send({'type': 'profile-action', 'launcher': launcher, 'action': action, 'confirmed': True})

    def web_size(self, message):
        launcher = self.controller.launcher(message.get('id'))
        width, height = message.get('width'), message.get('height')
        allowed = False
        if launcher and launcher.get('Kind') == 1 and not launcher.get('WebLockSize') and valid_preference('WebFlyoutWidth', width) and valid_preference('WebFlyoutHeight', height):
            if width == launcher.get('WebFlyoutWidth') and height == launcher.get('WebFlyoutHeight'):
                allowed = True
            else:
                candidate = deepcopy(launcher)
                candidate.update(WebFlyoutWidth=width, WebFlyoutHeight=height)
                allowed = self.controller.replace_launcher(candidate, launcher)
        self.browser.send({'type': 'reply', 'token': message.get('token'), 'allowed': allowed})

    def position_picker(self, message):
        launcher=self.controller.launcher(message.get('id'))
        allowed=False
        if launcher and launcher.get('Kind')==1 and not (launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay')):
            original=deepcopy(launcher)
            picker=AnchorPicker(launcher.get('WebAnchor',0),self.settings,position_supported=self.integration.supports_launcher(launcher['Id']))
            if picker.exec()==QDialog.Accepted:
                candidate={**original,'WebAnchor':picker.selected}
                allowed=self.controller.replace_launcher(candidate,original)
        self.browser.send({'type':'reply','token':message.get('token'),'allowed':allowed})

    def web_preference(self, message):
        """Apply only known browser quick choices to the latest full document."""
        key, value = message.get('key'), message.get('value')
        launcher = self.controller.launcher(message.get('id'))
        allowed = False
        if launcher and launcher.get('Kind') == 1 and valid_preference(key, value) and not (key == 'WebAnchor' and value == 10 and not self.integration.supports_launcher(launcher['Id'])):
            candidate = deepcopy(launcher)
            candidate[key] = value
            allowed = self.controller.replace_launcher(candidate, launcher)
        self.browser.send({'type': 'reply', 'token': message.get('token'), 'allowed': allowed})

    def show_settings(self):
        self.settings.show()
        self.settings.raise_()
        self.settings.activateWindow()

    def tray_unavailable(self, message):
        self.settings.summary.setText(message)
        self.show_settings()

    def open_launcher(self, identifier, x=0, y=0, native=False):
        launcher = self.controller.launcher(identifier)
        if launcher is None:
            self.show_settings()
            return
        if not self.controller.enabled(identifier):
            self.settings.configure(identifier)
            return
        self.integration.refresh()
        anchor = [x, y] if self.integration.supports_launcher(identifier) and (x != 0 or y != 0) else None
        if anchor and native:
            anchor.append('native')
        if launcher.get('Kind', 0) == 1:
            if not web_address(launcher):
                self.settings.configure(identifier)
                return
            self.browser.show(launcher, anchor)
            return
        existing = self.flyouts.get(identifier)
        if existing and existing.isVisible():
            existing.hide()
            return
        if existing:
            existing.close()
        flyout = ShortcutFlyout(launcher, anchor, controller=self.controller,
                                focus_regular_editor=self.focus_regular_editor)
        flyout.app_settings_requested.connect(self.show_settings)
        flyout.website_requested.connect(self.browser.show_shortcut)
        flyout.edit_requested.connect(self.settings.edit_items)
        flyout.settings_requested.connect(lambda value, host=flyout: self.settings.configure(value, parent=host))
        self.flyouts[identifier] = flyout
        flyout.show()
        flyout.activateWindow()

    def park_shortcut_flyout(self, identifier):
        """Regular Settings-origin editing owns the launcher until that editor is finished."""
        existing = self.flyouts.get(identifier)
        if existing:
            existing.hide()

    def focus_regular_editor(self, identifier):
        """Reuse the existing regular editor when its launcher pencil is invoked again."""
        if identifier not in self.settings.editors:
            return False
        self.settings.edit_items(identifier)
        return True

    def menu_action(self, identifier, action):
        if action == 'open':
            self.open_launcher(identifier)
        elif action == 'edit':
            self.settings.edit_items(identifier)
        elif action == 'launcher-settings':
            self.settings.configure(identifier)
        elif action == 'settings':
            self.show_settings()
        elif action == 'quit':
            self.request_quit()

    def open_extensions(self,identifier=''):
        if self.extensions_dialog:
            self.extensions_dialog.show();self.extensions_dialog.raise_();return
        from .extensions_page import ExtensionsDialog
        dialog=ExtensionsDialog(self.controller,self.browser,identifier,self.settings)
        self.extensions_dialog=dialog
        dialog.finished.connect(lambda *_:setattr(self,'extensions_dialog',None))
        dialog.show()

    def request_quit(self):
        if self.extensions_dialog and self.extensions_dialog.job:
            self.extensions_dialog.quit_pending=True
            self.extensions_dialog.status.setText('Finishing extension preparation before quitting…')
            self.extensions_dialog.job.finished.connect(self.request_quit)
            return
        if self.settings.updates.job:
            self.quit_after_update=True
            self.settings.updates.cancel()
            return
        if self.settings.sync.job:
            self.quit_after_sync = True
            self.settings.sync.cancel()
            self.settings.sync_status.setText('Finishing the current sync request before quitting…')
        else:
            self.browser.request_quit()

    def update_busy_changed(self,busy):
        if self.quit_after_update and not busy:
            self.quit_after_update=False
            self.request_quit()

    def sync_busy_changed(self, busy):
        if self.quit_after_sync and not busy:
            self.quit_after_sync=False
            self.request_quit()



def main():
    """Start one owner per private data directory; second launch opens existing Settings."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--background', action='store_true')
    parser.add_argument('--data-dir', type=Path)
    parser.add_argument('--no-autostart', action='store_true', help='Development only: do not write an autostart entry')
    options = parser.parse_args()
    directory = options.data_dir or Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'little-launcher'
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    prepare_desktop_environment()
    if os.environ.get('XDG_SESSION_TYPE') == 'wayland':
        os.environ['QT_QPA_PLATFORM'] = 'wayland'
    app = QApplication(sys.argv[:1])
    app.setApplicationName('little-launcher')
    app.setWindowIcon(QIcon(str(ICON)))
    app.setQuitOnLastWindowClosed(False)
    lock = QLockFile(str(directory / 'instance.lock'))
    socket_name = str(directory / 'instance.sock')
    if not lock.tryLock(0):
        socket = QLocalSocket()
        socket.connectToServer(socket_name)
        if socket.waitForConnected(1000):
            socket.write(b'settings\n')
            socket.waitForBytesWritten(1000)
        return 0
    QLocalServer.removeServer(socket_name)
    server = QLocalServer()
    server.setSocketOptions(QLocalServer.UserAccessOption)
    if not server.listen(socket_name):
        QMessageBox.critical(None, 'Little Launcher', 'The private application control socket is unavailable.')
        return 1
    registration_error=None
    if not options.no_autostart:
        try:register_appimage()
        except (OSError,ValueError) as error:registration_error=str(error)
    app.setDesktopFileName('io.github.RyanEwen.LittleLauncher')
    try:
        owner = LauncherApplication(app, directory)
    except (OSError, ValueError) as error:
        QMessageBox.critical(None, 'Little Launcher', str(error))
        return 1

    def connection():
        socket = server.nextPendingConnection()
        owner.show_settings()
        socket.disconnectFromServer()
        socket.deleteLater()
    server.newConnection.connect(connection)
    if not options.no_autostart:
        try:
            set_autostart(owner.controller.preferences.data.get('StartAtLogin', True))
        except (OSError,ValueError) as error:
            QTimer.singleShot(0, lambda message=str(error): QMessageBox.warning(owner.settings, 'Login startup', message))
    if registration_error:
        QTimer.singleShot(0,lambda message=registration_error:QMessageBox.warning(owner.settings,'Application menu registration',message))
    if not options.background or not owner.controller.launchers:
        owner.show_settings()
    exit_code=app.exec()
    server.close()
    lock.unlock()
    return owner.settings.updates.finish_restart(exit_code)
