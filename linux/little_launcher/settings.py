"""Desktop-native Settings navigation with familiar cross-platform section order."""
from copy import deepcopy
import json
import os
from pathlib import Path

from PySide6.QtCore import QUrl, QSize, Qt, Signal, QSignalBlocker
from PySide6.QtGui import QDesktopServices, QFont, QIcon
from PySide6.QtWidgets import (
    QCheckBox, QDialog, QFileDialog, QFrame, QGroupBox, QHBoxLayout, QLabel, QListWidget, QMenu, QScrollArea, QToolButton,
    QMainWindow, QGraphicsOpacityEffect, QMessageBox, QPushButton, QSizePolicy, QStackedWidget, QStyle, QVBoxLayout, QWidget,
)

from . import i18n
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
        self.setWindowTitle(i18n.t('settings.windowTitle'))
        self.resize(900, 700)
        body = QWidget()
        self.setCentralWidget(body)
        layout = QHBoxLayout(body)
        self.navigation = QListWidget()
        self.navigation.setFrameShape(QFrame.NoFrame)
        self.navigation.addItems([i18n.t(key) for key in ('home.title', 'launchers.title', 'sync.title', 'settings.nav.system', 'about.title')])
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
        self.controller.failed.connect(lambda text: QMessageBox.warning(self, i18n.t('settings.changesNotSaved'), text))
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
        box = QMessageBox(QMessageBox.Information, i18n.t('launchers.hiddenNotice.title'), text, QMessageBox.Ok, self)
        box.setTextFormat(Qt.PlainText)
        box.exec()

    def _home(self):
        layout = self._page('Little Launcher')
        self._text(layout, i18n.t('home.title'))
        launchers = LauncherActionRow(i18n.t('launchers.title'), i18n.t('home.launchersCard.description'))
        launchers.clicked.connect(lambda: self.navigation.setCurrentRow(1))
        layout.addWidget(launchers)
        cloud = LauncherActionRow(i18n.t('sync.title'), i18n.t('home.syncCard.description'))
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
        heading = QLabel(i18n.t('launchers.title'))
        font = QFont(heading.font())
        font.setPixelSize(24)
        font.setWeight(QFont.DemiBold)
        heading.setFont(font)
        content.addWidget(heading)
        content.addSpacing(8)
        description = QLabel(i18n.t('launchers.description'))
        description.setWordWrap(True)
        font = QFont(description.font())
        font.setPixelSize(13)
        description.setFont(font)
        content.addWidget(description)
        content.addSpacing(20)
        add = QPushButton(i18n.t('launchers.addButton'))
        add.setObjectName('add-launcher')
        add.setIcon(QIcon.fromTheme('list-add', self.style().standardIcon(QStyle.SP_FileIcon)))
        menu = QMenu(add)
        menu.addAction(i18n.t('launchers.addMenu.shortcuts'), lambda: self.add(False))
        menu.addAction(i18n.t('launchers.addMenu.webPage'), lambda: self.add(True))
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
        enabled_here = self.controller.enabled(identifier)
        # A launcher disabled on this device is dimmed but its switch stays live.
        title.setEnabled(enabled_here)
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
        def action(name, tooltip, accessible, theme, fallback, callback, enabled=True):
            button = QToolButton()
            button.setObjectName(name + '-' + identifier)
            button.setAutoRaise(True)
            button.setIcon(QIcon.fromTheme(theme, self.style().standardIcon(fallback)))
            button.setIconSize(QSize(12, 12))
            button.setFixedSize(24, 24)
            button.setToolTip(i18n.t(tooltip))
            button.setAccessibleName(i18n.t(accessible, launcher=launcher.get('Name', 'Launcher')))
            button.setEnabled(enabled)
            button.clicked.connect(callback)
            controls.addWidget(button)
        action('move-up', 'common.moveUp', 'launchers.card.moveUpNamed', 'go-up', QStyle.SP_ArrowUp, lambda: self.move_launcher(identifier, -1), position > 0)
        action('move-down', 'common.moveDown', 'launchers.card.moveDownNamed', 'go-down', QStyle.SP_ArrowDown, lambda: self.move_launcher(identifier, 1), position < len(self.controller.launchers) - 1)
        action('delete-launcher', 'launchers.delete.title', 'launchers.card.deleteNamed', 'edit-delete', QStyle.SP_TrashIcon, lambda: self.remove_launcher(identifier))
        header.addWidget(self._enabled_switch(launcher))
        header.addLayout(controls)
        layout.addLayout(header)
        divider = QFrame()
        divider.setFrameShape(QFrame.HLine)
        divider.setFrameShadow(QFrame.Plain)
        divider.setFixedHeight(1)
        layout.addWidget(divider)
        # Visual-only dimming (matches Windows' 50% opacity) so a launcher disabled on this
        # device can still be edited; unsupported launchers keep their existing disabled look.
        def dim(widget):
            if launcher_supported(launcher) and not enabled_here:
                effect = QGraphicsOpacityEffect(widget)
                effect.setOpacity(0.5)
                widget.setGraphicsEffect(effect)
        address = web_address(launcher)
        subtitle = (address or i18n.t('launchers.card.noWebAddress')) if launcher.get('Kind') == 1 else i18n.t('launchers.card.settingsSubtitle')
        if not self.controller.enabled(identifier):
            subtitle = (i18n.t('launchers.card.hiddenOnLinux', platform=launcher.get('Platform','windows').capitalize()) if launcher_hidden(launcher)
                        else i18n.t('launchers.card.disabledOnDevice', subtitle=subtitle))
        settings = LauncherActionRow(i18n.t('common.settings'), subtitle)
        settings.setObjectName('launcher-settings-' + identifier)
        settings.setEnabled(not launcher_hidden(launcher))
        dim(settings)
        settings.clicked.connect(lambda: self.configure(identifier))
        layout.addWidget(settings)
        if launcher.get('Kind', 0) != 1:
            count = self._item_count(launcher.get('Items', []))
            subtitle = i18n.plural('launchers.card.itemCountHiddenOnLinux' if launcher_hidden(launcher) else 'launchers.itemCount', count)
            items = LauncherActionRow(i18n.t('launchers.card.items'), subtitle)
            items.setObjectName('launcher-items-' + identifier)
            read_only = launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay')
            items.setEnabled(not read_only and not launcher_hidden(launcher))
            dim(items)
            items.setAccessibleName(i18n.t('launchers.card.editItems', launcher=launcher.get('Name', 'Launcher')))
            items.clicked.connect(lambda: self.edit_items(identifier))
            row = QHBoxLayout()
            row.setContentsMargins(0, 0, 0, 0)
            row.setSpacing(0)
            row.addWidget(items, 1)
            more = QToolButton()
            more.setObjectName('launcher-bulk-' + identifier)
            more.setEnabled(not launcher_hidden(launcher))
            dim(more)
            more.setAutoRaise(True)
            more.setText('…')
            more.setFixedSize(40, items.height())
            bulk_name = i18n.t('launchers.card.bulkActions', launcher=launcher.get('Name', 'Launcher'))
            more.setToolTip(bulk_name)
            more.setAccessibleName(bulk_name)
            # A popup callback keeps the ellipsis free of the extra split-menu arrow.
            def bulk_menu():
                menu = QMenu(more)
                export = menu.addAction(i18n.t('launchers.bulk.exportItems'), lambda: self.export_items(identifier))
                export.setEnabled(bool(launcher.get('Items')))
                imported = menu.addAction(i18n.t('launchers.bulk.importItems'), lambda: self.import_items(identifier))
                imported.setEnabled(not read_only)
                bookmarks=menu.addAction(i18n.t('launchers.bulk.importBookmarks'),lambda:self.import_browser_bookmarks(identifier))
                bookmarks.setEnabled(not read_only)
                menu.exec(more.mapToGlobal(more.rect().bottomLeft()))
            more.clicked.connect(bulk_menu)
            row.addWidget(more)
            layout.addLayout(row)
        return card

    def _enabled_switch(self, launcher):
        """Header checkbox for "Enabled on this device"; greyed out and off when unsupported here."""
        identifier = launcher['Id']
        name = i18n.t('launchers.card.enabledOnDevice', launcher=launcher.get('Name', 'Launcher'))
        box = QCheckBox()
        box.setObjectName('launcher-enabled-' + identifier)
        box.setToolTip(name)
        box.setAccessibleName(name)
        box.setChecked(self.controller.enabled(identifier))
        box.setEnabled(launcher_supported(launcher))
        box.toggled.connect(lambda enabled: self.set_launcher_enabled(identifier, enabled, box))
        return box

    def set_launcher_enabled(self, identifier, enabled, box=None):
        """Apply a device-only enablement change, confirming before anything is closed.

        Cancelling restores the checkbox without touching the controller. The list rebuilds
        itself from the controller's local_changed signal after a successful change.
        """
        def restore():
            if box is not None:
                with QSignalBlocker(box):
                    box.setChecked(self.controller.enabled(identifier))
        if not enabled and QMessageBox.question(self, i18n.t('launchers.disable.title'), i18n.t('launchers.disable.message'),
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No) != QMessageBox.Yes:
            restore()
            return
        if not self.controller.set_enabled(identifier, enabled):
            restore()

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
        path, _ = QFileDialog.getSaveFileName(self, i18n.t('launchers.bulk.exportItemsTitle'), 'launcher-items.json', i18n.t('launchers.bulk.jsonFiles') + ' (*.json)')
        if path:
            try:
                atomic_write(Path(path), (json.dumps(launcher.get('Items', []), indent=2) + '\n').encode())
            except OSError as error:
                QMessageBox.warning(self, i18n.t('launchers.bulk.exportFailed'), str(error))

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
            except DocumentError as error:QMessageBox.warning(self,i18n.t('launchers.bulk.importFailed'),str(error))

    def import_items(self, identifier):
        launcher = self.controller.launcher(identifier)
        if not launcher or launcher.get('Kind', 0) == 1:
            return
        expected = deepcopy(launcher)
        path, _ = QFileDialog.getOpenFileName(self, i18n.t('launchers.bulk.importItemsTitle'), '', i18n.t('launchers.bulk.jsonFiles') + ' (*.json)')
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
            choice.setWindowTitle(i18n.t('launchers.bulk.importItemsTitle'))
            choice.setText(i18n.plural('launchers.bulk.foundItems', self._item_count(imported)))
            replace = choice.addButton(i18n.t('launchers.bulk.replace'), QMessageBox.AcceptRole)
            append = choice.addButton(i18n.t('common.add'), QMessageBox.ActionRole)
            choice.addButton(QMessageBox.Cancel)
            choice.exec()
            if choice.clickedButton() is append:
                candidate['Items'] = deepcopy(expected.get('Items', [])) + imported
            elif choice.clickedButton() is not replace:
                return
            self.controller.replace_launcher(candidate, expected)
        except (OSError, ValueError) as error:
            QMessageBox.warning(self, i18n.t('launchers.bulk.importFailed'), str(error))

    def _sync(self):
        from .sync import LauncherSync
        from .sync_page import CloudSyncPage
        self.sync = LauncherSync(self.controller)
        page = CloudSyncPage(self.sync, self.download_cloud, self.upload_cloud, self.import_file, self.export_file)
        self.pages.addWidget(page)
        self.sync_status = page.notice
        self.auto_sync = page.automatic
        self.sync.upload_review_ready.connect(self._review_upload)
        self.sync.manual_failed.connect(lambda message:QMessageBox.warning(self,i18n.t('sync.review.failedTitle'),message))

    def download_cloud(self):
        if QMessageBox.question(self, i18n.t('sync.actions.download'), i18n.t('sync.review.downloadConfirm'),
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No) == QMessageBox.Yes:
            self.sync.download()

    def upload_cloud(self):
        self.sync.prepare_upload()

    def _review_upload(self,review):
        dialog=QMessageBox(self);dialog.setWindowTitle(i18n.t('sync.review.conflictTitle' if review['conflict'] else 'sync.review.title'));dialog.setTextFormat(Qt.PlainText)
        download_only=review['action']=='download'
        dialog.setText(i18n.t('sync.review.downloadQuestion') if download_only else
                       i18n.t('sync.review.conflictQuestion') if review['conflict'] else
                       i18n.t('sync.review.uploadQuestion'))
        details=i18n.t('sync.review.changes',added=review['added'],removed=review['removed'],changed=review['changed'])
        if review['iconChanges']:
            details+='\n'+i18n.plural('sync.review.iconChanges',review['iconChanges'],names=', '.join(review['iconNames']))
        details+='\n\n'+i18n.t('sync.review.backupNote')
        dialog.setInformativeText(details)
        upload=None if download_only else dialog.addButton(i18n.t('sync.review.keepDevice'),QMessageBox.AcceptRole)
        download=dialog.addButton(i18n.t('sync.review.keepCloud'),QMessageBox.ActionRole)if download_only or review['conflict']else None
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

    def _language(self, layout):
        """Add the interface language picker: Match device first, then each language in its own name.

        The choice is device-local (linux.json), never synced. Windows already open keep their
        text, so the row says a restart finishes the switch; new dialogs use it straight away.
        """
        from PySide6.QtWidgets import QComboBox
        combo = QComboBox()
        combo.setMinimumWidth(200)
        combo.addItem(i18n.t('system.language.matchDevice', language=i18n.NATIVE_NAMES[i18n.device_locale()]), '')
        for identity in i18n.ORDERED:
            combo.addItem(i18n.NATIVE_NAMES[identity], identity)
        saved = i18n.normalize_override(self.controller.preferences.data.get('Language')) or ''
        combo.setCurrentIndex(max(0, combo.findData(saved)))
        note = QLabel(i18n.t('system.language.restartNote'))
        note.setWordWrap(True)
        note.setVisible(False)

        def choose(index):
            value = combo.itemData(index)
            if value == (i18n.normalize_override(self.controller.preferences.data.get('Language')) or ''):
                return
            try:
                self.controller.preferences.set('Language', value)
            except OSError as error:
                QMessageBox.warning(self, i18n.t('system.language.saveFailed'), str(error))
                return
            note.setVisible(True)

        combo.currentIndexChanged.connect(choose)
        self._settings_row(layout, i18n.t('system.language.title'), i18n.t('system.language.description'), combo)
        layout.addWidget(note)

    def _system(self):
        from PySide6.QtWidgets import QApplication, QComboBox
        layout = self._page(i18n.t('common.settings'))
        self._text(layout, i18n.t('system.appearance'))
        theme = QComboBox()
        theme.addItems([i18n.t('system.theme.systemDefault'), i18n.t('system.theme.light'), i18n.t('system.theme.dark')])
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
        self._settings_row(layout, i18n.t('system.theme.title'), i18n.t('system.theme.description'), theme)
        self._language(layout)
        start = QCheckBox()
        self.startup_control = start
        start.setChecked(self.controller.preferences.data.get('StartAtLogin', True))
        start.toggled.connect(self.autostart)
        self._settings_row(layout, i18n.t('system.startup.title'), i18n.t('system.startup.description'), start)
        launchers = QPushButton(i18n.t('system.launcherAppearance.manage'))
        launchers.clicked.connect(lambda: self.navigation.setCurrentRow(1))
        self._settings_row(layout, i18n.t('system.launcherAppearance.title'), i18n.t('system.launcherAppearance.description'), launchers)
        self._text(layout, i18n.t('linuxDesktop.title'))
        placement = QWidget()
        placement_layout = QVBoxLayout(placement)
        placement_layout.setContentsMargins(0, 0, 0, 0)
        placement_layout.setSpacing(8)
        self.integration_status = QLabel()
        self.integration_status.setWordWrap(True)
        placement_layout.addWidget(self.integration_status)
        self.integration_button = QPushButton(i18n.t('linuxDesktop.enable'))
        self.integration_button.clicked.connect(self.enable_integration)
        placement_layout.addWidget(self.integration_button, 0, Qt.AlignRight)
        self._settings_row(layout, i18n.t('linuxDesktop.placement.title'), i18n.t('linuxDesktop.placement.description'), placement)
        self._text(layout, i18n.t('system.extensions.section'))
        extensions=QPushButton(i18n.t('webExtensions.manage'))
        extensions.clicked.connect(lambda:self.extensions_requested.emit(''))
        self._settings_row(layout,i18n.t('system.extensions.title'),i18n.t('system.extensions.manageDescription'),extensions)
        self._text(layout, i18n.t('system.backup.section'))
        exports = QWidget()
        from .item_toolbar import ActionFlowLayout
        actions = ActionFlowLayout(exports)
        for label, callback in [('system.backup.exportLaunchers', self.export_file), ('system.backup.importLaunchers', self.import_file)]:
            button = QPushButton(i18n.t(label))
            button.clicked.connect(callback)
            actions.addWidget(button)
        self._settings_row(layout, i18n.t('system.backup.launchersTitle'), i18n.t('system.backup.launchersDescription'), exports)
        layout.addStretch()

    def _about(self):
        layout = self._page(i18n.t('about.title'))
        icon = QLabel()
        icon.setPixmap(QIcon(str(ICON)).pixmap(48, 48))
        self._settings_row(layout, 'Little Launcher', application_version() + ' · ' + i18n.t('about.trayDescription'), icon)
        built = QLabel('')
        self._settings_row(layout, i18n.t('about.builtWith'), 'Qt Widgets · PySide6 · Python · Electron · Chromium', built)
        repository = QPushButton(i18n.t('about.sourceCode.link'))
        repository.clicked.connect(lambda: QDesktopServices.openUrl(QUrl('https://github.com/Dynamic-Solutions-Canada/little-launcher')))
        self._settings_row(layout, i18n.t('about.sourceCode.title'), i18n.t('about.sourceCode.description'), repository)
        self._settings_row(layout,i18n.t('about.update.title'),i18n.t('linuxUpdates.description'),UpdateControls(self.updates))
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
            self._text(self.launcher_cards, i18n.t('launchers.empty'))
        self.summary.setText(i18n.plural('home.summary.launchers', len(self.controller.launchers)) + ' ' + i18n.plural('home.summary.hiddenOnLinux', hidden))
        self.integration.refresh()
        kde = 'KDE' in os.environ.get('XDG_CURRENT_DESKTOP', '').upper().split(':')
        preview = bool(os.environ.get('LITTLE_LAUNCHER_PREVIEW_ID'))
        if self.integration.enabled:
            self.integration_status.setText(i18n.t('linuxDesktop.status.onPreview') if preview else i18n.t('common.on'))
            self.integration_button.setText(i18n.t('linuxDesktop.update'))
        elif kde:
            self.integration_status.setText(i18n.t('linuxDesktop.status.off'))
            self.integration_button.setText(i18n.t('linuxDesktop.enable'))
        else:
            self.integration_status.setText(i18n.t('linuxDesktop.status.unavailable'))
        self.integration_button.setEnabled(kde and not preview)
        self.integration_button.setToolTip(i18n.t('linuxDesktop.enableTooltip') if kde else i18n.t('linuxDesktop.kdeOnly'))

    def edit_items(self, identifier):
        launcher = self.controller.launcher(identifier)
        if not launcher or not launcher_supported(launcher):
            return
        if launcher.get('Kind', 0) == 1:
            self.configure(identifier)
            return
        if launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay'):
            QMessageBox.information(self, i18n.t('launchers.readOnly.title'), i18n.t('launchers.readOnly.message'))
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
        launcher = new_launcher(i18n.t('launchers.defaultName.web' if web else 'launchers.defaultName.shortcuts'), web)
        dialog = LauncherDialog(launcher, self)
        if dialog.exec() == QDialog.Accepted:
            value = dialog.value()
            if self.controller.edit(lambda document: document['Launchers'].append(value)) and value.get('Kind', 0) != 1:
                self.edit_items(value['Id'])

    def remove_launcher(self, identifier):
        launcher = self.controller.launcher(identifier)
        if launcher is None:
            return
        # The name is synced user content, so it must not be interpreted as rich text.
        box = QMessageBox(QMessageBox.Question, i18n.t('launchers.delete.title'),
                          i18n.t('launchers.delete.message', launcher=launcher.get('Name', 'Launcher')), QMessageBox.Yes | QMessageBox.No, self)
        box.setTextFormat(Qt.PlainText)
        # Linux keeps the launcher's device-local browser sessions; say so, as before.
        box.setInformativeText(i18n.t('launchers.delete.sessionsKept'))
        if box.exec() == QMessageBox.Yes:
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
        path, _ = QFileDialog.getOpenFileName(self, i18n.t('system.backup.importLaunchersTitle'), '', i18n.t('launchers.bulk.jsonFiles') + ' (*.json)')
        if not path:
            return
        try:
            self.controller.import_document(json.loads(Path(path).read_text()))
        except (OSError, ValueError) as error:
            QMessageBox.warning(self, i18n.t('launchers.bulk.importFailed'), str(error))

    def export_file(self):
        path, _ = QFileDialog.getSaveFileName(self, i18n.t('system.backup.exportLaunchersTitle'), 'launchers.json', i18n.t('launchers.bulk.jsonFiles') + ' (*.json)')
        if path:
            try:
                atomic_write(Path(path), (json.dumps(sync_envelope(pack_icons(self.controller.document,LauncherIcons.glyph_png)), indent=2) + '\n').encode())
            except OSError as error:
                QMessageBox.warning(self, i18n.t('launchers.bulk.exportFailed'), str(error))

    def autostart(self, enabled):
        previous=self.controller.preferences.data.get('StartAtLogin',True)
        try:
            set_autostart(enabled)
            self.controller.preferences.set('StartAtLogin',enabled)
        except (OSError,ValueError) as error:
            try:set_autostart(previous)
            except (OSError,ValueError):pass
            with QSignalBlocker(self.startup_control):self.startup_control.setChecked(previous)
            QMessageBox.warning(self,i18n.t('linuxDesktop.autostartFailed'),str(error))

    def enable_integration(self):
        try:
            self.integration.enable()
        except OSError as error:
            QMessageBox.warning(self, i18n.t('linuxDesktop.title'), str(error))
        self.refresh()
