"""Session-bound internal item drags, shared by cells and empty collection targets."""
import json

from PySide6.QtCore import QMimeData, QRect, Qt
from PySide6.QtGui import QFont, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import QWidget

MIME_TYPE = 'application/x-little-launcher-item'


class ItemDrag:
    @staticmethod
    def preview(item, mode, caption, widget):
        """Bounded Windows-reference identity card; group drags identify the full collection."""
        group = bool(item.get('IsGroup'))
        width, body_height = ((136, 36) if group else (128, 28)) if mode == 2 else (176, 44) if mode == 0 else (156, 40) if group else (156, 32)
        ratio = widget.devicePixelRatioF()
        image = QPixmap(round(width * ratio), round((body_height + 24) * ratio))
        image.setDevicePixelRatio(ratio)
        image.fill(Qt.transparent)
        painter = QPainter(image)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setPen(Qt.NoPen)
        painter.setBrush(widget.palette().window())
        painter.drawRoundedRect(QRect(0, 0, width, 20), 3, 3)
        painter.drawRoundedRect(QRect(0, 24, width, body_height), 4, 4)
        painter.setPen(widget.palette().highlight().color())
        painter.drawRoundedRect(QRect(0, 24, width - 1, body_height - 1), 4, 4)
        font = QFont(widget.font())
        font.setPixelSize(11)
        painter.setFont(font)
        painter.setPen(widget.palette().windowText().color())
        painter.drawText(QRect(6, 0, width - 12, 20), Qt.AlignVCenter,
                         painter.fontMetrics().elidedText(caption, Qt.ElideRight, width - 12))
        from .desktop import item_icon
        icon = QIcon.fromTheme('folder') if group else item_icon(item)
        icon.paint(painter, QRect(6, 24 + (body_height - 18) // 2, 18, 18))
        font.setWeight(QFont.DemiBold)
        painter.setFont(font)
        top = 24 + (body_height - 28) // 2 if group else 24
        painter.drawText(QRect(30, top, width - 36, 14 if group else body_height), Qt.AlignVCenter,
                         painter.fontMetrics().elidedText(item.get('Name', ''), Qt.ElideRight, width - 36))
        if group:
            count = len(item.get('Children', []))
            font.setPixelSize(10)
            font.setWeight(QFont.Normal)
            painter.setFont(font)
            painter.drawText(QRect(30, top + 15, width - 36, 13), Qt.AlignVCenter,
                             f"Group · {count} item{'s' if count != 1 else ''}")
        painter.end()
        return image

    @staticmethod
    def source(mime, session):
        """Reject foreign, malformed and pre-rebuild drag payloads before targeting source paths."""
        if not mime.hasFormat(MIME_TYPE):
            return None
        try:
            raw = bytes(mime.data(MIME_TYPE))
            if len(raw) > 4096:
                return None
            value = json.loads(raw)
            if not isinstance(value, dict):
                return None
            path = value.get('path')
            if (value.get('session') == session and isinstance(path, list) and path
                    and all(type(index) is int and index >= 0 for index in path)):
                return path
        except (ValueError, UnicodeError):
            pass
        return None


class ItemDropArea(QWidget):
    """Invisible collection target; hover paint never changes the intrinsic item geometry."""

    def __init__(self, session, destination, callback, parent):
        super().__init__(parent)
        self.session = session
        self.destination = destination
        self.callback = callback
        self.hovered = False
        self.setAcceptDrops(True)

    def dragEnterEvent(self, event):
        self.dragMoveEvent(event)

    def dragMoveEvent(self, event):
        if ItemDrag.source(event.mimeData(), self.session) is not None:
            self.hovered = True
            sender = event.source()
            if hasattr(sender, 'set_drag_caption'):
                sender.set_drag_caption('Move to end')
            self.update()
            event.acceptProposedAction()

    def dragLeaveEvent(self, event):
        self.hovered = False
        self.update()
        event.accept()

    def dropEvent(self, event):
        source = ItemDrag.source(event.mimeData(), self.session)
        self.hovered = False
        self.update()
        if source is not None:
            self.callback(source, self.destination)
            event.acceptProposedAction()

    def paintEvent(self, event):
        if self.hovered:
            from PySide6.QtGui import QPainter
            painter = QPainter(self)
            color = self.palette().highlight().color()
            painter.setPen(color)
            color.setAlphaF(0.08)
            painter.setBrush(color)
            painter.drawRect(self.rect().adjusted(1, 1, -2, -2))
