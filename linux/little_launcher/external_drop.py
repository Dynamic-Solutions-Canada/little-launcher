"""External URI drops create shortcut references, never move files or execute their contents."""
from pathlib import Path
from PySide6.QtCore import QEvent, QObject, QPoint, QRect, Qt
from PySide6.QtWidgets import QWidget
from .item_drag import MIME_TYPE


def dropped_items(mime):
    if mime.hasFormat(MIME_TYPE):
        return []  # A stale/foreign internal drag must not become an external import.
    urls = mime.urls() if mime.hasUrls() else []
    if not urls and mime.hasText():
        from PySide6.QtCore import QUrl
        text = mime.text().strip()
        if text.casefold().startswith(('https://', 'http://')) and '\n' not in text:
            urls = [QUrl(text)]
    if not urls or len(urls) > 64:
        return []
    result = []
    for url in urls:
        if not url.isValid() or len(url.toString()) > 8192 or '\0' in url.toString():
            return []
        if url.isLocalFile() and url.host() in ('', 'localhost'):
            path = Path(url.toLocalFile())
            if '\0' in str(path) or not path.is_absolute() or not path.exists():
                return []
            result.append({'Name': path.name or str(path), 'Path': str(path), 'IsWebsite': False})
        elif url.scheme() in ('http', 'https') and url.host():
            result.append({'Name': url.host(), 'Path': url.toString(), 'IsWebsite': True})
        else:
            return []
    return result


class ExternalDropFilter(QObject):
    """Give both native hosts the same full-source insertion target for external URI drops."""
    def __init__(self, view):
        super().__init__(view)
        self.view = view
        self.feedback = []
        for widget in [view, *view.findChildren(QWidget)]:
            if widget is view or widget.acceptDrops():
                widget.setAcceptDrops(True)
                widget.installEventFilter(self)

    def destination(self, point):
        view = self.view
        for group in sorted(view.metrics.groups, key=lambda value: len(value.path), reverse=True):
            if not QRect(group.x, group.y, group.width, group.height).contains(point):
                continue
            local = point - QPoint(group.x, group.y)
            from .item_geometry import group_drop_zone
            zone = group_drop_zone(local.x(), local.y(), group.width, group.height)
            if zone in ('before', 'after'):
                return {'target': list(group.path), 'after': zone == 'after'}
            for child in view.metrics.entries:
                if child.path[:-1] != group.path or child.kind == 'group':
                    continue
                if QRect(child.x, child.y, child.width, child.height).contains(point):
                    after = point.x() >= child.x + child.width / 2 if view.metrics.mode != 1 and child.kind == 'item' else point.y() >= child.y + child.height / 2
                    return {'target': list(child.path), 'after': after}
            return {'group': list(group.path)}
        for item in view.metrics.entries:
            if QRect(item.x, item.y, item.width, item.height).contains(point):
                after = point.x() >= item.x + item.width / 2 if view.metrics.mode != 1 and item.kind == 'item' else point.y() >= item.y + item.height / 2
                return {'target': list(item.path), 'after': after}
        for column in view.metrics.columns:
            if column.x <= point.x() < column.x + column.width:
                return {'column': column.index}
        return None

    def clear_feedback(self):
        for widget in self.feedback:
            if hasattr(widget, 'clear_drop'):
                widget.clear_drop()
            elif hasattr(widget, 'drop_mode'):
                widget.drop_mode = None
                widget.update()
            else:
                widget.hovered = False
                widget.update()
        self.feedback = []

    def show_feedback(self, destination):
        from .item_button import ItemButton
        from .item_drag import ItemDropArea
        self.clear_feedback()
        if 'target' in destination:
            button = next((value for value in self.view.findChildren(ItemButton) if value.path == destination['target']), None)
            if button is None:
                return
            group = button.drag_handler
            if group:
                group.intent = ('after' if destination['after'] else 'before', None, False) if button.path == group.path else ('child', button, destination['after'])
                group.overlay.update()
                self.feedback = [group]
            else:
                button.drop_mode = 'after' if destination['after'] else 'before'
                button.update()
                self.feedback = [button]
        elif 'group' in destination:
            group = self.view.group_containers.get(tuple(destination['group']))
            if group:
                group.intent = ('append', None, False)
                group.overlay.update()
                self.feedback = [group]
        else:
            for area in self.view.findChildren(ItemDropArea):
                if area.destination == destination:
                    area.hovered = True
                    area.update()
                    self.feedback.append(area)

    def eventFilter(self, widget, event):
        if event.type() == QEvent.DragLeave:
            if self.feedback:
                self.clear_feedback()
            return False
        if event.type() not in (QEvent.DragEnter, QEvent.DragMove, QEvent.Drop):
            return False
        if event.mimeData().hasFormat(MIME_TYPE):
            return False
        launcher = self.view.launcher
        if launcher.get('IsShared') and not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay'):
            event.ignore();return True
        items = dropped_items(event.mimeData())
        point = widget.mapTo(self.view, event.position().toPoint())
        destination = self.destination(point) if items else None
        actions = event.possibleActions()
        action = Qt.CopyAction if actions & Qt.CopyAction else Qt.LinkAction if actions & Qt.LinkAction else None
        if destination is None or action is None:
            self.clear_feedback()
            event.ignore();return True
        event.setDropAction(action);event.accept()
        if event.type() == QEvent.Drop:
            self.clear_feedback()
            self.view.external_drop_requested.emit(items, destination)
        else:
            self.show_feedback(destination)
        return True
