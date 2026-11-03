"""Shortcut picker state and selected-bookmark imports use synthetic local data only."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
try:
    from PySide6.QtCore import Qt
    from PySide6.QtGui import QColor, QImage
    from PySide6.QtWidgets import QApplication, QDialog
    from little_launcher.app_picker import ApplicationList
    from little_launcher.bookmark_picker import BookmarkPicker
    from little_launcher.browser_bookmarks import BookmarkSource
    from little_launcher.controller import LauncherController
    from little_launcher.desktop import item_icon
    from little_launcher.document import DocumentError
    from little_launcher.forms import ShortcutDialog
    from little_launcher.icon_picker import IconPicker
    from little_launcher.shortcut_import import append_browser_bookmarks
    QT=True
except ImportError:
    QT=False

@unittest.skipUnless(QT,'Native shortcut checks require Qt')
class ShortcutParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.app=QApplication.instance() or QApplication([])

    def test_saved_item_glyph_color_and_valid_image_priority(self):
        red=item_icon({'IconGlyph':'A','IconColor':'#ff0000'}).pixmap(64,64).toImage()
        self.assertTrue(any(red.pixelColor(x,y).alpha()>0 and red.pixelColor(x,y).red()>200 and red.pixelColor(x,y).green()<50 for x in range(64) for y in range(64)))
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'image.png';image=QImage(32,32,QImage.Format_ARGB32);image.fill(QColor('#00ff00'));image.save(str(path))
            result=item_icon({'IconPath':str(path),'IconGlyph':'A','IconColor':'#ff0000'}).pixmap(32,32).toImage()
            self.assertEqual(result.pixelColor(16,16).name(),'#00ff00')
            path.write_text('broken image')
            self.assertFalse(item_icon({'IconPath':str(path),'IconGlyph':'A'}).isNull())

    def test_item_picker_restores_colored_preset_and_uses_pending_item_fields(self):
        original={'Name':'Kept','Path':'/missing','IconGlyph':'\ue734','IconColor':'#80ff0000','Future':9}
        before=deepcopy(original);picker=IconPicker({},item=original)
        self.assertEqual(picker.tabs.currentIndex(),1)
        self.assertEqual(picker.value(),{'IconPath':'','IconGlyph':'\ue734','IconColor':'#80ff0000'})
        picker.set_color('#0078d4');picker.choose('Glyph:\ue840')
        self.assertEqual(picker.value()['IconColor'],'#0078d4')
        self.assertEqual(picker.value()['IconGlyph'],'\ue840')
        picker.choose('Glyph:👨\u200d👩\u200d👧\u200d👦')
        self.assertEqual(picker.value()['IconGlyph'],'👨\u200d👩\u200d👧\u200d👦')
        picker.reject();self.assertEqual(original,before)
        automatic=IconPicker({},item=original);automatic.choose('Composite')
        self.assertEqual(automatic.value(),{'IconPath':'','IconGlyph':'','IconColor':''})

    def test_unavailable_private_glyph_and_foreign_icon_path_survive_cancel_and_edit(self):
        original={'Name':'Kept','Path':'/missing','IconPath':'C:\\icons\\saved.png','IconGlyph':'\uf123','IconColor':'#123456','Future':{'a':1}}
        with patch('little_launcher.icon_glyphs.QFontDatabase.families',return_value=[]):
            picker=IconPicker({},item=original);self.assertFalse(picker.confirm.isEnabled());picker.reject()
            dialog=ShortcutDialog(original)
            value=dialog.value()
            for key in ('IconPath','IconGlyph','IconColor','Future'):self.assertEqual(value[key],original[key])
            dialog.reject()

    def test_shortcut_icon_confirmation_changes_only_icon_fields_in_pending_form(self):
        original={'Name':'Kept','Path':'/missing','IconPath':'old','Future':{'keep':7}}
        dialog=ShortcutDialog(original)
        with patch('little_launcher.icon_picker.IconPicker') as picker:
            picker.return_value.exec.return_value=QDialog.Accepted
            picker.return_value.value.return_value={'IconPath':'','IconGlyph':'A','IconColor':'#ff0000'}
            dialog._pick_icon()
        value=dialog.value();self.assertEqual(value['IconGlyph'],'A');self.assertEqual(value['IconPath'],'')
        self.assertEqual(value['Future'],original['Future']);self.assertEqual(original['IconPath'],'old')
        dialog.reject()

    def source(self,root,profile='Default'):
        path=root/profile/'Bookmarks';path.parent.mkdir(parents=True)
        path.write_text(json.dumps({'roots':{'bookmark_bar':{'type':'folder','name':'Bar','children':[{'type':'url','name':'Alpha','url':'https://a.example'},{'type':'url','name':'Beta','url':'https://b.example'}]}}}))
        return BookmarkSource('Chromium',profile,path)

    def test_bulk_checks_survive_filter_and_source_change_clears_selection(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);first=self.source(root);second=self.source(root,'Profile 2')
            before=first.path.read_bytes();picker=BookmarkPicker(catalog=[first,second],multiple=True)
            picker.visible_rows[0].setCheckState(0,Qt.Checked)
            picker.search.setText('Beta');picker.select_visible()
            self.assertEqual([row['Name'] for row in picker.value()],['Alpha','Beta'])
            picker.search.clear();self.assertEqual(picker.list.topLevelItem(0).checkState(0),Qt.Checked)
            picker.profile.setCurrentIndex(1);self.assertFalse(picker.checked);self.assertFalse(picker.confirm.isEnabled())
            self.assertEqual(first.path.read_bytes(),before);picker.reject()

    def test_single_bookmark_picker_keeps_single_value_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            source=self.source(Path(temporary));picker=BookmarkPicker(catalog=[source]);row=picker.list.topLevelItem(0);picker.list.setCurrentItem(row)
            self.assertEqual(picker.value(),{'Name':'Alpha','Url':'https://a.example'});picker.reject()

    def test_append_selection_preserves_existing_hidden_groups_and_unknown_fields(self):
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary));launcher={'Id':'items','Name':'Items','Future':9,'Items':[{'Name':'Windows','Path':'C:\\app.exe','SupportedOperatingSystems':['windows'],'Future':7},{'Name':'Group','IsGroup':True,'Children':[{'Name':'Child','Path':'/bin/true'}]}]}
            controller.edit(lambda doc:doc['Launchers'].append(launcher));expected=deepcopy(controller.launcher('items'))
            self.assertTrue(append_browser_bookmarks(controller,expected,[{'Name':'Alpha','Url':'https://a.example'}]))
            saved=controller.launcher('items');self.assertEqual(saved['Items'][:-1],expected['Items']);self.assertEqual(saved['Future'],9)
            self.assertNotIn('SupportedOperatingSystems',saved['Items'][-1]);self.assertTrue(saved['Items'][-1]['IsWebsite'])

    def test_stale_readonly_removed_and_invalid_bulk_imports_never_write_partial_items(self):
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary));controller.edit(lambda doc:doc['Launchers'].append({'Id':'items','Items':[]}));expected=deepcopy(controller.launcher('items'))
            controller.edit(lambda doc:doc['Launchers'][-1].update(Name='Changed'))
            self.assertFalse(append_browser_bookmarks(controller,expected,[{'Name':'A','Url':'https://a.example'}]))
            expected=deepcopy(controller.launcher('items'));before=controller.store.path.read_bytes()
            with self.assertRaises(DocumentError):append_browser_bookmarks(controller,expected,[{'Name':'A','Url':'https://a.example'},{'Name':'Bad','Url':'javascript:bad'}])
            self.assertEqual(controller.store.path.read_bytes(),before)
            controller.edit(lambda doc:doc['Launchers'][-1].update(IsShared=True));expected=deepcopy(controller.launcher('items'))
            self.assertFalse(append_browser_bookmarks(controller,expected,[{'Name':'A','Url':'https://a.example'}]));self.assertEqual(controller.launcher('items')['Items'],[])
            controller.edit(lambda doc:doc['Launchers'].clear());self.assertFalse(append_browser_bookmarks(controller,expected,[{'Name':'A','Url':'https://a.example'}]))

    def test_application_actions_leave_selection_untouched_and_filters_do_not_choose(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);entries=[{'Name':'Editor','_path':str(root/'editor.desktop'),'Exec':'editor %U'},{'Name':'Chat','_path':str(root/'chat.desktop'),'Exec':'chrome --app-id=abc'}]
            picker=ApplicationList(entries=entries);self.assertIsNone(picker.value());picker.list.setCurrentRow(0)
            menu=picker.row_menu(picker.list.item(1))
            with patch('little_launcher.app_picker.launch_item') as launch:menu.actions()[0].trigger();launch.assert_called_once_with({'Name':'Chat','Path':entries[1]['_path'],'Arguments':''})
            self.assertEqual(picker.value()['Name'],'Editor')
            with patch('little_launcher.app_picker.QDesktopServices.openUrl',return_value=True) as opened:menu.actions()[1].trigger();self.assertEqual(opened.call_args.args[0].toLocalFile(),str(root))
            picker.source.setCurrentIndex(2);self.assertEqual(picker.list.count(),1);self.assertIsNone(picker.value())
            self.assertEqual(picker.list.item(0).data(Qt.UserRole)['Name'],'Chat')
