"""Desktop-native Settings navigation with familiar cross-platform section order."""
from copy import deepcopy
import json
import os
from pathlib import Path

from PySide6.QtCore import QUrl, QSize, Qt, Signal, QSignalBlocker
from PySide6.QtGui import QDesktopServices, QFont, QIcon
from PySide6.QtWidgets import (
    QCheckBox, QDialog, QFileDialog, QFrame, QGroupBox, QHBoxLayout, QLabel, QListWidget, QMenu, QScrollArea, QToolButton,
    QMainWindow, QMessageBox, QPushButton, QSizePolicy, QStackedWidget, QStyle, QVBoxLayout, QWidget,
)

from .desktop import ICON, application_version, set_autostart
from .document import validate_document, DocumentError, launcher_hidden, launcher_supported, new_launcher, sync_envelope, web_address
from .editor import ItemsEditor
from .forms import BookmarkDialog, LauncherDialog
from .storage import atomic_write
from .portable_icons import pack_icons
from .bookmarks import BookmarkActions
from .icons import LauncherIcons
from .launcher_card import LauncherActionRow, LauncherCard, LauncherCardsColumn
from .update_controller import UpdateController
from .update_page import UpdateControls


class SettingsWindow(QMainWindow):
    extensions_requested = Signal(str)
    open_requested = Signal(str)
    items_editor_opened = Signal(str)
    profile_action_requested = Signal(str, str, object)

    def __init__(self, controller, integration):
        super().__init__()
        self.controller = controller
        self.integration = integration
        self.page_icon_for = lambda identifier: None
        self.bookmark_actions = BookmarkActions(controller, self)
        self.updates=UpdateController(controller,self)
        self.setWindowTitle('Little Launcher · Settings')
        self.resize(900, 700)
        body = QWidget()
        self.setCentralWidget(body)
        layout = QHBoxLayout(body)
        self.navigation = QListWidget()
        self.navigation.setFrameShape(QFrame.NoFrame)
        self.navigation.addItems(['Home', 'Launchers', 'Cloud sync', 'System', 'About'])
        for index in range(self.navigation.count()):
            self.navigation.item(index).setSizeHint(QSize(150, 40))
        self.navigation.setFixedWidth(180)
        self.navigation.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        layout.addWidget(self.navigation)
        self.pages = QStackedWidget()
        layout.addWidget(self.pages)
        self.navigation.currentRowChanged.connect(self.pages.setCurrentIndex)
        self._home()
        self._launchers()
        self._sync()
        self._system()
        self._about()
        self.navigation.setCurrentRow(0)
        self.controller.changed.connect(self.refresh)
        self.controller.local_changed.connect(self.refresh)
        self.controller.hidden_notice.connect(self.hidden_notice)
        self.controller.failed.connect(lambda text: QMessageBox.warning(self, 'Changes were not saved', text))
        self.editors = {}
        self.refresh()

    def _page(self, title):
        page = QWidget()
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        scroll = QScrollArea()
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setWidgetResizable(True)
        body = QWidget()
        body.setMaximumWidth(1000)
        layout = QVBoxLayout(body)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(8)
        heading = QLabel(title)
        font = heading.font()
        font.setPixelSize(24)
        font.setWeight(QFont.DemiBold)
        heading.setFont(font)
        layout.addWidget(heading)
        scroll.setWidget(body)
        outer.addWidget(scroll)
        self.pages.addWidget(page)
        return layout

    @staticmethod
    def _text(layout, text):
        label = QLabel(text)
        label.setWordWrap(True)
        layout.addWidget(label)
        return label

    @staticmethod
    def _button(layout, label, callback):
        button = QPushButton(label)
        button.clicked.connect(callback)
        layout.addWidget(button)
        return button

    def hidden_notice(self, text):
        from PySide6.QtCore import Qt
        box = QMessageBox(QMessageBox.Information, 'Launchers disabled on this system', text, QMessageBox.Ok, self)
        box.setTextFormat(Qt.PlainText)
        box.exec()

    def _home(self):
        layout = self._page('Little Launcher')
        self._text(layout, 'Home')
        launchers = LauncherActionRow('Launchers', 'Manage launchers & shortcuts')
        launchers.clicked.connect(lambda: self.navigation.setCurrentRow(1))
        layout.addWidget(launchers)
        cloud = LauncherActionRow('Cloud Sync', 'OneDrive, Google Drive, network share or SFTP')
        cloud.clicked.connect(lambda: self.navigation.setCurrentRow(2))
        layout.addWidget(cloud)
        self.summary = self._text(layout, '')
        layout.addStretch()

    def _launchers(self):
        page = QWidget()
        self.pages.addWidget(page)
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        scroll = QScrollArea()
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setFocusPolicy(Qt.NoFocus)
        scroll.setWidgetResizable(True)
        body = QWidget()
        body_layout = QVBoxLayout(body)
        body_layout.setContentsMargins(16, 16, 16, 16)
        self.launcher_content = LauncherCardsColumn()
        content = QVBoxLayout(self.launcher_content)
        content.setContentsMargins(0, 0, 0, 60)
        content.setSpacing(0)
        heading = QLabel('Launchers')
        font = QFont(heading.font())
        font.setPixelSize(24)
        font.setWeight(QFont.DemiBold)
        heading.setFont(font)
        content.addWidget(heading)
        content.addSpacing(8)
        description = QLabel('Each launcher has its own set of launcher items and its own tray icon.')
        description.setWordWrap(True)
        font = QFont(description.font())
        font.setPixelSize(13)
        description.setFont(font)
        content.addWidget(description)
        content.addSpacing(20)
        add = QPushButton('Add Launcher')
        add.setObjectName('add-launcher')
        add.setIcon(QIcon.fromTheme('list-add', self.style().standardIcon(QStyle.SP_FileIcon)))
        menu = QMenu(add)
        menu.addAction('Shortcuts', lambda: self.add(False))
        menu.addAction('Web page', lambda: self.add(True))
        add.setMenu(menu)
        content.addWidget(add, 0, Qt.AlignLeft)
        content.addSpacing(20)
        self.launcher_cards = QVBoxLayout()
        self.launcher_cards.setContentsMargins(0, 0, 0, 0)
        self.launcher_cards.setSpacing(8)
        content.addLayout(self.launcher_cards)
        body_layout.addWidget(self.launcher_content, 0, Qt.AlignHCenter | Qt.AlignTop)
        body_layout.addStretch()
        scroll.setWidget(body)
        outer.addWidget(scroll)

    @staticmethod
    def _item_count(items):
        return sum(SettingsWindow._item_count(item.get('Children', [])) if item.get('IsGroup') else 1
                   for item in items if not item.get('IsColumnBreak'))

    def _launcher_card(self, launcher):
        """Match LaunchersPage's icon header, Settings then Items, and sibling bulk action."""
        identifier = launcher['Id']
        card = LauncherCard()
        card.setObjectName('launcher-card-' + identifier)
        card.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Maximum)
        layout = QVBoxLayout(card)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(8)
        header = QHBoxLayout()
        header.setContentsMargins(0, 0, 0, 0)
        header.setSpacing(8)
        icon = QLabel()
        icon.setPixmap(LauncherIcons.resolve(launcher).pixmap(16, 16))
        icon.setFixedSize(16, 16)
        header.addWidget(icon)
        title = QLabel(launcher.get('Name', 'Launcher'))
        title.setTextFormat(Qt.PlainText)
        title.setEnabled(not launcher_hidden(launcher))
        title.setToolTip(title.text())
        title.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        font = QFont(title.font())
        font.setPixelSize(14)
        font.setWeight(QFont.DemiBold)
        title.setFont(font)
        header.addWidget(title, 1)
        controls = QHBoxLayout()
        controls.setSpacing(4)
        controls.setContentsMargins(0, 0, 0, 0)
        position = next(index for index, row in enumerate(self.controller.launchers) if row['Id'] == identifier)
        def action(name, theme, fallback, callback, enabled=True):
            button = QToolButton()
            button.setObjectName(name.lower().replace(' ', '-') + '-' + identifier)
            button.setAutoRaise(True)
            button.setIcon(QIcon.fromTheme(theme, self.style().standardIcon(fallback)))
            button.setIconSize(QSize(12, 12))
            button.setFixedSize(24, 24)
            button.setToolTip(name)
            button.setAccessibleName(name + ' ' + launcher.get('Name', 'Launcher'))
            button.setEnabled(enabled)
            button.clicked.connect(callback)
            controls.addWidget(button)
        action('Move up', 'go-up', QStyle.SP_ArrowUp, lambda: self.move_launcher(identifier, -1), position > 0)
        action('Move down', 'go-down', QStyle.SP_ArrowDown, lambda: self.move_launcher(identifier, 1), position < len(self.controller.launchers) - 1)
        action('Delete launcher', 'edit-delete', QStyle.SP_TrashIcon, lambda: self.remove_launcher(identifier))
        header.addLayout(controls)
        layout.addLayout(header)
        divider = QFrame()
        divider.setFrameShape(QFrame.HLine)
        divider.setFrameShadow(QFrame.Plain)
        divider.setFixedHeight(1)
        layout.addWidget(divider)
        address = web_address(launcher)
        subtitle = (address or 'No web address set') if launcher.get('Kind') == 1 else 'Name, icon, view mode, and more'
        if not self.controller.enabled(identifier):
            subtitle = ('Disabled on Linux · ' + launcher.get('Platform','windows').capitalize() + ' shortcut launcher') if launcher_hidden(launcher) else 'Disabled on this device · ' + subtitle
        settings = LauncherActionRow('Settings', subtitle)
        settings.setObjectName('launcher-settings-' + identifier)
        settings.setEnabled(not launcher_hidden(launcher))
        settings.clicked.connect(lambda: self.configure(identifier))
        layout.addWidget(settings)
        if launcher.get('Kind', 0) != 1:
            count = self._item_count(launcher.get('Items', []))
            subtitle = f"{count} item{'s' if count != 1 else ''}"
            if launcher_hidden(launcher):
                subtitle += ' · Disabled on Linux'
            items = LauncherActionRow('Items', subtitle)
            items.setObjectName('launcher-items-' + identifier)
            read_only = launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay')
            items.setEnabled(not read_only and not launcher_hidden(launcher))
            items.setAccessibleName('Edit items in ' + launcher.get('Name', 'Launcher'))
            items.clicked.connect(lambda: self.edit_items(identifier))
            row = QHBoxLayout()
            row.setContentsMargins(0, 0, 0, 0)
            row.setSpacing(0)
            row.addWidget(items, 1)
            more = QToolButton()
            more.setObjectName('launcher-bulk-' + identifier)
            more.setEnabled(not launcher_hidden(launcher))
            more.setAutoRaise(True)
            more.setText('…')
            more.setFixedSize(40, items.height())
            more.setToolTip('Item bulk actions')
            more.setAccessibleName('Item bulk actions for ' + launcher.get('Name', 'Launcher'))
            # A popup callback keeps the ellipsis free of the extra split-menu arrow.
            def bulk_menu():
                menu = QMenu(more)
                export = menu.addAction('Export items…', lambda: self.export_items(identifier))
                export.setEnabled(bool(launcher.get('Items')))
                imported = menu.addAction('Import items…', lambda: self.import_items(identifier))
                imported.setEnabled(not read_only)
                bookmarks=menu.addAction('Import browser bookmarks…',lambda:self.import_browser_bookmarks(identifier))
                bookmarks.setEnabled(not read_only)
                menu.exec(more.mapToGlobal(more.rect().bottomLeft()))
            more.clicked.connect(bulk_menu)
            row.addWidget(more)
            layout.addLayout(row)
        return card

    def move_launcher(self, identifier, delta):
        def move(document):
            launchers = document['Launchers']
            index = next((i for i, row in enumerate(launchers) if row['Id'] == identifier), -1)
            if index >= 0 and 0 <= index + delta < len(launchers):
                launchers.insert(index + delta, launchers.pop(index))
        self.controller.edit(move)

    def export_items(self, identifier):
        launcher = self.controller.launcher(identifier)
        if not launcher or launcher.get('Kind', 0) == 1:
            return
        path, _ = QFileDialog.getSaveFileName(self, 'Export Items', 'launcher-items.json', 'JSON files (*.json)')
        if path:
            try:
                atomic_write(Path(path), (json.dumps(launcher.get('Items', []), indent=2) + '\n').encode())
            except OSError as error:
                QMessageBox.warning(self, 'Export failed', str(error))

    def import_browser_bookmarks(self, identifier):
        from .bookmark_picker import BookmarkPicker
        from .shortcut_import import append_browser_bookmarks
        launcher=self.controller.launcher(identifier)
        if not launcher or launcher.get('Kind',0)!=0:return
        expected=deepcopy(launcher)
        if expected.get('IsShared') and not expected.get('IsSharedOwner') and not expected.get('SharedTwoWay'):return
        dialog=BookmarkPicker(self,multiple=True)
        if dialog.exec()==QDialog.Accepted:
            try:append_browser_bookmarks(self.controller,expected,dialog.value())
            except DocumentError as error:QMessageBox.warning(self,'Import failed',str(error))

    def import_items(self, identifier):
        launcher = self.controller.launcher(identifier)
        if not launcher or launcher.get('Kind', 0) == 1:
            return
        expected = deepcopy(launcher)
        path, _ = QFileDialog.getOpenFileName(self, 'Import Items', '', 'JSON files (*.json)')
        if not path:
            return
        try:
            imported = json.loads(Path(path).read_text())
            candidate = deepcopy(expected)
            candidate['Items'] = imported
            validate_document({'Launchers': [candidate]})
            if not imported:
                return
            choice = QMessageBox(self)
            choice.setWindowTitle('Import Items')
            choice.setText(f'Import {self._item_count(imported)} items?')
            replace = choice.addButton('Replace', QMessageBox.AcceptRole)
            append = choice.addButton('Append', QMessageBox.ActionRole)
            choice.addButton(QMessageBox.Cancel)
            choice.exec()
            if choice.clickedButton() is append:
                candidate['Items'] = deepcopy(expected.get('Items', [])) + imported
            elif choice.clickedButton() is not replace:
                return
            self.controller.replace_launcher(candidate, expected)
        except (OSError, ValueError) as error:
            QMessageBox.warning(self, 'Import failed', str(error))

    def _sync(self):
        from .sync import LauncherSync
        from .sync_page import CloudSyncPage
        self.sync = LauncherSync(self.controller)
        page = CloudSyncPage(self.sync, self.download_cloud, self.upload_cloud, self.import_file, self.export_file)
        self.pages.addWidget(page)
        self.sync_status = page.notice
        self.auto_sync = page.automatic
        self.sync.upload_review_ready.connect(self._review_upload)
        self.sync.manual_failed.connect(lambda message:QMessageBox.warning(self,'Sync did not complete',message))

    def download_cloud(self):
        if QMessageBox.question(self, 'Download launchers',
                'Replace this device’s launcher list with the OneDrive copy? A backup of the current data will be saved locally. Device preferences and browser profiles are kept.',
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No) == QMessageBox.Yes:
            self.sync.download()

    def upload_cloud(self):
        self.sync.prepare_upload()

    def _review_upload(self,review):
        dialog=QMessageBox(self);dialog.setWindowTitle('Sync conflict' if review['conflict'] else 'Sync launchers');dialog.setTextFormat(Qt.PlainText)
        download_only=review['action']=='download'
        dialog.setText('OneDrive has the latest changes. Download them to this device?' if download_only else
                       'Both copies changed since the last sync. Which copy do you want to keep?' if review['conflict'] else
                       'Upload this device’s latest launcher and icon changes to OneDrive?')
        details=f"{review['added']} added, {review['removed']} removed, {review['changed']} changed launchers between the copies."
        if review['iconChanges']:
            details+=f"\n{review['iconChanges']} icon differences: "+', '.join(review['iconNames'])
        details+='\n\nThe replaced copy will be backed up locally. Cancel keeps both copies.'
        dialog.setInformativeText(details)
        upload=None if download_only else dialog.addButton('Upload this device',QMessageBox.AcceptRole)
        download=dialog.addButton('Download OneDrive',QMessageBox.ActionRole)if download_only or review['conflict']else None
        cancel=dialog.addButton(QMessageBox.Cancel);dialog.setDefaultButton(cancel);dialog.setEscapeButton(cancel)
        dialog.exec()
        if upload is not None and dialog.clickedButton()is upload:self.sync.confirm_upload(review['token'])
        elif download is not None and dialog.clickedButton()is download:self.sync.confirm_upload(review['token'],use_cloud=True)
        else:self.sync.cancel_upload_review(review['token'])

    def _settings_row(self, layout, title, subtitle, control):
        from .launcher_settings import SettingsRow
        card = LauncherCard()
        content = QVBoxLayout(card)
        content.setContentsMargins(16, 16, 16, 16)
        content.addWidget(SettingsRow(title, subtitle, control))
        layout.addWidget(card)

    def _system(self):
        from PySide6.QtWidgets import QApplication, QComboBox
        layout = self._page('Settings')
        self._text(layout, 'Appearance')
        theme = QComboBox()
        theme.addItems(['System Default', 'Light', 'Dark'])
        theme.setFixedWidth(160)
        current = self.controller.preferences.data.get('AppTheme', 0)
        theme.setCurrentIndex(current if current in (0, 1, 2) else 0)
        def apply_theme(value, persist=True):
            scheme = (Qt.ColorScheme.Unknown, Qt.ColorScheme.Light, Qt.ColorScheme.Dark)[value]
            QApplication.instance().styleHints().setColorScheme(scheme)
            if persist:
                self.controller.preferences.set('AppTheme', value)
        apply_theme(theme.currentIndex(), False)
        theme.currentIndexChanged.connect(apply_theme)
        self._settings_row(layout, 'App Theme', 'Use the desktop appearance or choose light or dark', theme)
        start = QCheckBox()
        self.startup_control = start
        start.setChecked(self.controller.preferences.data.get('StartAtLogin', True))
        start.toggled.connect(self.autostart)
        self._settings_row(layout, 'Launch on Startup', 'Start minimized to the system tray', start)
        launchers = QPushButton('Manage Launchers')
        launchers.clicked.connect(lambda: self.navigation.setCurrentRow(1))
        self._settings_row(layout, 'Launcher appearance', 'Change each launcher’s tray icon, layout and behavior on the Launchers page.', launchers)
        self._text(layout, 'Desktop Integration')
        placement = QWidget()
        placement_layout = QVBoxLayout(placement)
        placement_layout.setContentsMargins(0, 0, 0, 0)
        placement_layout.setSpacing(8)
        self.integration_status = QLabel()
        self.integration_status.setWordWrap(True)
        placement_layout.addWidget(self.integration_status)
        self.integration_button = QPushButton('Enable tray positioning')
        self.integration_button.clicked.connect(self.enable_integration)
        placement_layout.addWidget(self.integration_button, 0, Qt.AlignRight)
        self._settings_row(layout, 'Open launchers beside their tray icons',
                           'Keep launcher windows next to the icon you click, rather than opening in the middle of the screen.', placement)
        self._text(layout, 'Browser Extensions')
        extensions=QPushButton('Manage Extensions…')
        extensions.clicked.connect(lambda:self.extensions_requested.emit(''))
        self._settings_row(layout,'Browser extensions','Add an extension ZIP or folder. Launchers using the same browser profile share extension data.',extensions)
        self._text(layout, 'Backup & Restore')
        exports = QWidget()
        from .item_toolbar import ActionFlowLayout
        actions = ActionFlowLayout(exports)
        for label, callback in [('Export Launchers…', self.export_file), ('Import Launchers…', self.import_file)]:
            button = QPushButton(label)
            button.clicked.connect(callback)
            actions.addWidget(button)
        self._settings_row(layout, 'Export / Import Launchers', 'Portable launcher settings; browser sessions and account tokens stay on this device', exports)
        layout.addStretch()

    def _about(self):
        layout = self._page('About')
        icon = QLabel()
        icon.setPixmap(QIcon(str(ICON)).pixmap(48, 48))
        self._settings_row(layout, 'Little Launcher', application_version() + ' · A customizable application and website launcher that lives in your desktop tray.', icon)
        built = QLabel('')
        self._settings_row(layout, 'Built With', 'Qt Widgets · PySide6 · Python · Electron · Chromium', built)
        repository = QPushButton('GitHub Repository')
        repository.clicked.connect(lambda: QDesktopServices.openUrl(QUrl('https://github.com/Dynamic-Solutions-Canada/little-launcher')))
        self._settings_row(layout, 'Source Code', 'View the project on GitHub', repository)
        self._settings_row(layout,'Updates','Check for and install verified Linux AppImage releases.',UpdateControls(self.updates))
        layout.addStretch()

    def refresh(self):
        while self.launcher_cards.count():
            entry = self.launcher_cards.takeAt(0)
            if entry.widget():
                entry.widget().deleteLater()
        hidden = 0
        for launcher in self.controller.launchers:
            hidden += int(launcher_hidden(launcher))
            self.launcher_cards.addWidget(self._launcher_card(launcher))
        if not self.controller.launchers:
            self._text(self.launcher_cards, 'No launchers yet. Choose Add launcher to create one.')
        self.summary.setText(f'{len(self.controller.launchers)} launchers. {hidden} disabled on Linux because they belong to another operating system.')
        self.integration.refresh()
        kde = 'KDE' in os.environ.get('XDG_CURRENT_DESKTOP', '').upper().split(':')
        preview = bool(os.environ.get('LITTLE_LAUNCHER_PREVIEW_ID'))
        if self.integration.enabled:
            self.integration_status.setText('On for this preview' if preview else 'On')
            self.integration_button.setText('Update tray positioning')
        elif kde:
            self.integration_status.setText('Off · launchers open as regular windows')
            self.integration_button.setText('Enable tray positioning')
        else:
            self.integration_status.setText('Not available on this desktop. Launchers open as regular windows.')
        self.integration_button.setEnabled(kde and not preview)
        self.integration_button.setToolTip('Enable window positioning for your user account. No administrator access is needed.' if kde else
                                         'Tray positioning is currently available on KDE Plasma.')

    def edit_items(self, identifier):
        launcher = self.controller.launcher(identifier)
        if not launcher or not launcher_supported(launcher):
            return
        if launcher.get('Kind', 0) == 1:
            self.configure(identifier)
            return
        if launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay'):
            QMessageBox.information(self, 'Read-only launcher', 'This subscribed launcher is managed by its owner.')
            return
        existing = self.editors.get(identifier)
        if existing:
            existing.show()
            existing.raise_()
            existing.activateWindow()
            return
        editor = ItemsEditor(self.controller, launcher, self)
        self.editors[identifier] = editor
        editor.settings_requested.connect(lambda value, host=editor: self.configure(value, parent=host))
        editor.finished.connect(lambda *_: self.editors.pop(identifier, None))
        editor.show()
        self.items_editor_opened.emit(identifier)

    def configure(self, identifier, parent=None, section=None):
        launcher = self.controller.launcher(identifier)
        if launcher and launcher_supported(launcher):
            dialog = LauncherDialog(launcher, parent or self, controller=self.controller, position_supported=self.integration.supports_launcher(identifier), page_icon=self.page_icon_for(identifier))
            dialog.profile_action_requested.connect(lambda action: self.profile_action_requested.emit(identifier, action, dialog.original.get('WebSharedProfile', False)))
            if section:
                dialog.show_section(section)
            if dialog.exec() == QDialog.Accepted:
                value = dialog.value()
                if value == dialog.original or self.controller.replace_launcher(value, dialog.original):
                    editor = self.editors.get(identifier)
                    if editor and value.get('Kind', 0) == 1:
                        editor.close()
                    elif editor:
                        from copy import deepcopy
                        editor.session.original = deepcopy(value)
                        editor.rebuild()

    def add(self, web):
        launcher = new_launcher('Web launcher' if web else 'Shortcuts', web)
        dialog = LauncherDialog(launcher, self)
        if dialog.exec() == QDialog.Accepted:
            value = dialog.value()
            if self.controller.edit(lambda document: document['Launchers'].append(value)) and value.get('Kind', 0) != 1:
                self.edit_items(value['Id'])

    def remove_launcher(self, identifier):
        launcher = self.controller.launcher(identifier)
        if launcher and QMessageBox.question(self, 'Remove launcher', 'Remove this launcher? Local browser sessions are retained.') == QMessageBox.Yes:
            self.controller.edit(lambda document: document['Launchers'].remove(launcher))

    def add_bookmark(self, identifier, address, title):
        launcher = self.controller.launcher(identifier)
        if launcher is None:
            return
        from copy import deepcopy
        original = deepcopy(launcher)
        dialog = BookmarkDialog(title, address, self)
        if dialog.exec() == QDialog.Accepted:
            value = deepcopy(original)
            value.setdefault('WebBookmarks', []).append(dialog.value())
            self.controller.replace_launcher(value, original)

    def import_file(self):
        path, _ = QFileDialog.getOpenFileName(self, 'Import launchers', '', 'JSON files (*.json)')
        if not path:
            return
        try:
            self.controller.import_document(json.loads(Path(path).read_text()))
        except (OSError, ValueError) as error:
            QMessageBox.warning(self, 'Import failed', str(error))

    def export_file(self):
        path, _ = QFileDialog.getSaveFileName(self, 'Export launchers', 'launchers.json', 'JSON files (*.json)')
        if path:
            try:
                atomic_write(Path(path), (json.dumps(sync_envelope(pack_icons(self.controller.document,LauncherIcons.glyph_png)), indent=2) + '\n').encode())
            except OSError as error:
                QMessageBox.warning(self, 'Export failed', str(error))

    def autostart(self, enabled):
        previous=self.controller.preferences.data.get('StartAtLogin',True)
        try:
            set_autostart(enabled)
            self.controller.preferences.set('StartAtLogin',enabled)
        except (OSError,ValueError) as error:
            try:set_autostart(previous)
            except (OSError,ValueError):pass
            with QSignalBlocker(self.startup_control):self.startup_control.setChecked(previous)
            QMessageBox.warning(self,'Login startup was not changed',str(error))

    def enable_integration(self):
        try:
            self.integration.enable()
        except OSError as error:
            QMessageBox.warning(self, 'Desktop integration', str(error))
        self.refresh()
