"""One intrinsic native item surface for both flyout and regular editor hosts."""
from uuid import uuid4
from dataclasses import replace

from PySide6.QtCore import QSize, Qt, Signal
from PySide6.QtGui import QFont, QFontMetricsF
from PySide6.QtWidgets import QLabel, QMenu, QToolButton, QWidget

from .item_button import ItemButton, MIME_TYPE
from .item_geometry import LauncherGeometry
from .item_drag import ItemDropArea
from .item_group import ItemGroup


class LauncherItemsView(QWidget):
    """Use saved source paths with the same content slots, packing and fonts in each host."""
    activated = Signal(object)
    edit_requested = Signal(object)
    remove_requested = Signal(object)
    step_requested = Signal(object, int)
    move_requested = Signal(object, object, bool, bool)
    column_removed = Signal(int)
    append_requested = Signal(object, object)
    transfer_requested = Signal(object, str)
    external_drop_requested = Signal(object, object)
    icons_per_row_requested = Signal(int)
    content_size_changed = Signal(bool)

    def __init__(self, launcher, editing=False, show_other=False, parent=None, *, resize_grips=True):
        super().__init__(parent)
        self.launcher = launcher
        self.editing = editing
        self.move_destinations = lambda path: []
        self.show_other = bool(editing and show_other)
        self.session = str(uuid4())
        self.rendered_paths = set()
        self.group_containers = {}
        self.metrics = LauncherGeometry(launcher, self._measure, editing, self.show_other)
        self.setFixedSize(self.metrics.width, self.metrics.height)
        top = 8
        if launcher.get('ShowTitle'):
            label = QLabel(launcher.get('Name', ''), self)
            label.setTextFormat(Qt.PlainText)
            label.setAlignment(Qt.AlignCenter)
            font = QFont(label.font())
            font.setPixelSize(12)
            font.setWeight(QFont.DemiBold)
            label.setFont(font)
            label.setGeometry(16, top, self.width() - 32, 24)
            top += 32
        if editing and len(self.metrics.columns) > 1:
            for column in self.metrics.columns:
                remove = QToolButton(self)
                remove.setAutoRaise(True)
                remove.setText('×')
                remove.setToolTip('Remove column and keep its items')
                remove.setGeometry(column.x + 2, top + 2, 20, 20)
                remove.clicked.connect(lambda checked=False, index=column.index: self.column_removed.emit(index))
                label = QLabel(f'Column {column.index + 1}', self)
                label.setAlignment(Qt.AlignCenter)
                font = QFont(label.font())
                font.setPixelSize(11)
                label.setFont(font)
                label.setGeometry(column.x + 24, top, max(1, column.width - 26), 24)
        if editing:
            # Collection zones sit behind cells, so blank columns/groups remain usable targets.
            body_top = top + (24 if len(self.metrics.columns) > 1 else 0)
            for column in self.metrics.columns:
                area = ItemDropArea(self.session, {'column': column.index}, self.append_requested.emit, self)
                area.setGeometry(column.x, body_top, column.width, max(1, self.height() - body_top - 8))
        # Real group widgets own their complete bounds; columns remain the blank-space targets.
        for group in sorted(self.metrics.groups, key=lambda descriptor: len(descriptor.path)):
            owner = self.group_containers.get(group.path[:-1])
            widget = ItemGroup(group, self, owner)
            origin_x = owner.descriptor.x if owner else 0
            origin_y = owner.descriptor.y if owner else 0
            widget.setGeometry(group.x - origin_x, group.y - origin_y, group.width, group.height)
            self.group_containers[group.path] = widget
        for placement in self.metrics.entries:
            owner = self.group_containers.get(placement.path if placement.kind == 'group' else placement.path[:-1])
            local = replace(placement, x=placement.x - owner.descriptor.x, y=placement.y - owner.descriptor.y) if owner else placement
            button = ItemButton(local, self.metrics.mode, self.session, editing, owner or self)
            if owner:
                button.drag_handler = owner
                if placement.kind == 'group':
                    owner.header = button
                else:
                    owner.child_buttons.append(button)
            self.rendered_paths.add(placement.path)
            button.moved.connect(self.move_requested)
            button.edit_clicked.connect(lambda path=placement.path: self.edit_requested.emit(list(path)))
            if placement.kind == 'item' and not editing:
                button.clicked.connect(lambda checked=False, item=placement.item: self.activated.emit(item))
            if editing:
                button.setContextMenuPolicy(Qt.CustomContextMenu)
                button.customContextMenuRequested.connect(lambda point, cell=button: self._menu(cell, point))
        for group in self.group_containers.values():
            group.finish()
        if editing:
            from .external_drop import ExternalDropFilter
            self.external_drop_filter = ExternalDropFilter(self)
        if not self.metrics.entries:
            text = 'This launcher is empty.\nAdd your first item with + above.' if editing else 'This launcher is empty.\nHover here and click the pencil to add items.'
            label = QLabel(text, self)
            label.setWordWrap(True)
            label.setAlignment(Qt.AlignCenter)
            label.setGeometry(16, top, max(1, self.width() - 32), 56)
        self.resize_grips = []
        self.resize_preview = None
        if editing and self.metrics.mode != 1 and resize_grips:
            from .item_resize import IconResizeGrip
            self.resize_grips = [IconResizeGrip(self, True), IconResizeGrip(self, False)]
            self.position_resize_grips()

    def position_resize_grips(self):
        for grip in self.resize_grips:
            grip.setGeometry(0 if grip.left else self.width() - 8, 0, 8, self.height())
            grip.raise_()

    def preview_icon_count(self, count, keep_right):
        """Render transient geometry without replacing the captured gesture or saved source."""
        if self.resize_preview:
            self.resize_preview.hide()
            self.resize_preview.deleteLater()
            self.resize_preview = None
        if count is None:
            for widget in getattr(self, '_resize_hidden', []):
                widget.show()
            self.setFixedSize(self.metrics.width, self.metrics.height)
        else:
            if not hasattr(self, '_resize_hidden'):
                self._resize_hidden = [widget for widget in self.findChildren(QWidget, options=Qt.FindDirectChildrenOnly)
                                       if widget not in self.resize_grips and not widget.isHidden()]
            for widget in self._resize_hidden:
                widget.hide()
            candidate = dict(self.launcher, IconModeIconsPerRow=count)
            self.resize_preview = LauncherItemsView(candidate, True, True, self, resize_grips=False)
            self.resize_preview.setAttribute(Qt.WA_TransparentForMouseEvents)
            self.resize_preview.move(0, 0)
            self.setFixedSize(self.resize_preview.size())
            self.resize_preview.show()
        self.position_resize_grips()
        self.content_size_changed.emit(keep_right)

    def source_item(self, path):
        rows = self.launcher['Items']
        for index in path[:-1]:
            rows = rows[index]['Children']
        return rows[path[-1]]

    def _measure(self, text, pixels, semibold):
        """Measure native-font strings, never content widgets whose layout could feed back."""
        font = QFont(self.font())
        font.setPixelSize(pixels)
        font.setWeight(QFont.DemiBold if semibold else QFont.Normal)
        return QFontMetricsF(font).horizontalAdvance(text)

    def sizeHint(self):
        return QSize(self.metrics.width, self.metrics.height)

    def _menu(self, button, position):
        menu = QMenu(self)
        menu.addAction('Rename group' if button.kind == 'group' else 'Edit',
                       lambda: self.edit_requested.emit(button.path))
        menu.addAction('Move up', lambda: self.step_requested.emit(button.path, -1))
        menu.addAction('Move down', lambda: self.step_requested.emit(button.path, 1))
        move = menu.addMenu('Move to…')
        destinations = self.move_destinations(button.path)
        move.setEnabled(bool(destinations))
        for label, destination in destinations:
            if isinstance(destination, str):
                move.addAction(label, lambda target=destination: self.transfer_requested.emit(button.path, target))
            else:
                move.addAction(label, lambda target=destination: self.append_requested.emit(button.path, target))
        menu.addSeparator()
        menu.addAction('Remove', lambda: self.remove_requested.emit(button.path))
        menu.exec(button.mapToGlobal(position))
