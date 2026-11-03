"""Editing-only native edge gestures; previews never write the launcher document."""
from math import copysign, floor

from PySide6.QtCore import Qt, QEvent
from PySide6.QtWidgets import QWidget


class IconResizeGrip(QWidget):
    def __init__(self, view, left):
        super().__init__(view)
        self.view = view
        self.left = left
        self.active = False
        self.setCursor(Qt.SizeHorCursor)
        self.setToolTip('Drag to change icons per row')
        self.setAccessibleName('Resize icon grid from ' + ('left' if left else 'right'))
        self.setFocusPolicy(Qt.StrongFocus)

    def mousePressEvent(self, event):
        if event.button() != Qt.LeftButton:
            return super().mousePressEvent(event)
        self.active = True
        self.start_x = event.globalPosition().x()
        self.start_count = self.view.metrics.per_row
        self.target = self.start_count
        self.step = self.view.metrics.cell_width * max(1, len(self.view.metrics.columns))
        self.setFocus(Qt.MouseFocusReason)
        self.grabMouse()
        event.accept()

    def mouseMoveEvent(self, event):
        if not self.active:
            return super().mouseMoveEvent(event)
        distance = (event.globalPosition().x() - self.start_x) * (-1 if self.left else 1)
        steps = int(copysign(floor(abs(distance) / self.step + .5), distance))
        target = max(1, min(12, self.start_count + steps))
        if target != self.target:
            self.target = target
            self.view.preview_icon_count(target, self.left)
        event.accept()

    def mouseReleaseEvent(self, event):
        if self.active and event.button() == Qt.LeftButton:
            target = self.target
            self.active = False
            self.releaseMouse()
            self.view.preview_icon_count(None, self.left)
            if target != self.start_count:
                self.view.icons_per_row_requested.emit(target)
            event.accept()
        else:
            super().mouseReleaseEvent(event)

    def keyPressEvent(self, event):
        if self.active and event.key() == Qt.Key_Escape:
            self.active = False
            self.releaseMouse()
            self.view.preview_icon_count(None, self.left)
            event.accept()
        else:
            super().keyPressEvent(event)

    def event(self, event):
        if event.type() == QEvent.UngrabMouse and self.active:
            self.active = False
            self.view.preview_icon_count(None, self.left)
        return super().event(event)
