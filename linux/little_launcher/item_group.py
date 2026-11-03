"""Real group containers with full-bounds targeting and non-reflowing feedback."""
from PySide6.QtCore import QPoint, QRect, Qt
from PySide6.QtGui import QPainter
from PySide6.QtWidgets import QApplication, QWidget
from .item_drag import ItemDrag
from .item_geometry import group_drop_zone


class GroupOverlay(QWidget):
    """Passive foreground feedback; never changes dimensions or takes pointer input."""
    def __init__(self, group):
        super().__init__(group)
        self.group = group
        self.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.setFocusPolicy(Qt.NoFocus)
        self.setGeometry(group.rect())

    def paintEvent(self, event):
        group = self.group
        if not group.editing or not (group.header_hover or group.intent or group.dragging):
            return
        painter = QPainter(self)
        accent = self.palette().highlight().color()
        if group.intent:
            mode = group.intent[0]
            if mode in ('before', 'after'):
                painter.setPen(accent)
                y = 1 if mode == 'before' else self.height() - 2
                painter.drawLine(0, y, self.width(), y)
                return
            fill = self.palette().highlight().color()
            fill.setAlphaF(.1)
            painter.fillRect(self.rect().adjusted(1, 1, -1, -1), fill)
        else:
            accent.setAlphaF(1 if group.dragging else .5)
        painter.setPen(accent)
        painter.drawRoundedRect(self.rect().adjusted(1, 1, -2, -2), 3, 3)
        if group.intent:
            mode, target, after = group.intent
            if mode == 'child' and not (after and target is group.child_buttons[-1]):
                rect = target.geometry()
                if group.mode == 1 or target.kind != 'item':
                    y = rect.bottom() if after else rect.top()
                    painter.drawLine(rect.left(), y, rect.right(), y)
                else:
                    x = rect.right() if after else rect.left()
                    painter.drawLine(x, rect.top(), x, rect.bottom())
            if mode == 'append' or (mode == 'child' and after and target is group.child_buttons[-1]):
                # End-of-children uses an inset cue inside the last child, not a sibling boundary.
                rect = group.child_buttons[-1].geometry() if group.child_buttons else self.rect().adjusted(0, group.descriptor.header_height, 0, 0)
                inset = min(8, rect.width() // 4)
                painter.drawLine(rect.left() + inset, rect.bottom() - min(8, rect.height() // 3),
                                 rect.right() - inset, rect.bottom() - min(8, rect.height() // 3))


class ItemGroup(QWidget):
    """Header and children belong to one semantic group; the header is its drag handle."""
    def __init__(self, descriptor, view, parent=None):
        super().__init__(parent or view)
        self.descriptor = descriptor
        self.path = list(descriptor.path)
        self.item = descriptor.item
        self.view = view
        self.mode = view.metrics.mode
        self.editing = view.editing
        self.session = view.session
        self.header = None
        self.child_buttons = []
        self.header_hover = False
        self.dragging = False
        self.intent = None
        self.setAcceptDrops(self.editing)
        self.setMouseTracking(self.editing)
        self.setAccessibleName(self.item.get('Name', 'Group'))
        self.setAccessibleDescription('Group containing launcher items')
        self.overlay = None
        self.press_position = QPoint()

    def finish(self):
        if self.editing:
            self.setContextMenuPolicy(Qt.CustomContextMenu)
            self.customContextMenuRequested.connect(lambda point: self.view._menu(self.header, self.mapTo(self.header, point)))
        self.overlay = GroupOverlay(self)
        self.overlay.raise_()
        self.overlay.show()

    def set_dragging(self, active):
        self.dragging = active
        if active and self.header:
            self.header.pencil.hide()
        if self.overlay:
            try:
                self.overlay.update()
            except RuntimeError:
                # Successful drops can dispose this old source container during QDrag.exec.
                pass

    def hover(self, header):
        self.header_hover = bool(header and self.editing)
        if not self.header_hover and self.header:
            self.header.pencil.hide()
            self.header.update()
        if self.overlay:
            self.overlay.update()

    def mousePressEvent(self, event):
        if self.editing and event.button() == Qt.LeftButton:
            self.press_position = event.position().toPoint()
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if (self.editing and self.header and event.buttons() & Qt.LeftButton
                and (event.position().toPoint() - self.press_position).manhattanLength() >= QApplication.startDragDistance()):
            self.header.start_drag()
            return
        if self.editing:
            self.hover(True)
            if self.header:
                self.header.pencil.show()
        super().mouseMoveEvent(event)

    def leaveEvent(self, event):
        self.hover(False)
        if self.header:
            self.header.pencil.hide()
        super().leaveEvent(event)

    @staticmethod
    def zone(x, y, width, height):
        """Windows LauncherDropGeometry: up to8px outer bands belong to the column."""
        return group_drop_zone(x, y, width, height)

    def resolve(self, source, point):
        try:
            item = self.view.source_item(source)
        except (IndexError, KeyError, TypeError):
            return None
        if source == self.path or self.path[:len(source)] == source:
            return None
        zone = self.zone(point.x(), point.y(), self.width(), self.height())
        if zone is None:
            return None
        if item.get('IsGroup'):
            # Groups never nest: the whole destination container supplies the midpoint.
            return ('after' if point.y() >= self.height() / 2 else 'before', None, False)
        if zone != 'inside':
            return (zone, None, False)
        for child in self.child_buttons:
            if child.geometry().contains(point):
                local = point - child.pos()
                after = local.x() >= child.width() / 2 if self.mode != 1 and child.kind == 'item' else local.y() >= child.height() / 2
                return ('child', child, after)
        return ('append', None, False)

    def caption(self, intent):
        mode, child, after = intent
        name = self.item.get('Name', 'Group')
        if mode in ('before', 'after'):
            return f'Move {mode} {name}'
        if mode == 'child':
            return f"Into {name}: {'after' if after else 'before'} {child.item.get('Name', 'item')}"
        return f'Into {name}: at end'

    def drag_move(self, widget, event):
        source = ItemDrag.source(event.mimeData(), self.session) if self.editing else None
        intent = self.resolve(source, widget.mapTo(self, event.position().toPoint())) if source is not None else None
        self.intent = intent
        self.header_hover = False
        if self.overlay:
            self.overlay.update()
        if intent is None:
            event.ignore()
            return
        sender = event.source()
        if hasattr(sender, 'set_drag_caption'):
            sender.set_drag_caption(self.caption(intent))
        event.acceptProposedAction()

    def clear_drop(self):
        self.intent = None
        if self.overlay:
            self.overlay.update()

    def drop(self, widget, event):
        source = ItemDrag.source(event.mimeData(), self.session) if self.editing else None
        intent = self.resolve(source, widget.mapTo(self, event.position().toPoint())) if source is not None else None
        self.clear_drop()
        if intent is None:
            event.ignore()
            return
        mode, child, after = intent
        if mode in ('before', 'after'):
            self.view.move_requested.emit(source, self.path, mode == 'after', False)
        elif mode == 'child':
            self.view.move_requested.emit(source, child.path, after, False)
        else:
            self.view.append_requested.emit(source, {'group': self.path})
        event.acceptProposedAction()

    def dragEnterEvent(self, event): self.drag_move(self, event)
    def dragMoveEvent(self, event): self.drag_move(self, event)
    def dragLeaveEvent(self, event): self.clear_drop(); event.accept()
    def dropEvent(self, event): self.drop(self, event)
