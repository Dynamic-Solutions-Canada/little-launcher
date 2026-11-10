"""Native transaction regressions, run in the private KDE fixture or Qt offscreen."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

try:
    from PySide6.QtWidgets import QApplication
    from PySide6.QtGui import QColor, QIcon, QPixmap
    from little_launcher.icons import LauncherIcons
    from little_launcher.browser import BrowserHost
    from little_launcher.desktop import set_autostart
    from little_launcher.storage import LocalPreferences
    from little_launcher.controller import LauncherController
    from little_launcher.editor import ItemsEditor
    from little_launcher.forms import LauncherDialog, ShortcutDialog
    HAS_QT = True
except ImportError:
    HAS_QT = False


@unittest.skipUnless(HAS_QT, 'Run native checks in the Linux development image')
class NativeEditorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.controller = LauncherController(Path(self.temporary.name))
        self.launcher = {'Platform':'linux','Id': 'editor-test', 'Name': 'Apps', 'Items': [
            {'Name': 'Windows', 'Path': 'C:\\offline\\app.exe', 'Future': 9},
            {'Name': 'Linux', 'Path': '/not/installed', 'SupportedOperatingSystems': ['linux']},
            {'Name': 'Group', 'IsGroup': True, 'Children': [
                {'Name': 'Foreign child', 'Path': 'C:\\app.exe', 'FutureChild': True},
            ]},
        ]}
        self.assertTrue(self.controller.edit(lambda document: document['Launchers'].append(deepcopy(self.launcher))))

    def test_move_menu_targets_and_atomic_cross_launcher_transfer_preserve_hidden_data(self):
        from little_launcher.item_editing import ItemEditingSession
        from PySide6.QtWidgets import QWidget
        other = {'Id': 'other', 'Platform': 'linux', 'Name': 'Other', 'Items': [], 'Opaque': {'keep': 3}}
        web = {'Id': 'web', 'Kind': 1, 'Items': []}
        readonly = {'Id': 'readonly', 'IsShared': True, 'Items': []}
        foreign = {'Id': 'foreign', 'Platform': 'windows', 'Items': []}
        self.controller.edit(lambda doc: doc['Launchers'].extend([other, web, readonly, foreign]))
        parent = QWidget()
        session = ItemEditingSession(self.controller, self.launcher, parent)
        targets = session.destinations([1])
        self.assertIn(('Other (launcher)', 'other'), targets)
        self.assertNotIn('web', [target for _, target in targets])
        self.assertNotIn('readonly', [target for _, target in targets])
        self.assertNotIn('foreign', [target for _, target in targets])
        before = deepcopy(self.controller.document)
        session.transfer([0], 'foreign')
        self.assertEqual(self.controller.document, before)
        changes = []
        self.controller.changed.connect(lambda: changes.append(True))
        session.transfer([0], 'other')
        self.assertEqual(len(changes), 1)
        self.assertEqual(self.controller.launcher('other')['Items'], [self.launcher['Items'][0]])
        self.assertEqual(self.controller.launcher('other')['Opaque'], {'keep': 3})
        self.assertEqual(self.controller.launcher('editor-test')['Items'], self.launcher['Items'][1:])

    def test_cross_launcher_transfer_rechecks_destination_policy(self):
        from little_launcher.item_editing import ItemEditingSession
        from PySide6.QtWidgets import QWidget
        other = {'Id': 'other', 'Platform': 'linux', 'Items': []}
        self.controller.edit(lambda doc: doc['Launchers'].append(other))
        parent = QWidget()
        session = ItemEditingSession(self.controller, self.launcher, parent)
        self.controller.edit(lambda doc: doc['Launchers'][1].update(IsShared=True))
        session.transfer([1], 'other')
        self.assertEqual(self.controller.launcher('editor-test'), self.launcher)
        self.assertEqual(self.controller.launcher('other')['Items'], [])

    def test_icon_edge_resize_previews_without_saving_then_commits_full_source(self):
        from little_launcher.items_view import LauncherItemsView
        for mode in (0, 2):
            launcher = deepcopy(self.controller.launcher('editor-test'))
            launcher.update(ViewMode=mode, IconModeIconsPerRow=3)
            self.controller.replace_launcher(launcher)
            editor = ItemsEditor(self.controller, launcher)
            self.addCleanup(editor.close)
            view = editor.view
            self.assertEqual(len(view.resize_grips), 2)
            initial = deepcopy(self.controller.document)
            width = view.width()
            view.preview_icon_count(5, False)
            self.assertGreater(view.width(), width)
            self.assertEqual(self.controller.document, initial)
            self.assertEqual(view.resize_preview.metrics.per_row, 5)
            view.preview_icon_count(None, False)
            self.assertEqual(view.width(), width)
            view.icons_per_row_requested.emit(5)
            self.assertEqual(self.controller.launcher('editor-test')['IconModeIconsPerRow'], 5)
            self.assertEqual(self.controller.launcher('editor-test')['Items'], launcher['Items'])
            self.assertEqual(editor.view.metrics.per_row, 5)
        for editing, mode in ((False, 0), (True, 1)):
            self.assertFalse(LauncherItemsView(dict(self.launcher, ViewMode=mode), editing).resize_grips)

    def test_icon_resize_rejects_stale_view_and_foreign_launcher(self):
        editor = ItemsEditor(self.controller, self.launcher)
        self.addCleanup(editor.close)
        changed = deepcopy(self.launcher)
        changed['Name'] = 'Newer edit'
        self.controller.replace_launcher(changed)
        editor.view.icons_per_row_requested.emit(8)
        self.assertEqual(self.controller.launcher('editor-test'), changed)
        editor.session.original = dict(changed, Platform='windows')
        editor.session.resize_icons(6)
        self.assertEqual(self.controller.launcher('editor-test'), changed)

    def test_web_settings_share_menu_destinations_and_preserve_unknown_fields(self):
        dialog = LauncherDialog({'Id': 'web', 'Kind': 1, 'Items': [], 'Opaque': 8})
        self.addCleanup(dialog.close)
        self.assertEqual(list(dialog.section_widgets), ['General', 'Appearance', 'Size and position', 'Behavior', 'Privacy and sign-ins'])
        self.assertFalse(dialog.section_widgets['Behavior'][1].isChecked())
        dialog.show_section('Behavior')
        self.assertTrue(dialog.section_widgets['Behavior'][1].isChecked())
        dialog.address_bar.setChecked(True)
        dialog.always_tabs.setChecked(True)
        self.assertTrue(dialog.value()['WebShowAddressBar'])
        self.assertTrue(dialog.value()['WebAlwaysShowTabs'])
        self.assertEqual(dialog.value()['Opaque'], 8)

    def test_old_item_menu_cannot_target_a_rebuilt_source_path(self):
        from little_launcher.item_editing import ItemEditingSession
        from little_launcher.items_view import LauncherItemsView
        from PySide6.QtWidgets import QWidget
        parent = QWidget()
        session = ItemEditingSession(self.controller, self.launcher, parent)
        view = LauncherItemsView(self.launcher, editing=True, show_other=True)
        session.wire(view)
        candidate = deepcopy(self.launcher)
        candidate['Items'].reverse()
        session.commit(candidate)
        before = deepcopy(self.controller.document)
        view.append_requested.emit([0], {'group': [0]})
        self.assertEqual(self.controller.document, before)

    def test_item_source_tabs_share_the_same_pending_target_and_icon(self):
        dialog = ShortcutDialog(self.launcher['Items'][1])
        self.addCleanup(dialog.close)
        self.assertEqual([dialog.source_tabs.tabText(index) for index in range(2)], ['Apps && web apps', 'File or link'])
        self.assertEqual(dialog.source_tabs.currentIndex(), 1)
        dialog.icon_path.setText('/tmp/custom-icon.png')
        dialog.source_tabs.setCurrentIndex(0)
        dialog.source_tabs.setCurrentIndex(1)
        self.assertEqual(dialog.value()['Path'], '/not/installed')
        self.assertEqual(dialog.value()['IconPath'], '/tmp/custom-icon.png')

    def test_plasma_tray_coordinate_space_follows_xwayland_compatibility_choice(self):
        import os
        from unittest.mock import patch
        from little_launcher.desktop import native_tray_coordinates
        directory = Path(self.temporary.name)
        with patch.dict(os.environ, {'XDG_CONFIG_HOME': str(directory), 'XDG_CURRENT_DESKTOP': 'KDE', 'XDG_SESSION_TYPE': 'wayland'}):
            self.assertTrue(native_tray_coordinates())
            (directory / 'kdeglobals').write_text('[KScreen]\nXwaylandClientsScale=false\n')
            self.assertFalse(native_tray_coordinates())
            (directory / 'kdeglobals').write_text('[KScreen]\nXwaylandClientsScale=true\n')
            self.assertTrue(native_tray_coordinates())

    def test_card_bulk_export_retains_hidden_rows_for_read_only_launchers(self):
        from unittest.mock import patch
        from little_launcher.settings import SettingsWindow
        class Integration:
            enabled = False
            def refresh(self): pass
        self.controller.edit(lambda doc: doc['Launchers'][0].update(IsShared=True, IsSharedOwner=False))
        window = SettingsWindow(self.controller, Integration())
        self.addCleanup(window.close)
        target = Path(self.temporary.name) / 'items.json'
        with patch('little_launcher.settings.QFileDialog.getSaveFileName', return_value=(str(target), '')):
            window.export_items(self.launcher['Id'])
        self.assertEqual(json.loads(target.read_text()), self.launcher['Items'])
        from PySide6.QtWidgets import QToolButton
        bulk = window.findChild(QToolButton, 'launcher-bulk-' + self.launcher['Id'])
        self.assertTrue(bulk.isEnabled())

    def test_card_enabled_switch_confirms_disable_and_restores_on_cancel(self):
        from unittest.mock import patch
        from PySide6.QtWidgets import QCheckBox, QMessageBox, QWidget
        from little_launcher.settings import SettingsWindow
        class Integration:
            enabled = False
            def refresh(self): pass
        identifier = self.launcher['Id']
        window = SettingsWindow(self.controller, Integration())
        self.addCleanup(window.close)
        box = window.findChild(QCheckBox, 'launcher-enabled-' + identifier)
        self.assertTrue(box.isChecked())
        with patch('little_launcher.settings.QMessageBox.question', return_value=QMessageBox.No) as ask:
            box.click()
        ask.assert_called_once()
        self.assertTrue(self.controller.enabled(identifier))
        self.assertTrue(window.findChild(QCheckBox, 'launcher-enabled-' + identifier).isChecked())
        with patch('little_launcher.settings.QMessageBox.question', return_value=QMessageBox.Yes):
            window.findChild(QCheckBox, 'launcher-enabled-' + identifier).click()
        self.assertFalse(self.controller.enabled(identifier))
        # The list rebuilds its cards with deleteLater; flush so lookups see only the new card.
        from PySide6.QtCore import QCoreApplication, QEvent
        QCoreApplication.sendPostedEvents(None, QEvent.DeferredDelete)
        box = window.findChild(QCheckBox, 'launcher-enabled-' + identifier)
        self.assertFalse(box.isChecked())
        for name in ('launcher-settings-', 'launcher-items-', 'launcher-bulk-'):
            row = window.findChild(QWidget, name + identifier)
            self.assertTrue(row.isEnabled())
            self.assertAlmostEqual(row.graphicsEffect().opacity(), 0.5)
        self.assertIsNone(box.graphicsEffect())
        with patch('little_launcher.settings.QMessageBox.question') as ask:
            box.click()
        ask.assert_not_called()
        self.assertTrue(self.controller.enabled(identifier))

    def test_card_enabled_switch_is_off_and_disabled_for_unsupported_launchers(self):
        from PySide6.QtWidgets import QCheckBox
        from little_launcher.settings import SettingsWindow
        class Integration:
            enabled = False
            def refresh(self): pass
        self.controller.edit(lambda doc: doc['Launchers'].append(
            {'Id': 'foreign', 'Platform': 'windows', 'Name': 'Foreign', 'Items': [{'Name': 'App', 'Path': 'C:\\app.exe'}]}))
        window = SettingsWindow(self.controller, Integration())
        self.addCleanup(window.close)
        box = window.findChild(QCheckBox, 'launcher-enabled-foreign')
        self.assertFalse(box.isChecked())
        self.assertFalse(box.isEnabled())

    def test_card_bulk_append_keeps_source_metadata_and_rejects_stale_replace(self):
        from unittest.mock import patch
        from little_launcher.settings import SettingsWindow
        class Integration:
            enabled = False
            def refresh(self): pass
        window = SettingsWindow(self.controller, Integration())
        self.addCleanup(window.close)
        source = Path(self.temporary.name) / 'import.json'
        imported = [{'Name': 'Imported', 'Path': 'C:\\app.exe', 'Future': {'keep': True}}]
        source.write_text(json.dumps(imported))
        replace, append, cancel = object(), object(), object()
        with patch('little_launcher.settings.QFileDialog.getOpenFileName', return_value=(str(source), '')), \
                patch('little_launcher.settings.QMessageBox') as box:
            box.return_value.addButton.side_effect = [replace, append, cancel]
            box.return_value.clickedButton.return_value = append
            window.import_items(self.launcher['Id'])
        self.assertEqual(self.controller.launcher(self.launcher['Id'])['Items'], self.launcher['Items'] + imported)
        def concurrent_edit():
            self.controller.edit(lambda doc: doc['Launchers'][0].update(Name='Changed elsewhere'))
        with patch('little_launcher.settings.QFileDialog.getOpenFileName', return_value=(str(source), '')), \
                patch('little_launcher.settings.QMessageBox') as box:
            box.return_value.addButton.side_effect = [replace, append, cancel]
            box.return_value.clickedButton.return_value = replace
            box.return_value.exec.side_effect = concurrent_edit
            window.import_items(self.launcher['Id'])
        saved = self.controller.launcher(self.launcher['Id'])
        self.assertEqual(saved['Name'], 'Changed elsewhere')
        self.assertEqual(saved['Items'], self.launcher['Items'] + imported)

    def test_launcher_settings_type_changes_retain_hidden_content_and_separate_view_values(self):
        launcher = deepcopy(self.launcher)
        launcher.update(WebHomeUrl='https://home.example', WebBookmarks=[{'Name': 'Saved', 'Url': 'https://saved.example', 'Opaque': 3}])
        dialog = LauncherDialog(launcher)
        self.addCleanup(dialog.close)
        dialog.kind.setCurrentIndex(dialog.kind.findData(1))
        dialog.url.setText('https://changed.example')
        web = dialog.value()
        self.assertEqual(web['Kind'], 1)
        self.assertEqual(web['Items'], launcher['Items'])
        self.assertEqual(web['WebBookmarks'], launcher['WebBookmarks'])
        dialog.kind.setCurrentIndex(dialog.kind.findData(0))
        dialog.view.setCurrentIndex(dialog.view.findData(2))
        self.assertEqual(dialog.value()['ViewMode'], 2)
        dialog.view.setCurrentIndex(dialog.view.findData(1))
        self.assertEqual(dialog.value()['ViewMode'], 1)
        self.assertTrue(dialog.columns_row.isHidden())
        self.assertTrue(dialog.section_widgets['Size and position'][0].isHidden())

    def test_visible_launcher_settings_done_and_field_return_have_distinct_behavior(self):
        from PySide6.QtCore import Qt
        from PySide6.QtTest import QTest
        dialog = LauncherDialog(self.launcher)
        self.addCleanup(dialog.close)
        dialog.show()
        self.app.processEvents()
        dialog.name.setFocus()
        QTest.keyClick(dialog.name, Qt.Key_Return)
        self.assertTrue(dialog.isVisible(), 'Finishing the name field must not close settings')
        QTest.mouseClick(dialog.done_button, Qt.LeftButton)
        self.app.processEvents()
        self.assertFalse(dialog.isVisible())
        self.assertEqual(dialog.result(), 1)

    def test_shortcut_settings_escape_applies_pending_tray_visibility(self):
        from PySide6.QtCore import Qt
        from PySide6.QtTest import QTest
        dialog = LauncherDialog(self.launcher)
        self.addCleanup(dialog.close)
        dialog.show()
        dialog.show_tray.setChecked(False)
        QTest.keyClick(dialog, Qt.Key_Escape)
        self.assertEqual(dialog.result(), 1)
        self.assertTrue(dialog.value()['NIconHide'])
        self.assertEqual(dialog.value()['Items'], self.launcher['Items'])

    def _group_view(self, mode):
        from PySide6.QtWidgets import QWidget
        from little_launcher.items_view import LauncherItemsView
        from little_launcher.item_editing import ItemEditingSession
        launcher = {'Id': 'groups', 'ViewMode': mode, 'Items': [
            {'Name': 'A', 'IsGroup': True, 'FutureGroup': 9, 'Children': [
                {'Name': 'First', 'Path': '/first'}, {'Name': 'Hidden', 'Path': 'C:\\hidden.exe', 'Future': 3},
                {'Name': 'Last', 'Path': '/last'}]},
            {'Name': 'B', 'IsGroup': True, 'Children': [{'Name': 'Other', 'Path': '/other'}]},
            {'Name': 'Loose', 'Path': '/loose'}]}
        self.controller.edit(lambda doc: doc['Launchers'].append(launcher))
        parent = QWidget()
        self.addCleanup(parent.close)
        session = ItemEditingSession(self.controller, launcher, parent)
        view = LauncherItemsView(launcher, editing=True, parent=parent)
        session.wire(view)
        return view, session

    @staticmethod
    def _group_drop(group, source, x, y):
        from PySide6.QtCore import QMimeData, QPointF, Qt
        from PySide6.QtGui import QDropEvent
        from little_launcher.item_drag import MIME_TYPE
        mime = QMimeData()
        mime.setData(MIME_TYPE, json.dumps({'session': group.session, 'path': source}).encode())
        event = QDropEvent(QPointF(x, y), Qt.MoveAction, mime, Qt.LeftButton, Qt.NoModifier)
        group.drop(group, event)
        return event.isAccepted()

    def test_group_owns_header_and_child_widgets_without_renumbering_hidden_source(self):
        for mode in (0, 1, 2):
            view, session = self._group_view(mode)
            group = view.group_containers[(0,)]
            self.assertIs(group.header.parentWidget(), group)
            self.assertEqual([button.path for button in group.child_buttons], [[0, 0], [0, 1], [0, 2]])
            self.assertTrue(all(button.parentWidget() is group for button in group.child_buttons))
            self.assertGreater(group.height(), group.header.height())
            self.assertEqual(group.item['FutureGroup'], 9)
            self.controller.edit(lambda doc: doc['Launchers'].pop())

    def test_group_interior_header_drop_appends_to_full_children_not_the_root(self):
        view, session = self._group_view(1)
        group = view.group_containers[(0,)]
        self.assertTrue(self._group_drop(group, [2], group.width()//2, 12))
        saved = self.controller.launcher('groups')['Items']
        self.assertEqual([row['Name'] for row in saved], ['A', 'B'])
        self.assertEqual([row['Name'] for row in saved[0]['Children']], ['First', 'Hidden', 'Last', 'Loose'])
        self.assertEqual(saved[0]['Children'][1]['Future'], 3)

    def test_group_outer_bottom_drop_extracts_child_as_a_sibling(self):
        view, session = self._group_view(0)
        group = view.group_containers[(0,)]
        self.assertTrue(self._group_drop(group, [0, 2], group.width()//2, group.height()-2))
        saved = self.controller.launcher('groups')['Items']
        self.assertEqual([row['Name'] for row in saved], ['A', 'Last', 'B', 'Loose'])
        self.assertEqual([row['Name'] for row in saved[0]['Children']], ['First', 'Hidden'])

    def test_group_drag_over_another_groups_child_uses_whole_container_midpoint(self):
        view, session = self._group_view(0)
        target = view.group_containers[(1,)]
        self.assertTrue(self._group_drop(target, [0], target.width()//2, target.height()-16))
        saved = self.controller.launcher('groups')['Items']
        self.assertEqual([row['Name'] for row in saved], ['B', 'A', 'Loose'])
        self.assertEqual(saved[1]['FutureGroup'], 9)
        self.assertEqual([row['Name'] for row in saved[1]['Children']], ['First', 'Hidden', 'Last'])

    def test_regular_editor_keeps_a_compact_intrinsic_preview(self):
        from PySide6.QtWidgets import QGroupBox
        editor = ItemsEditor(self.controller, self.launcher)
        self.addCleanup(editor.close)
        editor.show()
        self.app.processEvents()
        preview = editor.scroll.widget()
        self.assertEqual(preview.width(), editor.view.width() + 2)
        self.assertLess(preview.width(), editor.scroll.viewport().width())
        self.assertLess(preview.height(), editor.scroll.viewport().height())
        self.assertEqual(editor.layout().contentsMargins().left(), 24)
        self.assertEqual(editor.layout().contentsMargins().bottom(), 24)
        self.assertEqual(len(editor.view.findChildren(QGroupBox)), 0)
        self.assertFalse(hasattr(editor,'foreign'))
        editor.resize(300, 620)
        self.app.processEvents()
        self.assertGreater(editor.toolbar.buttons[2].y(), editor.toolbar.buttons[0].y())
        self.assertLessEqual(editor.toolbar.buttons[-1].geometry().right(), editor.toolbar.width())

    def test_flyout_editing_stays_in_place_and_keeps_hidden_rows_during_live_save(self):
        from little_launcher.flyout import ShortcutFlyout
        from little_launcher.item_button import ItemButton
        flyout = ShortcutFlyout(self.launcher, controller=self.controller)
        self.addCleanup(flyout.close)
        self.controller.changed.connect(lambda: flyout.sync_launcher(self.controller.launcher(self.launcher['Id'])))
        separate_editors = []
        flyout.edit_requested.connect(separate_editors.append)
        flyout.show()
        self.app.processEvents()
        width = flyout.width()
        normal_cells = {tuple(button.path): button.size() for button in flyout.view.findChildren(ItemButton)}
        flyout.enter_edit()
        self.app.processEvents()
        self.assertTrue(flyout.editing)
        self.assertIsNotNone(flyout.toolbar)
        self.assertEqual(flyout.width(), width)
        self.assertEqual(separate_editors, [])
        for button in flyout.view.findChildren(ItemButton):
            if tuple(button.path) in normal_cells:
                self.assertEqual(button.size(), normal_cells[tuple(button.path)])
        flyout.session.move_item([1], [0])
        self.assertTrue(flyout.isVisible())
        self.assertTrue(flyout.editing)
        self.assertEqual(self.controller.launcher(self.launcher['Id'])['Items'][1]['Future'], 9)
        self.assertIn((1,), flyout.view.rendered_paths)
        from PySide6.QtCore import QEvent, Qt
        from PySide6.QtGui import QKeyEvent
        self.app.sendEvent(flyout, QKeyEvent(QEvent.KeyPress, Qt.Key_Escape, Qt.NoModifier))
        self.assertFalse(flyout.editing)
        self.assertIn((1,), flyout.view.rendered_paths)
        self.assertIsNone(flyout.toolbar)
        self.assertTrue(flyout.isVisible())
        self.app.sendEvent(flyout, QKeyEvent(QEvent.KeyPress, Qt.Key_Escape, Qt.NoModifier))
        self.assertFalse(flyout.isVisible())

    def test_empty_collection_drops_preserve_full_source_and_reject_stale_drag_tokens(self):
        from little_launcher.item_drag import ItemDrag, MIME_TYPE
        from PySide6.QtCore import QMimeData
        editor = ItemsEditor(self.controller, self.launcher)
        self.addCleanup(editor.close)
        editor.add_item(4)
        editor.session.move_to([1], {'column': 1})
        rows = self.controller.launcher(self.launcher['Id'])['Items']
        self.assertEqual(rows[-1]['Name'], 'Linux')
        self.assertEqual(rows[0]['Future'], 9)
        self.assertEqual(rows[1]['Children'][0]['FutureChild'], True)
        editor.session.move_to([3], {'group': [1]})
        rows = self.controller.launcher(self.launcher['Id'])['Items']
        self.assertEqual(rows[1]['Children'][-1]['Name'], 'Linux')
        mime = QMimeData()
        mime.setData(MIME_TYPE, b'[]')
        self.assertIsNone(ItemDrag.source(mime, editor.view.session))
        mime.setData(MIME_TYPE, json.dumps({'session': 'old', 'path': [0]}).encode())
        self.assertIsNone(ItemDrag.source(mime, editor.view.session))
        mime.setData(MIME_TYPE, json.dumps({'session': editor.view.session, 'path': [0]}).encode())
        self.assertEqual(ItemDrag.source(mime, editor.view.session), [0])

    def test_late_dialog_reply_does_not_restart_a_quitting_browser(self):
        host = BrowserHost(Path(self.temporary.name))
        host.quitting = True
        host._start = lambda: self.fail('A late reply restarted the browser during quit')
        host.send({'type': 'reply', 'token': 'late', 'allowed': False})
        self.assertEqual(host.pending, [])

    def test_bookmark_menu_snapshot_and_native_dialog_reject_stale_saves(self):
        from unittest.mock import patch
        from little_launcher.bookmarks import BookmarkActions
        from PySide6.QtWidgets import QDialog
        web = {'Id': 'web-editor', 'Kind': 1, 'WebHomeUrl': 'https://home.example',
               'Items': [], 'WebBookmarks': [{'Name': 'Saved', 'Url': 'https://saved.example', 'Future': 7}]}
        self.assertTrue(self.controller.edit(lambda document: document['Launchers'].append(deepcopy(web))))
        actions = BookmarkActions(self.controller, None)
        errors = []
        self.controller.failed.connect(errors.append)
        with patch('little_launcher.bookmarks.BookmarkDialog') as dialog:
            actions.handle({'id': web['Id'], 'action': 'edit', 'path': [0], 'expected': []})
            dialog.assert_not_called()
        self.assertTrue(errors)

        def concurrent_settings_edit():
            changed = deepcopy(self.controller.launcher(web['Id']))
            changed['WebHomeUrl'] = 'https://later.example'
            self.assertTrue(self.controller.replace_launcher(changed))
            return QDialog.Accepted

        with patch('little_launcher.bookmarks.BookmarkDialog') as dialog:
            dialog.return_value.exec.side_effect = concurrent_settings_edit
            dialog.return_value.value.return_value = {'Name': 'Changed', 'Url': 'https://new.example'}
            actions.handle({'id': web['Id'], 'action': 'edit', 'path': [0], 'expected': web['WebBookmarks']})
        saved = self.controller.launcher(web['Id'])
        self.assertEqual(saved['WebBookmarks'], web['WebBookmarks'])
        self.assertEqual(saved['WebHomeUrl'], 'https://later.example')
        self.assertIn('changed while the editor was open', errors[-1])

    def test_bookmark_icons_only_preserves_metadata_and_rejects_stale_or_read_only_edits(self):
        from little_launcher.bookmarks import BookmarkActions
        web = {'Id':'bookmark-icons', 'Kind':1, 'WebHomeUrl':'https://home.example',
               'WebBookmarks':[{'Name':'Hidden','Url':'legacy:skip'}, {'Name':'Saved','Url':'https://saved.example','Future':9}]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(web)))
        actions=BookmarkActions(self.controller,None)
        message={'id':web['Id'],'action':'icons-only','path':[1],'expected':deepcopy(web['WebBookmarks']),'value':True}
        actions.handle(message)
        saved=deepcopy(self.controller.launcher(web['Id']))
        self.assertTrue(saved['WebBookmarks'][1]['IconsOnly'])
        self.assertEqual(saved['WebBookmarks'][1]['Future'],9)
        self.assertEqual(saved['WebBookmarks'][0],web['WebBookmarks'][0])
        self.assertEqual(saved['WebHomeUrl'],web['WebHomeUrl'])
        actions.handle({**message,'value':False})
        self.assertEqual(self.controller.launcher(web['Id']),saved)
        message['expected']=deepcopy(saved['WebBookmarks'])
        actions.handle({**message,'value':1})
        self.assertEqual(self.controller.launcher(web['Id']),saved)
        self.controller.edit(lambda doc:next(x for x in doc['Launchers'] if x['Id']==web['Id']).update(IsShared=True))
        actions.handle({**message,'value':False})
        self.assertTrue(self.controller.launcher(web['Id'])['WebBookmarks'][1]['IconsOnly'])

    def test_bookmark_caption_address_and_home_commands_own_only_their_fields(self):
        from little_launcher.bookmarks import BookmarkActions
        from unittest.mock import patch
        from PySide6.QtWidgets import QDialog
        web={'Id':'bookmark-fields','Kind':1,'WebHomeUrl':'https://home.example','FutureLauncher':7,
             'WebBookmarks':[{'Url':'legacy:skip','FutureHidden':3},
                             {'Name':'Saved','Url':'https://saved.example','IconsOnly':True,'Future':9}]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(web)))
        actions=BookmarkActions(self.controller,None)
        def invoke(action,**extra):
            current=self.controller.launcher(web['Id'])
            actions.handle({'id':web['Id'],'action':action,'path':[1],
                            'expected':deepcopy(current['WebBookmarks']),**extra})
        for action,dialog_name,value in [('rename','NameDialog','Renamed'),('address','BookmarkAddressDialog','https://new.example')]:
            with patch('little_launcher.bookmarks.'+dialog_name) as dialog:
                dialog.return_value.exec.return_value=QDialog.Accepted
                dialog.return_value.value.return_value=value
                invoke(action)
        current=deepcopy(self.controller.launcher(web['Id']))
        self.assertEqual(current['WebBookmarks'][1],{'Name':'Renamed','Url':'https://new.example','IconsOnly':True,'Future':9})
        self.assertEqual(current['WebHomeUrl'],web['WebHomeUrl'])
        invoke('set-home',expectedHome=web['WebHomeUrl'])
        saved=self.controller.launcher(web['Id'])
        self.assertEqual(saved['WebHomeUrl'],'https://new.example')
        self.assertEqual(saved['WebBookmarks'],current['WebBookmarks'])
        self.assertEqual(saved['FutureLauncher'],7)
        # An unchanged bookmark tree must not allow a stale menu to overwrite a newer home choice.
        self.controller.edit(lambda doc:next(x for x in doc['Launchers'] if x['Id']==web['Id']).update(WebHomeUrl='https://later.example'))
        invoke('set-home',expectedHome=web['WebHomeUrl'])
        self.assertEqual(self.controller.launcher(web['Id'])['WebHomeUrl'],'https://later.example')
        self.controller.edit(lambda doc:next(x for x in doc['Launchers'] if x['Id']==web['Id']).update(IsShared=True))
        invoke('set-home',expectedHome='https://later.example')
        self.assertEqual(self.controller.launcher(web['Id'])['WebHomeUrl'],'https://later.example')

    def test_theme_refresh_ignores_widget_style_events_when_desktop_colors_are_unchanged(self):
        from little_launcher.desktop import NativeThemeMonitor
        from PySide6.QtGui import QPalette
        original=QPalette(self.app.palette());monitor=NativeThemeMonitor();changed=[]
        monitor.changed.connect(lambda:changed.append(True))
        try:
            palette=QPalette(original);palette.setColor(QPalette.WindowText,QColor('#352167'))
            self.app.setPalette(palette);monitor._publish()
            self.assertEqual(len(changed),1)
            monitor._publish();monitor._publish()
            self.assertEqual(len(changed),1,'A settings rebuild must not start a repeating global refresh loop')
        finally:self.app.setPalette(original)

    def test_portable_preset_legacy_and_colored_glyph_icons_render_without_blue_reset(self):
        import base64
        from PySide6.QtGui import QImage
        from little_launcher.icon_glyphs import PRESETS, canonical_mode, glyph_icon
        from little_launcher.icons import LauncherIcons
        from unittest.mock import patch
        blue=LauncherIcons.window_png({'TrayIconMode':'Blue'})
        for index,name in enumerate(PRESETS,6):
            self.assertEqual(canonical_mode(index),name)
            self.assertNotEqual(LauncherIcons.window_png({'TrayIconMode':name}),blue)
        self.assertEqual(canonical_mode(12),'Custom');self.assertEqual(canonical_mode(13),'Composite')
        launcher={'TrayIconMode':'Glyph:#ff0000:A','Future':7}
        before=deepcopy(launcher);rendered=QImage.fromData(base64.b64decode(LauncherIcons.window_png(launcher)))
        pixels=[rendered.pixelColor(x,y) for x in range(rendered.width()) for y in range(rendered.height())]
        self.assertTrue(any(pixel.alpha()>100 and pixel.red()>pixel.green()+50 for pixel in pixels))
        self.assertEqual(launcher,before)
        with patch('little_launcher.icon_glyphs.QFontDatabase.families',return_value=[]):
            self.assertIsNone(glyph_icon('Glyph:\ue000'))
            self.assertIsNotNone(glyph_icon('Glyph:#ff0000:\ue734'))

    def test_icon_picker_preselects_and_cancel_keeps_the_original_choice(self):
        from little_launcher.icon_picker import IconPicker
        original={'Id':'pending-icon','TrayIconMode':'Glyph:#ff0000:A','CustomTrayIconPath':'missing.png','Future':7}
        before=deepcopy(original);picker=IconPicker(original)
        self.assertEqual(picker.tabs.currentIndex(),1);self.assertEqual(picker.text.text(),'A')
        self.assertEqual(picker.value()['TrayIconMode'],original['TrayIconMode'])
        picker.choose('Star');picker.reject();self.assertEqual(original,before)

    def test_confirmed_icon_choice_is_one_live_transaction_and_cancel_does_not_save(self):
        from unittest.mock import patch
        from PySide6.QtWidgets import QDialog
        launcher={'Id':'icon-confirm','Items':[],'TrayIconMode':'Blue','Future':7}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(launcher)))
        dialog=LauncherDialog(launcher,controller=self.controller)
        changed=[];self.controller.changed.connect(lambda:changed.append(True))
        with patch('little_launcher.launcher_settings.IconPicker') as picker:
            picker.return_value.exec.return_value=QDialog.Rejected;dialog._pick_icon()
            self.assertEqual(changed,[])
            picker.return_value.exec.return_value=QDialog.Accepted
            picker.return_value.value.return_value={'TrayIconMode':'Glyph:#ff0000:A','CustomTrayIconPath':''}
            dialog._pick_icon()
        self.assertEqual(len(changed),1)
        self.assertEqual(self.controller.launcher(launcher['Id'])['TrayIconMode'],'Glyph:#ff0000:A')
        self.assertEqual(self.controller.launcher(launcher['Id'])['Future'],7)
        dialog.accept()

    def test_external_file_and_url_drops_add_references_with_copy_semantics(self):
        from PySide6.QtCore import QMimeData, QPoint, QPointF, QUrl, Qt
        from PySide6.QtGui import QDragEnterEvent, QDropEvent
        from little_launcher.external_drop import dropped_items
        source=Path(self.temporary.name)/'document.txt';source.write_text('untouched source')
        launcher={'Id':'external-drop','ViewMode':1,'Items':[
            {'Name':'Hidden','Path':'C:\\foreign.exe','SupportedOperatingSystems':['windows'],'Future':7},
            {'Name':'Group','IsGroup':True,'Children':[{'Name':'Hidden child','Path':'C:\\foreign.exe','SupportedOperatingSystems':['windows'],'Future':9},
                {'Name':'Linux child','Path':'/bin/echo','SupportedOperatingSystems':['linux']}]},
        ]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(launcher)))
        editor=ItemsEditor(self.controller,launcher);view=editor.view
        group=view.metrics.groups[0];point=QPoint(group.x+group.width//2,group.y+group.header_height//2)
        mime=QMimeData();mime.setUrls([QUrl.fromLocalFile(str(source)),QUrl('https://example.com/new')])
        items=dropped_items(mime);self.assertEqual(len(items),2)
        enter=QDragEnterEvent(point,Qt.CopyAction|Qt.MoveAction,mime,Qt.LeftButton,Qt.NoModifier)
        self.app.sendEvent(view,enter);self.assertTrue(enter.isAccepted());self.assertEqual(enter.dropAction(),Qt.CopyAction)
        drop=QDropEvent(QPointF(point),Qt.CopyAction|Qt.MoveAction,mime,Qt.LeftButton,Qt.NoModifier)
        self.app.sendEvent(view,drop);self.assertTrue(drop.isAccepted());self.assertEqual(drop.dropAction(),Qt.CopyAction)
        saved=self.controller.launcher(launcher['Id'])
        self.assertEqual(saved['Items'][0],launcher['Items'][0])
        self.assertEqual(saved['Items'][1]['Children'][:2],launcher['Items'][1]['Children'])
        self.assertEqual(saved['Items'][1]['Children'][2:],items)
        self.assertEqual(source.read_text(),'untouched source')
        self.assertNotIn('SupportedOperatingSystems',items[0])
        self.assertNotIn('SupportedOperatingSystems',items[1])
        before=deepcopy(self.controller.document)
        view.external_drop_requested.emit(items,{'column':0})
        self.assertEqual(self.controller.document,before,'A pre-rebuild target cannot insert twice')
        editor.close()

    def test_external_drops_reject_foreign_internal_tokens_and_read_only_targets(self):
        from PySide6.QtCore import QMimeData, QPoint, QUrl, Qt
        from PySide6.QtGui import QDragEnterEvent
        from little_launcher.external_drop import dropped_items
        from little_launcher.item_drag import MIME_TYPE
        mime=QMimeData();mime.setUrls([QUrl('https://example.com')]);mime.setData(MIME_TYPE,b'foreign internal drag')
        self.assertEqual(dropped_items(mime),[])
        mime=QMimeData();mime.setUrls([QUrl('javascript:alert(1)')]);self.assertEqual(dropped_items(mime),[])
        launcher={'Id':'readonly-external','IsShared':True,'Items':[]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(launcher)))
        editor=ItemsEditor(self.controller,launcher);mime.setUrls([QUrl('https://example.com')])
        event=QDragEnterEvent(QPoint(12,12),Qt.CopyAction,mime,Qt.LeftButton,Qt.NoModifier)
        self.app.sendEvent(editor.view,event);self.assertFalse(event.isAccepted())
        before=deepcopy(self.controller.document)
        editor.view.external_drop_requested.emit(dropped_items(mime),{'column':0})
        self.assertEqual(self.controller.document,before)
        editor.close()

    def test_launcher_settings_controls_apply_immediately_and_reject_external_changes(self):
        web={'Id':'live-settings','Kind':1,'WebHomeUrl':'https://home.example','Future':7,
             'WebBookmarks':[{'Name':'Saved','Url':'https://saved.example','Future':9}]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(web)))
        dialog=LauncherDialog(web,controller=self.controller)
        dialog.bookmark_icons.setChecked(True)
        saved=deepcopy(self.controller.launcher(web['Id']))
        self.assertTrue(saved['WebBookmarkIconsOnly'])
        self.assertEqual(saved['WebBookmarks'],web['WebBookmarks'])
        self.assertEqual(saved['Future'],7)
        dialog.name.setText('New name');dialog.name.editingFinished.emit()
        self.assertEqual(self.controller.launcher(web['Id'])['Name'],'New name')
        self.controller.edit(lambda doc:next(x for x in doc['Launchers'] if x['Id']==web['Id']).update(WebHomeUrl='https://external.example'))
        dialog.reload_open.setChecked(True)
        self.assertTrue(dialog.live_conflict)
        self.assertEqual(self.controller.launcher(web['Id'])['WebHomeUrl'],'https://external.example')
        self.assertFalse(self.controller.launcher(web['Id']).get('WebReloadOnShow',False))
        dialog.accept()

    def test_native_browser_bookmark_picker_search_and_guarded_import_preserve_home(self):
        from little_launcher.bookmark_picker import BookmarkPicker
        from little_launcher.browser_bookmarks import BookmarkSource
        from little_launcher.bookmarks import BookmarkActions
        from PySide6.QtWidgets import QDialog
        from unittest.mock import patch
        path=Path(self.temporary.name)/'Bookmarks'
        path.write_text(json.dumps({'roots':{'bar':{'type':'folder','name':'Work','children':[
            {'type':'url','name':'Wiki','url':'https://wiki.example'},
            {'type':'url','name':'Mail','url':'https://mail.example'}]}}}))
        picker=BookmarkPicker(catalog=[BookmarkSource('Synthetic browser','Default',path)])
        picker.search.setText('wiki');self.assertEqual(picker.list.topLevelItemCount(),1)
        item=picker.list.topLevelItem(0);picker.list.setCurrentItem(item);item.setSelected(True)
        self.assertEqual(picker.value(),{'Name':'Wiki','Url':'https://wiki.example'})
        web={'Id':'import-bookmark','Kind':1,'WebHomeUrl':'https://home.example',
             'WebBookmarks':[{'Url':'legacy:skip','Future':7}]}
        self.controller.edit(lambda doc:doc['Launchers'].append(deepcopy(web)))
        with patch('little_launcher.bookmarks.BookmarkPicker') as prompt:
            prompt.return_value.exec.return_value=QDialog.Accepted
            prompt.return_value.value.return_value=picker.value()
            BookmarkActions(self.controller,None).handle({'id':web['Id'],'action':'import','path':[],'expected':web['WebBookmarks']})
        saved=self.controller.launcher(web['Id'])
        self.assertEqual(saved['WebHomeUrl'],web['WebHomeUrl'])
        self.assertEqual(saved['WebBookmarks'],web['WebBookmarks']+[picker.value()])
        picker.close()

    def test_bookmark_address_prompt_rejects_non_web_schemes_before_closing(self):
        from little_launcher.forms import BookmarkAddressDialog
        from PySide6.QtWidgets import QDialog
        from unittest.mock import patch
        dialog=BookmarkAddressDialog('https://example.com')
        dialog.name.setText('file:///private')
        with patch('little_launcher.forms.QMessageBox.warning') as warning:
            dialog.accept()
            warning.assert_called_once()
        self.assertNotEqual(dialog.result(),QDialog.Accepted)
        dialog.name.setText('example.org/path')
        dialog.accept()
        self.assertEqual(dialog.value(),'https://example.org/path')

    def test_read_only_subscription_cannot_replace_bookmarks(self):
        shared = {'Id': 'shared-web', 'Kind': 1, 'IsShared': True,
                  'Items': [], 'WebBookmarks': [{'Name': 'Saved', 'Url': 'https://saved.example'}]}
        self.assertTrue(self.controller.edit(lambda document: document['Launchers'].append(deepcopy(shared))))
        changed = deepcopy(shared)
        changed['WebBookmarks'].clear()
        self.assertFalse(self.controller.replace_launcher(changed))
        self.assertEqual(self.controller.launcher(shared['Id'])['WebBookmarks'], shared['WebBookmarks'])

    def test_login_startup_preserves_off_and_unrelated_entries(self):
        import os
        from unittest.mock import patch
        directory = Path(self.temporary.name)
        preferences = LocalPreferences(directory / 'linux.json')
        self.assertTrue(preferences.data['StartAtLogin'])
        with patch.dict(os.environ, {'XDG_CONFIG_HOME': str(directory)}):
            set_autostart(True)
            own_entry = directory / 'autostart/little-launcher.desktop'
            self.assertTrue(own_entry.exists())
            unrelated = own_entry.parent / 'unrelated.desktop'
            unrelated.write_text('preserve')
            preferences.set('StartAtLogin', False)
            reopened = LocalPreferences(directory / 'linux.json')
            set_autostart(reopened.data['StartAtLogin'])
            self.assertFalse(own_entry.exists())
            self.assertEqual(unrelated.read_text(), 'preserve')

    def test_tray_pixels_use_sni_network_byte_order(self):
        pixmap = QPixmap(1, 1)
        pixmap.fill(QColor(17, 34, 51, 255))
        for width, height, data in LauncherIcons.pixmaps(QIcon(pixmap)):
            self.assertEqual(len(data), width * height * 4)
            self.assertEqual(data[:4], bytes([255, 17, 34, 51]))

    def test_hidden_rows_survive_real_editor_save(self):
        editor = ItemsEditor(self.controller, self.launcher)
        self.addCleanup(editor.close)
        self.assertIn((0,), editor.view.rendered_paths)
        editor.move_item([1], [0])
        saved = self.controller.launcher('editor-test')['Items']
        self.assertEqual([item['Name'] for item in saved], ['Linux', 'Windows', 'Group'])
        self.assertEqual(saved[1]['Future'], 9)
        self.assertTrue(saved[2]['Children'][0]['FutureChild'])
        self.assertIn((1,), editor.view.rendered_paths)

    def test_unknown_os_assignment_survives_unrelated_form_edit(self):
        item = {'Name': 'Future', 'Path': 'app', 'SupportedOperatingSystems': ['other-os', 'linux'], 'Future': 4}
        dialog = ShortcutDialog(item)
        self.addCleanup(dialog.close)
        dialog.name.setText('Renamed')
        self.assertEqual(dialog.value()['SupportedOperatingSystems'], ['other-os', 'linux'])
        self.assertEqual(dialog.value()['Future'], 4)

    def test_stale_launcher_editor_rejects_overwriting_later_settings(self):
        earlier = deepcopy(self.launcher)
        newer = deepcopy(self.launcher)
        newer['Name'] = 'Changed elsewhere'
        self.assertTrue(self.controller.replace_launcher(newer, earlier))
        earlier['Items'].clear()
        errors = []
        self.controller.failed.connect(errors.append)
        self.assertFalse(self.controller.replace_launcher(earlier, self.launcher))
        self.assertEqual(self.controller.launcher('editor-test')['Name'], 'Changed elsewhere')
        self.assertEqual(len(self.controller.launcher('editor-test')['Items']), 3)
        self.assertIn('changed while the editor', errors[0])

    def test_whole_group_cannot_be_dropped_into_another_groups_children(self):
        launcher = deepcopy(self.launcher)
        launcher['Items'].append({'Name': 'Other group', 'IsGroup': True,
                                  'Children': [{'Name': 'Linux child', 'Path': '/missing'}]})
        self.assertTrue(self.controller.replace_launcher(launcher))
        editor = ItemsEditor(self.controller, launcher)
        self.addCleanup(editor.close)
        before = deepcopy(self.controller.document)
        editor.move_item([2], [3, 0])
        self.assertEqual(self.controller.document, before)

    def test_home_address_form_does_not_overwrite_the_first_bookmark(self):
        launcher = {'Id': 'web-test', 'Kind': 1, 'Name': 'Site', 'WebHomeUrl': 'https://home.example',
                    'WebBookmarks': [{'Name': 'Saved', 'Url': 'https://saved.example', 'Future': 7}]}
        dialog = LauncherDialog(launcher)
        self.addCleanup(dialog.close)
        dialog.url.setText('https://changed.example')
        value = dialog.value()
        self.assertEqual(value['WebHomeUrl'], 'https://changed.example')
        self.assertEqual(value['WebBookmarks'], launcher['WebBookmarks'])

    def test_read_only_subscription_rejects_domain_item_mutation(self):
        subscribed = deepcopy(self.launcher)
        subscribed.update(IsShared=True, IsSharedOwner=False, SharedTwoWay=False)
        self.assertTrue(self.controller.replace_launcher(subscribed))
        edited = deepcopy(subscribed)
        edited['Items'].clear()
        self.assertFalse(self.controller.replace_launcher(edited, subscribed))
        self.assertEqual(self.controller.launcher('editor-test')['Items'], self.launcher['Items'])
