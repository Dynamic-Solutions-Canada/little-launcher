"""Regular native editor: Windows-reference toolbar, intrinsic preview and external Done."""
from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QKeySequence, QShortcut, QPalette
from PySide6.QtWidgets import (
    QDialog, QFrame, QHBoxLayout, QPushButton, QScrollArea, QVBoxLayout,
)

from .item_editing import ItemEditingSession
from .item_toolbar import ItemEditToolbar
from .items_view import LauncherItemsView
from .icons import LauncherIcons


class ItemsEditor(QDialog):
    """Keep the launcher compact inside a scrolling viewport; native frame styling stays local."""
    settings_requested = Signal(str)

    def __init__(self, controller, launcher, parent=None):
        super().__init__(parent)
        self.controller = controller
        self.session = ItemEditingSession(controller, launcher, self)
        self.session.changed.connect(self.rebuild)
        self.setWindowTitle(launcher.get('Name', 'Launcher') + ' · Edit items')
        self.resize(560, 620)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 8, 24, 24)
        layout.setSpacing(0)
        self.toolbar = ItemEditToolbar(self.session, parent=self)
        self.toolbar.settings_requested.connect(lambda: self.settings_requested.emit(self.original['Id']))
        layout.addWidget(self.toolbar)
        layout.addSpacing(8)
        self.scroll = QScrollArea()
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.setWidgetResizable(False)
        self.scroll.viewport().setAutoFillBackground(False)
        layout.addWidget(self.scroll, 1)
        layout.addSpacing(16)
        footer = QHBoxLayout()
        footer.addStretch()
        done = QPushButton('Done')
        done.setMinimumWidth(90)
        done.setDefault(True)
        done.clicked.connect(self.accept)
        footer.addWidget(done)
        layout.addLayout(footer)
        QShortcut(QKeySequence(Qt.Key_Escape), self, activated=self.accept)
        self.rebuild()

    @property
    def original(self):
        return self.session.original

    def rebuild(self):
        """Recreate only the intrinsic preview; viewport size never stretches the launcher."""
        self.setWindowIcon(LauncherIcons.resolve(self.original))
        old = self.scroll.takeWidget()
        if old:
            old.deleteLater()
        self.view = LauncherItemsView(self.original, editing=True, show_other=True)
        self.session.wire(self.view)
        preview = QFrame()
        preview.setFrameShape(QFrame.NoFrame)
        preview.setBackgroundRole(QPalette.AlternateBase)
        preview.setAutoFillBackground(True)
        preview.setFocusPolicy(Qt.NoFocus)
        self.view.setParent(preview)
        self.view.move(1, 1)
        preview.setFixedSize(self.view.width() + 2, self.view.height() + 2)
        self.view.content_size_changed.connect(lambda _: preview.setFixedSize(self.view.width() + 2, self.view.height() + 2))
        self.scroll.setWidget(preview)
        self.scroll.setAlignment(Qt.AlignLeft | Qt.AlignTop)

    def add_item(self, kind=0):
        self.session.add_item(kind)

    def remove_column(self, index):
        self.session.remove_column(index)

    def edit_item(self, path):
        self.session.edit_item(path)

    def remove_item(self, path):
        self.session.remove_item(path)

    def step_item(self, path, delta):
        self.session.step_item(path, delta)

    def move_item(self, source, target, after=False, into=None):
        self.session.move_item(source, target, after, into)
