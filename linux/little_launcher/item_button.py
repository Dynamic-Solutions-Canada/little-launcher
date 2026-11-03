"""Native shortcut cells with Windows-reference content slots and overlay edit feedback."""
import json

from PySide6.QtCore import QMimeData, QPoint, QRect, QSize, Qt, Signal
from PySide6.QtGui import QDrag, QFont, QIcon, QPainter, QTextLayout, QTextOption
from PySide6.QtWidgets import QApplication, QStyle, QStyleOptionToolButton, QToolButton

from .desktop import item_icon

from .item_drag import ItemDrag, MIME_TYPE


class ItemButton(QToolButton):
    """Paint exact item geometry using the active native style, palette and font family."""
    moved = Signal(object, object, bool, bool)
    edit_clicked = Signal()

    def __init__(self, placement, mode, session, editing, parent=None):
        super().__init__(parent)
        self.item = placement.item
        self.path = list(placement.path)
        self.kind = placement.kind
        self.mode = mode
        self.session = session
        self.editing = editing
        self.press_position = QPoint()
        self.drag_handler = None
        self.active_drag = None
        self.drag_caption = ''
        self.drop_mode = None
        self.setGeometry(placement.x, placement.y, placement.width, placement.height)
        self.setAcceptDrops(editing)
        if not editing and self.kind != 'item':
            self.setFocusPolicy(Qt.NoFocus)
        self.setAutoRaise(True)
        self.setIcon(item_icon(self.item))
        self.setAccessibleName(self.item.get('Name', ''))
        self.setToolTip(self.item.get('Name', ''))
        self.pencil = QToolButton(self)
        self.pencil.setAutoRaise(True)
        self.pencil.setIcon(QIcon.fromTheme('document-edit', self.style().standardIcon(QStyle.SP_FileDialogDetailedView)))
        self.pencil.setIconSize(QSize(12, 12))
        compact = self.mode == 2 and self.kind == 'item'
        self.pencil.setFixedSize(16 if compact else 20, 16 if compact else 20)
        self.pencil.setToolTip('Rename group' if self.kind == 'group' else 'Edit item')
        self.pencil.setAccessibleName(self.pencil.toolTip())
        pencil_y = self.height() - 18 if compact else (self.height() - 20) // 2 if self.mode == 1 or self.kind != 'item' else 2
        self.pencil.move(self.width() - self.pencil.width() - 2, pencil_y)
        self.pencil.clicked.connect(self.edit_clicked)
        self.pencil.hide()

    def enterEvent(self, event):
        if self.drag_handler:
            self.drag_handler.hover(self.kind == 'group')
        if self.editing:
            self.pencil.show()
        super().enterEvent(event)

    def focusInEvent(self, event):
        if self.drag_handler and self.kind == 'group':
            self.drag_handler.hover(True)
        super().focusInEvent(event)

    def focusOutEvent(self, event):
        if self.drag_handler and self.kind == 'group':
            self.drag_handler.hover(False)
        super().focusOutEvent(event)

    def leaveEvent(self, event):
        if self.drag_handler and self.kind == 'group':
            self.drag_handler.hover(False)
        self.pencil.hide()
        super().leaveEvent(event)

    def paintEvent(self, event):
        painter = QPainter(self)
        option = QStyleOptionToolButton()
        self.initStyleOption(option)
        option.text = ''
        option.icon = QIcon()
        if self.drag_handler and self.drag_handler.intent:
            option.state &= ~(QStyle.State_MouseOver | QStyle.State_Sunken | QStyle.State_On)
        if self.kind == 'item' or (self.editing and self.kind != 'group'):
            self.style().drawComplexControl(QStyle.CC_ToolButton, option, painter, self)
        font = QFont(self.font())
        font.setPixelSize(12 if self.kind != 'item' else 13 if self.mode == 1 else 11)
        font.setWeight(QFont.DemiBold if self.kind != 'item' else QFont.Normal)
        painter.setFont(font)
        text = self.item.get('Name', '')
        if self.kind != 'item':
            color = self.palette().windowText().color()
            color.setAlphaF(0.7)
            painter.setPen(color)
            rectangle = self.rect().adjusted(8, 0, -8, 0)
            if self.editing and self.pencil.isVisible():
                rectangle.adjust(8, 0, -self.pencil.width() - 4, 0)
            alignment = Qt.AlignVCenter | (Qt.AlignLeft if self.mode == 1 else Qt.AlignHCenter)
            painter.drawText(rectangle, alignment, painter.fontMetrics().elidedText(text, Qt.ElideRight, rectangle.width()))
        else:
            painter.setPen(self.palette().buttonText().color())
            size = 20 if self.mode in (1, 2) else 32
            icon_x = 10 if self.mode == 1 else (self.width() - size) // 2
            icon_y = (self.height() - size) // 2 if self.mode in (1, 2) else 8
            self.icon().paint(painter, QRect(icon_x, icon_y, size, size))
            if self.mode == 1:
                rectangle = self.rect().adjusted(40, 0, -8, 0)
                if self.editing and self.pencil.isVisible():
                    rectangle.adjust(0, 0, -self.pencil.width() - 4, 0)
                painter.drawText(rectangle, Qt.AlignVCenter | Qt.AlignLeft,
                                 painter.fontMetrics().elidedText(text, Qt.ElideRight, rectangle.width()))
            elif self.mode == 0:
                self._wrapped_label(painter, text, font)
        if self.editing and self.pencil.isVisible():
            painter.setPen(self.palette().mid().color())
            top = 3 if self.mode != 1 and self.kind == 'item' else max(2, (self.height() - 10) // 2)
            for row in range(3):
                painter.drawPoint(3, top + row * 4)
                painter.drawPoint(6, top + row * 4)
        if self.drop_mode:
            painter.setPen(self.palette().highlight().color())
            if self.drop_mode == 'into':
                painter.drawRoundedRect(self.rect().adjusted(1, 1, -2, -2), 3, 3)
            elif self.mode == 1 or self.kind != 'item':
                y = 1 if self.drop_mode == 'before' else self.height() - 2
                painter.drawLine(0, y, self.width(), y)
            else:
                x = 1 if self.drop_mode == 'before' else self.width() - 2
                painter.drawLine(x, 0, x, self.height())

    def _wrapped_label(self, painter, text, font):
        """Reserve the reference 48-pixel icon slot and two 72-pixel-wide label lines."""
        layout = QTextLayout(text, font)
        # QTextLayout positions count UTF-16 units, while Python indexes Unicode code points.
        text_units = text.encode('utf-16-le')
        unit_count = len(text_units) // 2
        option = QTextOption(Qt.AlignHCenter)
        option.setWrapMode(QTextOption.WrapAtWordBoundaryOrAnywhere)
        layout.setTextOption(option)
        layout.beginLayout()
        y = 0
        for index in range(2):
            line = layout.createLine()
            if not line.isValid():
                break
            line.setLineWidth(72)
            line.setPosition(QPoint(0, y))
            if index == 1 and line.textStart() + line.textLength() < unit_count:
                # Elide only the second line; the first line keeps ordinary word wrapping.
                remaining = text_units[line.textStart() * 2:].decode('utf-16-le')
                painter.drawText(QRect(4, 50 + y, 72, int(line.height())), Qt.AlignTop | Qt.AlignHCenter,
                                 painter.fontMetrics().elidedText(remaining, Qt.ElideRight, 72))
                break
            y += line.height()
        layout.endLayout()
        painter.save()
        painter.setClipRect(QRect(4, 50, 72, 34))
        # Draw manually so the final elided line does not get painted twice.
        for index in range(layout.lineCount()):
            line = layout.lineAt(index)
            if index == 1 and line.textStart() + line.textLength() < unit_count:
                continue
            line.draw(painter, QPoint(4, 50))
        painter.restore()

    def mousePressEvent(self, event):
        self.press_position = event.position().toPoint()
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if (self.editing and event.buttons() & Qt.LeftButton
                and (event.position().toPoint() - self.press_position).manhattanLength() >= QApplication.startDragDistance()):
            self.start_drag()
            return
        super().mouseMoveEvent(event)

    def start_drag(self):
        if self.kind == 'group' and self.drag_handler:
            self.drag_handler.set_dragging(True)
        drag = QDrag(self)
        mime = QMimeData()
        mime.setData(MIME_TYPE, json.dumps({'session': self.session, 'path': self.path}).encode())
        drag.setMimeData(mime)
        self.active_drag = drag
        self.set_drag_caption('Move group' if self.kind == 'group' else 'Move item')
        drag.setHotSpot(QPoint(8, 32))
        try:
            drag.exec(Qt.MoveAction)
        finally:
            self.active_drag = None
            self.drag_caption = ''
            if self.kind == 'group' and self.drag_handler:
                self.drag_handler.set_dragging(False)
            try:
                self.setDown(False)
            except RuntimeError:
                # A successful drop can rebuild/delete its origin during QDrag.exec.
                pass

    def set_drag_caption(self, caption):
        if self.active_drag and caption != self.drag_caption:
            self.drag_caption = caption
            self.active_drag.setPixmap(ItemDrag.preview(self.item, self.mode, caption, self))

    def _source(self, event):
        return ItemDrag.source(event.mimeData(), self.session) if self.editing else None

    def _drop_feedback(self, event):
        """Use a cell midpoint outside semantic group containers."""
        fraction = event.position().y() / max(1, self.height())
        if self.mode != 1 and self.kind == 'item':
            fraction = event.position().x() / max(1, self.width())
        return 'after' if fraction > 0.5 else 'before'

    def dragEnterEvent(self, event):
        self.dragMoveEvent(event)

    def dragMoveEvent(self, event):
        if self.drag_handler:
            self.drag_handler.drag_move(self, event)
            return
        if self._source(event) is not None:
            self.drop_mode = self._drop_feedback(event)
            sender = event.source()
            if hasattr(sender, 'set_drag_caption'):
                sender.set_drag_caption(f"Move {self.drop_mode} {self.item.get('Name', 'item')}")
            self.update()
            event.acceptProposedAction()

    def dragLeaveEvent(self, event):
        if self.drag_handler:
            self.drag_handler.clear_drop()
        self.drop_mode = None
        self.update()
        event.accept()

    def dropEvent(self, event):
        if self.drag_handler:
            self.drag_handler.drop(self, event)
            return
        source = self._source(event)
        intent = self._drop_feedback(event)
        self.drop_mode = None
        self.update()
        if source is not None:
            self.moved.emit(source, self.path, intent == 'after', intent == 'into')
            event.acceptProposedAction()
