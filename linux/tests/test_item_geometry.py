"""Windows-reference layout contracts, independent of native colors and font metrics."""
import unittest
from pathlib import Path
import re

from little_launcher.item_geometry import LauncherGeometry, group_drop_zone


class ShortcutGeometryTests(unittest.TestCase):
    @staticmethod
    def measure(text, pixels, semibold):
        return len(text) * 6

    def test_dimensions_track_the_current_windows_source_contract(self):
        root = Path(__file__).resolve().parents[2] / 'LittleLauncher/Controls'
        rendering = (root / 'LauncherItemsView.Rendering.cs').read_text(encoding='utf-8-sig')
        editing = (root / 'LauncherItemsView.Editing.cs').read_text(encoding='utf-8-sig')
        def constant(name, source=rendering):
            match = re.search(r'\b' + name + r'\s*=\s*(\d+)\s*;', source)
            self.assertIsNotNone(match, name)
            return int(match.group(1))
        self.assertEqual(LauncherGeometry.LARGE_CELL, (constant('IconCellWidth'), constant('IconCellHeight')))
        self.assertEqual(LauncherGeometry.SMALL_CELL, (constant('SmallIconCellWidth'), constant('SmallIconCellHeight')))
        self.assertEqual(LauncherGeometry.OUTER, constant('FlyoutOuterPadding'))
        self.assertEqual(LauncherGeometry.LIST_ROW, constant('itemHeight'))
        self.assertEqual(LauncherGeometry.GROUP_HEADER, constant('groupHeight'))
        self.assertEqual(LauncherGeometry.ICON_GROUP_HEADER, constant('IconGroupHeaderHeight'))
        self.assertEqual(LauncherGeometry.COLUMN_HEADER, constant('ColumnHeaderHeight', editing))

    def test_reference_cells_column_chrome_and_list_rows(self):
        for mode, width, height in ((0, 276, 100), (1, 96, 80), (2, 152, 56)):
            with self.subTest(mode=mode):
                geometry = LauncherGeometry({'ViewMode': mode, 'Items': [{'Name': 'App', 'Path': '/app'}]}, self.measure)
                cell = geometry.entries[0]
                self.assertEqual((geometry.width, geometry.height), (width, height))
                self.assertEqual((cell.width, cell.height), (80, 84) if mode == 0 else (80, 32) if mode == 1 else (40, 40))

    def test_small_groups_share_an_icon_row_instead_of_becoming_boxed_full_width_sections(self):
        groups = [{'Name': name, 'IsGroup': True, 'Children': [{'Name': name, 'Path': '/app'}]}
                  for name in ('One', 'Two', 'Three')]
        geometry = LauncherGeometry({'ViewMode': 0, 'Items': groups}, self.measure)
        headers = [entry for entry in geometry.entries if entry.kind == 'group']
        self.assertEqual([entry.x for entry in headers], [18, 98, 178])
        self.assertEqual([entry.y for entry in headers], [8, 8, 8])
        self.assertEqual(geometry.height, 130)

    def test_entering_edit_mode_keeps_width_and_cell_sizes_and_only_adds_column_headers(self):
        for mode in (0, 1, 2):
            launcher = {'ViewMode': mode, 'Items': [{'Name': 'App', 'Path': '/app'},
                       {'IsColumnBreak': True}, {'Name': 'Other', 'Path': '/app'}]}
            normal = LauncherGeometry(launcher, self.measure)
            editing = LauncherGeometry(launcher, self.measure, editing=True)
            self.assertEqual(normal.width, editing.width)
            for before, after in zip(normal.entries, editing.entries):
                self.assertEqual((before.path, before.x, before.width, before.height),
                                 (after.path, after.x, after.width, after.height))
                self.assertEqual(after.y - before.y, 24)

    def test_hidden_rows_keep_saved_paths_and_do_not_change_other_cell_geometry(self):
        launcher = {'ViewMode': 1, 'Items': [{'Name': 'Foreign', 'Path': 'C:\\app.exe'},
                   {'Name': 'App', 'Path': '/app'}]}
        normal = LauncherGeometry(launcher, self.measure)
        editing = LauncherGeometry(launcher, self.measure, editing=True)
        self.assertEqual([entry.path for entry in normal.entries], [(0,),(1,)])
        self.assertEqual(normal.width, editing.width)
        self.assertEqual(normal.entries[0], editing.entries[0])


class GroupContainerGeometryTests(unittest.TestCase):
    def test_outer_bands_are_measured_against_the_whole_group_not_its_heading(self):
        self.assertEqual(group_drop_zone(40, 7, 160, 114), 'before')
        self.assertEqual(group_drop_zone(40, 20, 160, 114), 'inside')
        self.assertEqual(group_drop_zone(40, 98, 160, 114), 'inside')
        self.assertEqual(group_drop_zone(40, 108, 160, 114), 'after')
        self.assertEqual(group_drop_zone(4, 5, 16, 12), 'inside')
        for values in [(1, 1, 0, 100), (1, 1, float('inf'), 100), (float('nan'), 1, 80, 100), (80, 1, 80, 100)]:
            self.assertIsNone(group_drop_zone(*values))

    def test_group_bounds_include_its_children_and_keep_reference_cell_geometry(self):
        for mode, cell_height in [(0, 84), (1, 32), (2, 40)]:
            launcher = {'ViewMode': mode, 'Items': [{'Name': 'Group', 'IsGroup': True, 'Children': [{'Name': 'App', 'Path': '/app'}]}]}
            geometry = LauncherGeometry(launcher, lambda *_: 20, editing=True)
            group = geometry.groups[0]
            self.assertEqual(group.height, group.header_height + cell_height)
            self.assertEqual(geometry.entries[1].y, group.y + group.header_height)
            self.assertEqual(geometry.entries[1].height, cell_height)
