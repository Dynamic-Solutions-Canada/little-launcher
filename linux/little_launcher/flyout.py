"""Compact native shortcut flyout with in-place editing and an owned floating toolbar."""
from copy import deepcopy

from PySide6.QtCore import QEvent, Qt, Signal
from PySide6.QtGui import QAction, QIcon
from PySide6.QtWidgets import (
    QApplication, QDialog, QFrame, QMenu, QMessageBox, QToolButton,
    QScrollArea, QSizePolicy, QStyle, QStyleOption, QStylePainter, QVBoxLayout,
)

from .desktop import launch_item
from .item_editing import ItemEditingSession
from .item_toolbar import ItemEditToolbar
from .items_view import LauncherItemsView
from .icons import LauncherIcons


class FloatingEditToolbar(QDialog):
    """Non-activating owned bar; KWin places it against this flyout's settled frame."""

    def __init__(self, owner, session):
        super().__init__(owner, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setWindowTitle('LittleLauncherToolbar:' + owner.launcher['Id'])
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.controls = ItemEditToolbar(session, compact=True, parent=self)
        self.controls.settings_requested.connect(owner.configure)
        self.controls.done_requested.connect(owner.exit_edit)
        layout.addWidget(self.controls)
        self.controls.fit_width(owner.width())
        self.setFixedSize(self.controls.size())
        self.setContextMenuPolicy(Qt.CustomContextMenu)
        self.customContextMenuRequested.connect(lambda point: owner.options_menu(self.mapToGlobal(point)))

    def position_for(self, owner):
        """Provide best-effort regular-window placement; the helper owns Wayland anchoring."""
        self.controls.fit_width(owner.width())
        self.setFixedSize(self.controls.size())
        geometry = owner.frameGeometry()
        area = owner.screen().availableGeometry()
        x = max(area.left(), min(area.right() - self.width() + 1, geometry.right() - self.width() + 1))
        y = geometry.top() - self.height() - 6
        if y < area.top():
            y = geometry.bottom() + 6
        self.move(x, max(area.top(), min(y, area.bottom() - self.height() + 1)))

    def paintEvent(self, event):
        option = QStyleOption()
        option.initFrom(self)
        painter = QStylePainter(self)
        painter.drawPrimitive(QStyle.PE_PanelMenu, option)


class ShortcutFlyout(QDialog):
    """Share exact item geometry with the regular editor; edit entry never opens another host."""
    edit_requested = Signal(str)
    settings_requested = Signal(str)
    website_requested = Signal(object)
    app_settings_requested = Signal()

    def __init__(self, launcher, anchor=None, parent=None, controller=None, focus_regular_editor=None):
        super().__init__(parent)
        self.launcher = launcher
        self.controller = controller
        self.focus_regular_editor = focus_regular_editor
        self.anchored = anchor is not None
        self._protocol_title = None
        self.editing = False
        self.restoring_activation = False
        self.session = None
        self.toolbar = None
        self.owned_interaction = False

        if self.anchored:
            self.setWindowFlag(Qt.Tool)
            self.setWindowFlag(Qt.FramelessWindowHint)
            self.setAttribute(Qt.WA_TranslucentBackground)
            self._protocol_title = f"LittleLauncherFlyout:{launcher['Id']}:{anchor[0]}:{anchor[1]}" + (":native" if len(anchor) > 2 and anchor[2] == "native" else "")
            self.update_protocol_title()
        else:
            self.setWindowTitle(launcher.get('Name', 'Launcher') + ' · Regular window')
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(False)
        self.scroll.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Ignored)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.viewport().setAutoFillBackground(False)
        layout.addWidget(self.scroll)
        self.edit_button = QToolButton(self)
        self.edit_button.setIcon(QIcon.fromTheme('document-edit', self.style().standardIcon(QStyle.SP_FileDialogDetailedView)))
        self.edit_button.setToolTip('Edit items')
        self.edit_button.setAccessibleName('Edit items')
        self.edit_button.setAutoRaise(True)
        self.edit_button.setFixedSize(24, 24)
        self.edit_button.clicked.connect(self.enter_edit)
        self.edit_button.hide()
        self.rebuild()

    @property
    def owned_interaction(self):
        return getattr(self, '_owned_interaction', False)

    @owned_interaction.setter
    def owned_interaction(self, value):
        self._owned_interaction = value
        self.update_protocol_title()

    def update_protocol_title(self):
        title = getattr(self, '_protocol_title', None)
        if title:
            self.setWindowTitle(title + (':owned' if self.editing or self.owned_interaction or self.restoring_activation else '') + (':focus' if self.restoring_activation else ''))

    def rebuild(self):
        """Only column headers or explicit content edits add space; item cells never stretch."""
        self.update_protocol_title()
        old = self.scroll.takeWidget()
        if old:
            old.deleteLater()
        launcher = self.session.original if self.session else self.launcher
        self.setWindowIcon(LauncherIcons.resolve(launcher))
        self.view = LauncherItemsView(launcher, self.editing, True)
        self.view.activated.connect(self._launch)
        self.view.content_size_changed.connect(self._preview_size)
        if self.session:
            self.session.wire(self.view)
        self.scroll.setWidget(self.view)
        # QScrollArea turns this on when installing its widget; keep the native menu corners visible.
        self.view.setAutoFillBackground(False)
        available = self.screen().availableGeometry()
        self.resize(min(self.view.width(), available.width() - 16),
                    min(self.view.height(), available.height() - 16))
        self.edit_button.move(self.width() - 26, 2)
        self.edit_button.raise_()
        if self.toolbar:
            self.toolbar.position_for(self)

    def _preview_size(self, keep_right):
        # Wayland does not supply stable global pointer coordinates while KWin
        # reanchors a resizing window. Reflow inside its viewport during the drag;
        # the committed rebuild applies the new outer geometry once on release.
        if self.view.resize_preview is not None:
            return
        available = self.screen().availableGeometry()
        self.resize(min(self.view.width(), available.width() - 16), min(self.view.height(), available.height() - 16))

    def enter_edit(self):
        if self.editing:
            return
        read_only = self.launcher.get('IsShared') and not self.launcher.get('IsSharedOwner') and not self.launcher.get('SharedTwoWay')
        if read_only:
            return
        if self.focus_regular_editor and self.focus_regular_editor(self.launcher['Id']):
            self.hide()
            return
        if not self.controller:
            self.edit_requested.emit(self.launcher['Id'])
            return
        self.session = ItemEditingSession(self.controller, self.launcher, self)
        self.session.changed.connect(self._edited)
        self.editing = True
        self.edit_button.hide()
        self.rebuild()
        self.toolbar = FloatingEditToolbar(self, self.session)
        self.toolbar.position_for(self)
        self.toolbar.show()

    def _edited(self):
        self.launcher = deepcopy(self.session.original)
        self.rebuild()

    def sync_launcher(self, launcher):
        """Refresh this same host after saves; active dialogs keep their own stale-save guard."""
        self.launcher = deepcopy(launcher)
        if self.session and self.session.saving:
            return
        if self.session:
            self.session.original = deepcopy(launcher)
        self.rebuild()

    def exit_edit(self, restore=True):
        if not self.editing:
            return
        self.restoring_activation = bool(restore and self.isVisible())
        self.editing = False
        if self.toolbar:
            self.toolbar.hide()
            self.toolbar.deleteLater()
            self.toolbar = None
        self.session.deleteLater()
        self.session = None
        self.rebuild()
        if restore and self.isVisible():
            self.activateWindow()
            if self.isActiveWindow():
                self.restoring_activation = False
                self.update_protocol_title()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if hasattr(self, 'edit_button'):
            self.edit_button.move(self.width() - 26, 2)
        if self.toolbar:
            self.toolbar.position_for(self)

    def moveEvent(self, event):
        super().moveEvent(event)
        if self.toolbar:
            self.toolbar.position_for(self)

    def enterEvent(self, event):
        read_only = self.launcher.get('IsShared') and not self.launcher.get('IsSharedOwner') and not self.launcher.get('SharedTwoWay')
        if not self.editing and not read_only:
            self.edit_button.show()
        super().enterEvent(event)

    def leaveEvent(self, event):
        self.edit_button.hide()
        super().leaveEvent(event)

    def options_menu(self, position):
        menu = QMenu(self)
        if self.editing:
            menu.addAction('Done', self.exit_edit)
        else:
            menu.addAction('Edit items', self.enter_edit)
        menu.addAction('Launcher settings', self.configure)
        menu.addAction('App settings', self.app_settings_requested.emit)
        self.owned_interaction = True
        try:
            menu.exec(position)
        finally:
            self.owned_interaction = False

    def contextMenuEvent(self, event):
        self.options_menu(event.globalPos())

    def configure(self):
        self.owned_interaction = True
        try:
            self.settings_requested.emit(self.launcher['Id'])
        finally:
            self.owned_interaction = False
        if self.isVisible():
            self.activateWindow()

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Escape:
            if self.editing:
                self.exit_edit()
            else:
                self.hide()
            event.accept()
            return
        super().keyPressEvent(event)

    def hideEvent(self, event):
        self.restoring_activation = False
        self.exit_edit(restore=False)
        super().hideEvent(event)

    def paintEvent(self, event):
        if not self.anchored:
            super().paintEvent(event)
            return
        option = QStyleOption()
        option.initFrom(self)
        painter = QStylePainter(self)
        painter.drawPrimitive(QStyle.PE_PanelMenu, option)
        if self.editing:
            painter.setPen(self.palette().highlight().color())
            painter.drawRect(self.rect().adjusted(0, 0, -1, -1))

    def _launch(self, item):
        if item.get('IsWebsite') and item.get('OpenInAppWindow'):
            self.website_requested.emit(item)
            self.hide()
            return
        try:
            launch_item(item)
        except (OSError, ValueError) as error:
            QMessageBox.warning(self, 'Could not open shortcut', str(error))
            return
        self.hide()

    def event(self, event):
        if event.type() == QEvent.WindowActivate and getattr(self, 'restoring_activation', False):
            self.restoring_activation = False
            self.update_protocol_title()
        if event.type() == QEvent.WindowDeactivate and self.anchored:
            if not self.editing and not self.owned_interaction and not self.restoring_activation and QApplication.activeModalWidget() is None:
                self.hide()
        return super().event(event)
