"""Windows-reference shortcut geometry, independent of toolkit decoration and persistence."""
from dataclasses import dataclass
from math import ceil, isfinite

from .document import is_heading


def group_drop_zone(x, y, width, height):
    """Full-container Windows policy: outer8px insert beside; the interior owns children."""
    if not all(isfinite(value) for value in (x, y, width, height)) or width <= 0 or height <= 0 or not 0 <= x < width or not 0 <= y < height:
        return None
    edge = min(8, height / 4)
    return 'before' if y < edge else 'after' if y >= height - edge else 'inside'


@dataclass
class ItemPlacement:
    item: dict
    path: tuple
    kind: str
    x: int
    y: int
    width: int
    height: int


@dataclass
class GroupPlacement:
    item: dict
    path: tuple
    x: int
    y: int
    width: int
    height: int
    header_height: int


@dataclass
class ColumnPlacement:
    index: int
    x: int
    width: int


class LauncherGeometry:
    """Compute one intrinsic layout shared by the native flyout and regular editor.

    Constants follow LauncherItemsView.Rendering.cs and its XAML templates. The
    caller measures label strings with the active native font, never laid-out widgets.
    Source paths always address the complete saved collection, including hidden rows.
    """

    OUTER = 8
    LIST_ROW = 32
    LIST_CHROME = 48
    GROUP_HEADER = 24
    ICON_GROUP_HEADER = 30
    COLUMN_HEADER = 24
    LARGE_CELL = (80, 84)
    SMALL_CELL = (40, 40)

    def __init__(self, launcher, measure, editing=False, show_other=False):
        self.launcher = launcher
        self.editing = editing
        self.show_other = show_other
        mode = launcher.get('ViewMode', 0)
        self.mode = mode if mode in (0, 1, 2) else 0
        self.per_row = max(1, min(12, launcher.get('IconModeIconsPerRow', 3)))
        self.entries = []
        self.drop_areas = []
        self.groups = []
        self.columns = []
        columns = [[]]
        for index, item in enumerate(launcher.get('Items', [])):
            if item.get('IsColumnBreak'):
                columns.append([])
            else:
                columns[-1].append((item, (index,)))
        projected = [self._visible(rows) for rows in columns]
        retained = [(index, rows) for index, rows in enumerate(projected)
                    if rows or (editing and not columns[index])] or [(0, [])]
        projected = [rows for _, rows in retained]
        self.list_width = self._list_width(projected, measure)
        self.column_width = (self.list_width if self.mode == 1 else
                             self.per_row * self.cell_width + (16 if self.mode == 2 else 20))
        title_height = 32 if launcher.get('ShowTitle') else 0
        column_header = self.COLUMN_HEADER if editing and len(retained) > 1 else 0
        top = self.OUTER + title_height + column_header
        tallest = 0
        for position, (index, rows) in enumerate(retained):
            x = self.OUTER + position * self.column_width
            self.columns.append(ColumnPlacement(index, x, self.column_width))
            height = self._list(rows, x, top) if self.mode == 1 else self._icons(rows, x, top)
            tallest = max(tallest, height)
        minimum = 52 if self.mode == 2 else 80
        self.width = 2 * self.OUTER + len(retained) * self.column_width
        self.height = max(minimum, top + tallest + self.OUTER + (2 if self.mode == 1 else 0))
        if not self.entries:
            self.height = max(self.height, top + 56 + self.OUTER)

    @property
    def cell_width(self):
        return (self.SMALL_CELL if self.mode == 2 else self.LARGE_CELL)[0]

    @property
    def cell_height(self):
        return (self.SMALL_CELL if self.mode == 2 else self.LARGE_CELL)[1]

    def _visible(self, rows):
        """Filter eligibility without renumbering source paths or serializing a projection."""
        result = []
        pending_heading = None
        for item, path in rows:
            children = self._visible([(child, (*path, index))
                                      for index, child in enumerate(item.get('Children', []))]) if item.get('IsGroup') else []
            if item.get('IsGroup') and not children:
                if item.get('Children') and not self.show_other:
                    continue
                if not self.editing:
                    continue
            row = (item, path, children)
            if is_heading(item) and not self.editing:
                pending_heading = row
                continue
            if pending_heading:
                result.append(pending_heading)
                pending_heading = None
            result.append(row)
        return result

    def _list_width(self, columns, measure):
        widest = 0
        def consider(rows):
            nonlocal widest
            for item, _, children in rows:
                header = item.get('IsGroup') or is_heading(item)
                chrome = 16 if header else self.LIST_CHROME
                widest = max(widest, measure(item.get('Name', ''), 12 if header else 13, header) + chrome)
                consider(children)
        for rows in columns:
            consider(rows)
        if self.launcher.get('ShowTitle'):
            widest = max(widest, (measure(self.launcher.get('Name', ''), 12, True) + 16) / max(1, len(columns)))
        return max(80, min(175, ceil(widest))) if widest else 175

    def _add(self, item, path, kind, x, y, width, height):
        self.entries.append(ItemPlacement(item, path, kind, int(x), int(y), int(width), int(height)))

    def _list(self, rows, x, top):
        y = top
        for item, path, children in rows:
            if item.get('IsGroup'):
                group_top = y
                self._add(item, path, 'group', x, y, self.column_width, self.GROUP_HEADER)
                y += self.GROUP_HEADER
                child_height = self._list(children, x, y) if children else self.LIST_ROW
                self.groups.append(GroupPlacement(item, path, x, group_top, self.column_width, self.GROUP_HEADER + child_height, self.GROUP_HEADER))
                self.drop_areas.append(({'group': list(path)}, x, y, self.column_width, child_height))
                y += child_height
            else:
                self._add(item, path, 'heading' if is_heading(item) else 'item', x, y,
                          self.column_width, self.LIST_ROW)
                y += self.LIST_ROW
        return y - top

    def _icons(self, rows, x, top):
        """Pack real groups and uninterrupted loose-item runs into the same cell rows."""
        units = []
        loose = []
        for row in rows:
            item = row[0]
            if item.get('IsGroup') or is_heading(item):
                if loose:
                    units.append((None, loose))
                    loose = []
                units.append((row, row[2]))
            else:
                loose.append(row)
        if loose:
            units.append((None, loose))
        used = 0
        row_height = 0
        y = top
        inset = (self.column_width - self.per_row * self.cell_width) // 2
        for header, children in units:
            count = len(children)
            span = min(self.per_row, max(1, count))
            heading_only = header is not None and is_heading(header[0])
            if heading_only:
                span = self.per_row
            header_height = self.ICON_GROUP_HEADER if header else 0
            height = header_height + (0 if heading_only else max(1, ceil(count / self.per_row)) * self.cell_height)
            if used and used + span > self.per_row:
                y += row_height
                used = 0
                row_height = 0
            left = x + inset + used * self.cell_width
            if header:
                self._add(header[0], header[1], 'heading' if heading_only else 'group',
                          left, y, span * self.cell_width, header_height)
            if header and not heading_only:
                self.groups.append(GroupPlacement(header[0], header[1], left, y, span * self.cell_width, height, header_height))
                self.drop_areas.append(({'group': list(header[1])}, left, y + header_height,
                                        span * self.cell_width, height - header_height))
            for index, (item, path, _) in enumerate(children):
                self._add(item, path, 'heading' if is_heading(item) else 'item', left + index % self.per_row * self.cell_width,
                          y + header_height + index // self.per_row * self.cell_height,
                          self.cell_width, self.cell_height)
            used += span
            row_height = max(row_height, height)
        return y + row_height - top
