"""Shared native add/settings controls with regular and compact Windows-reference layouts."""
from math import ceil

from PySide6.QtCore import QRect, QSize, Qt, Signal
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (
    QHBoxLayout, QLayout, QPushButton, QStyle, QToolButton, QWidget,
)


class ActionFlowLayout(QLayout):
    """Wrap controls at their native natural widths without stretching each action to a column."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.items = []
        self.setContentsMargins(0, 0, 0, 0)
        self.setSpacing(8)

    def addItem(self, item):
        self.items.append(item)

    def count(self):
        return len(self.items)

    def itemAt(self, index):
        return self.items[index] if 0 <= index < len(self.items) else None

    def takeAt(self, index):
        return self.items.pop(index) if 0 <= index < len(self.items) else None

    def hasHeightForWidth(self):
        return True

    def heightForWidth(self, width):
        return self._arrange(QRect(0, 0, width, 0), False)

    def setGeometry(self, rectangle):
        super().setGeometry(rectangle)
        self._arrange(rectangle, True)

    def sizeHint(self):
        return self.minimumSize()

    def minimumSize(self):
        size = QSize()
        for item in self.items:
            if item.isEmpty():continue
            size = size.expandedTo(item.minimumSize())
        return size

    def _arrange(self, rectangle, apply):
        """Compute wrapping independently from the settings control's separate fixed column."""
        x, y = rectangle.x(), rectangle.y()
        row_height = 0
        for item in self.items:
            if item.isEmpty():continue
            size = item.sizeHint()
            if x > rectangle.x() and x + size.width() > rectangle.right() + 1:
                x = rectangle.x()
                y += row_height + self.spacing()
                row_height = 0
            if apply:
                item.setGeometry(QRect(x, y, size.width(), size.height()))
            x += size.width() + self.spacing()
            row_height = max(row_height, size.height())
        return y + row_height - rectangle.y()


class ItemEditToolbar(QWidget):
    """Keep regular labelled actions and compact 32-pixel slots backed by one edit session."""
    settings_requested = Signal()
    done_requested = Signal()

    def __init__(self, session, compact=False, parent=None):
        super().__init__(parent)
        self.compact = compact
        self.buttons = []
        layout = None if compact else QHBoxLayout(self)
        if layout:
            layout.setContentsMargins(0, 0, 0, 0)
            layout.setSpacing(8)
            actions = QWidget(self)
            flow = ActionFlowLayout(actions)
            layout.addWidget(actions, 1)
        for text, theme, icon, kind in [('Add item', 'list-add', QStyle.SP_FileIcon, 0),
                                        ('Add group', 'folder-new', QStyle.SP_DirIcon, 3),
                                        ('Add column', 'view-split-left-right', QStyle.SP_FileDialogListView, 4)]:
            button = QToolButton(self) if compact else QPushButton(text, self)
            button.setIcon(QIcon.fromTheme(theme, self.style().standardIcon(icon)))
            button.setToolTip(text)
            button.setAccessibleName(text)
            button.clicked.connect(lambda checked=False, value=kind: session.add_item(value))
            self.buttons.append(button)
            if layout:
                flow.addWidget(button)
        settings = QToolButton(self)
        settings.setIcon(QIcon.fromTheme('configure', self.style().standardIcon(QStyle.SP_FileDialogDetailedView)))
        settings.setToolTip('Launcher settings')
        settings.setAccessibleName('Launcher settings')
        settings.clicked.connect(self.settings_requested)
        self.buttons.append(settings)
        if layout:
            settings.setFixedSize(32, 32)
            layout.addWidget(settings, 0, Qt.AlignTop)
        else:
            done = QToolButton(self)
            done.setIcon(self.style().standardIcon(QStyle.SP_DialogApplyButton))
            done.setToolTip('Done')
            done.setAccessibleName('Done')
            done.clicked.connect(self.done_requested)
            self.buttons.append(done)
            for button in self.buttons:
                button.setFixedSize(28, 28)
                button.setAutoRaise(True)
                button.setFocusPolicy(Qt.NoFocus)
            self.fit_width(200)

    def fit_width(self, width):
        """Wrap only the floating toolbar, preserving the launcher's intrinsic width."""
        if not self.compact:
            return
        per_row = min(5, max(1, (width - 16) // 32))
        for index, button in enumerate(self.buttons):
            button.move(5 + index % per_row * 32, 5 + index // per_row * 32)
        self.setFixedSize(per_row * 32 + 6, ceil(len(self.buttons) / per_row) * 32 + 6)
