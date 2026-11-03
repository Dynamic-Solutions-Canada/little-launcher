"""Imported-layout, account-state and trusted-browser-control regression checks."""
from copy import deepcopy
import importlib.util
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from little_launcher.web_preferences import valid_preference

class PreferenceBoundaryTests(unittest.TestCase):
    def test_quick_controls_accept_only_typed_supported_settings(self):
        self.assertTrue(valid_preference('WebRegularWindow', True))
        self.assertTrue(valid_preference('WebBookmarkIconsOnly', True))
        self.assertFalse(valid_preference('WebBookmarkIconsOnly', 1))
        self.assertTrue(valid_preference('WebZoomPercent', 125))
        self.assertTrue(valid_preference('WebAnchor', 9))
        for key, value in [('WebRegularWindow', 1), ('WebZoomPercent', True), ('WebAnchor', 11),
                           ('WebZoomPercent', 401), ('WebFlyoutWidth', 100), ('CustomTrayIconPath', '/tmp/file')]:
            self.assertFalse(valid_preference(key, value))

try:
    from PySide6.QtWidgets import QApplication
    from little_launcher.controller import LauncherController
    from little_launcher.launcher_settings import LauncherDialog
    from little_launcher.sync import LauncherSync
    from little_launcher.sync_page import CloudSyncPage
    from little_launcher.browser import BrowserHost
    QT = True
except ImportError:
    QT = False


@unittest.skipUnless(QT, 'Native layout/account checks require Qt')
class NativeParityTests(unittest.TestCase):
    def test_native_fullscreen_settings_keep_dependency_and_portable_flags(self):
        original={'Id':'video','Kind':1,'WebHomeUrl':'https://example.com','WebFullScreenInWindow':False,'WebFitFullscreenToVideo':True,'Future':{'keep':9}}
        dialog=LauncherDialog(original,position_supported=True)
        self.assertFalse(dialog.fullscreen_video.isEnabled())
        self.assertTrue(dialog.fullscreen_video.isChecked())
        dialog.fullscreen_window.setChecked(True)
        self.assertTrue(dialog.fullscreen_video.isEnabled())
        value=dialog.value()
        self.assertTrue(value['WebFullScreenInWindow']);self.assertTrue(value['WebFitFullscreenToVideo'])
        self.assertEqual(value['Future'],original['Future'])
        dialog.anchor.setCurrentIndex(dialog.anchor.findData(10))
        self.assertEqual(dialog.value()['WebAnchor'],10)

    def test_kde_preview_selects_modern_url_opener_without_overriding_session(self):
        import os
        spec = importlib.util.spec_from_file_location('preview_desktop', Path(__file__).parents[1] / 'dev/preview_desktop.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with patch.dict(os.environ, {'XDG_CURRENT_DESKTOP': 'KDE'}, clear=True):
            module.prepare_desktop_environment()
            self.assertEqual(os.environ['KDE_SESSION_VERSION'], '6')
        with patch.dict(os.environ, {'XDG_CURRENT_DESKTOP': 'KDE', 'KDE_SESSION_VERSION': '5'}, clear=True):
            module.prepare_desktop_environment()
            self.assertEqual(os.environ['KDE_SESSION_VERSION'], '5')
        with patch.dict(os.environ, {'XDG_CURRENT_DESKTOP': 'GNOME'}, clear=True):
            module.prepare_desktop_environment()
            self.assertNotIn('KDE_SESSION_VERSION', os.environ)

    def test_rejected_browser_handoff_cancels_sign_in_and_keeps_existing_account(self):
        from little_launcher.onedrive import SyncError
        class Transport:
            def sign_in(self, browse, cancelled):
                browse('https://login.example/authorize')
                cancelled.wait(2)
                raise SyncError('Sign-in cancelled.')
        with tempfile.TemporaryDirectory() as temporary:
            sync = LauncherSync(LauncherController(Path(temporary)), Transport())
            sync._account(True, 'Existing account')
            statuses = []
            sync.status.connect(statuses.append)
            with patch('little_launcher.sync.QDesktopServices.openUrl', return_value=False) as opener:
                sync.sign_in()
                self.wait(sync)
                opener.assert_called_once()
            self.assertIn('default browser could not open', statuses[-1])
            self.assertTrue(sync.signed_in)
            self.assertEqual(sync.account_name, 'Existing account')

    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def wait(self, service):
        deadline = time.monotonic() + 5
        while service.job and time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(.002)
        self.assertIsNone(service.job)

    def test_imported_windows_icon_path_and_long_name_cannot_widen_the_viewport(self):
        launcher={'Id':'imported', 'Name':'UnbrokenName'*120, 'Kind':1, 'WebHomeUrl':'https://example.com',
                  'TrayIconMode':'Custom', 'CustomTrayIconPath': 'C:\\Users\\someone\\AppData\\Local\\Packages\\' + 'PackageName'*25 + '\\icons\\chosen.png'}
        dialog=LauncherDialog(launcher)
        dialog.show(); self.app.processEvents()
        self.assertEqual(dialog.custom_row.subtitle.text(), 'chosen.png')
        self.assertEqual(dialog.width(),560)
        self.assertLessEqual(dialog.form_scroll.widget().width(),dialog.form_scroll.viewport().width())
        for width in (400,460,560):
            dialog.resize(width,600); self.app.processEvents()
            self.assertLessEqual(dialog.form_scroll.widget().width(),dialog.form_scroll.viewport().width())
        self.assertEqual(dialog.value()['CustomTrayIconPath'],launcher['CustomTrayIconPath'])
        dialog.hide()

    def test_added_browser_settings_preserve_unrelated_data_and_wire_values(self):
        original={'Id':'web', 'Kind':1, 'WebHomeUrl':'https://example.com', 'Future': {'keep':9},
                  'WebBookmarks':[{'Name':'Kept','Url':'https://example.com'}], 'WebAnchor':10}
        dialog=LauncherDialog(original)
        self.assertEqual(dialog.value()['WebAnchor'],10)
        dialog.anchor.setCurrentIndex(dialog.anchor.findData(9))
        dialog.zoom.setCurrentIndex(dialog.zoom.findData(125))
        dialog.bookmark_icons.setChecked(True)
        dialog.reload_open.setChecked(True)
        dialog.links_external.setChecked(True)
        dialog.remember_size.setChecked(False)
        value=dialog.value()
        self.assertEqual((value['WebAnchor'],value['WebZoomPercent']),(9,125))
        self.assertTrue(value['WebReloadOnShow'] and value['WebLinksInBrowser'] and value['WebLockSize'])
        self.assertTrue(value['WebBookmarkIconsOnly'])
        self.assertEqual(value['Future'],original['Future'])
        self.assertEqual(value['WebBookmarks'],original['WebBookmarks'])

    def test_account_state_is_persistent_and_sign_in_out_buttons_reflect_wallet_status(self):
        class Secrets:
            def clear(self): pass
        class Transport:
            secrets=Secrets()
            def account_state(self): return {'signedIn':True,'name':'Synthetic Account'}
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            sync=LauncherSync(controller,Transport())
            page=CloudSyncPage(sync,lambda:None,lambda:None,lambda:None,lambda:None)
            sync.check_account(); self.wait(sync)
            self.assertEqual(page.onedrive.status.text(),'Synthetic Account')
            self.assertTrue(page.sign_in.isHidden())
            self.assertFalse(page.sign_out.isHidden())
            reopened=LauncherSync(LauncherController(Path(temporary)),Transport())
            self.assertTrue(reopened.signed_in)
            self.assertEqual(reopened.account_name,'Synthetic Account')
            sync.sign_out(); self.wait(sync)
            self.assertEqual(page.onedrive.status.text(),'Not signed in')
            self.assertFalse(page.sign_in.isHidden())
            self.assertTrue(page.sign_out.isHidden())
            self.assertFalse(sync.state.get('Automatic',False))

    def test_browser_boundary_does_not_send_portable_icon_bytes_or_shortcut_trees(self):
        with tempfile.TemporaryDirectory() as temporary:
            host=BrowserHost(Path(temporary))
            launcher={'Id':'web','Kind':1,'WebHomeUrl':'https://example.com',
                      'CustomTrayIconData':'A'*1500000,'Items':[{'Path':'C:\\private.exe'}],
                      'WebBookmarks':[{'Name':'Kept','Url':'https://example.com'}]}
            original=deepcopy(launcher)
            with patch.object(host,'_start'):
                host.show(launcher,None)
            sent=host.pending[0]['launcher']
            self.assertNotIn('CustomTrayIconData',sent)
            self.assertNotIn('Items',sent)
            self.assertEqual(sent['WebBookmarks'],launcher['WebBookmarks'])
            self.assertEqual(launcher,original)

    def test_browser_window_icon_uses_the_rendered_custom_choice_without_changing_source(self):
        import base64
        from PySide6.QtGui import QImage, QColor
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'custom.png'
            image=QImage(64,64,QImage.Format_RGBA8888);image.fill(QColor('#de2040'));image.save(str(path))
            launcher={'Id':'custom-window','Kind':1,'TrayIconMode':'Custom','CustomTrayIconPath':str(path),
                      'WebHomeUrl':'https://example.com','Unknown':7}
            original=deepcopy(launcher)
            host=BrowserHost(Path(temporary))
            with patch.object(host,'_start'):host.show(launcher,None)
            payload=host.pending[0]['presentation']['windowIcon']
            rendered=QImage.fromData(base64.b64decode(payload))
            self.assertEqual((rendered.width(),rendered.height()),(64,64))
            self.assertEqual(rendered.pixelColor(32,32),QColor('#de2040'))
            self.assertLess(len(payload),128*1024)
            self.assertEqual(launcher,original)

    def test_saved_tabs_are_local_and_reappear_only_in_browser_presentation(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory=Path(temporary)
            launcher={'Id':'local-tabs','Kind':1,'WebHomeUrl':'https://home.example'}
            host=BrowserHost(directory)
            with patch.object(host,'_start'):host.show(launcher,None)
            session={'tabs':['https://page.example/one','https://page.example/two'],'active':1}
            host._message({'type':'session','id':launcher['Id'],'session':session})
            restored=BrowserHost(directory)
            with patch.object(restored,'_start'):restored.show(launcher,None)
            message=restored.pending[0]
            self.assertEqual(message['presentation']['session'],session)
            self.assertNotIn('WebSessionTabs',message['launcher'])
            self.assertEqual(launcher,{'Id':'local-tabs','Kind':1,'WebHomeUrl':'https://home.example'})
            before=(directory/'web-sessions.json').read_bytes()
            host._message({'type':'session','id':'unknown-launcher','session':session})
            self.assertEqual((directory/'web-sessions.json').read_bytes(),before)

    def test_wayland_browser_start_explicitly_selects_native_backend(self):
        with tempfile.TemporaryDirectory() as temporary:
            host=BrowserHost(Path(temporary))
            with patch.dict('os.environ',{'XDG_SESSION_TYPE':'wayland','LITTLE_LAUNCHER_ELECTRON':'/usr/bin/python3'}), patch.object(host.process,'start'):
                host._start()
            self.assertIn('--ozone-platform=wayland',host.process.arguments())

    def test_profile_maintenance_cannot_target_the_previously_selected_shared_profile(self):
        dialog=LauncherDialog({'Id':'profile','Kind':1,'WebHomeUrl':'https://example.com','WebSharedProfile':True})
        requested=[]
        dialog.profile_action_requested.connect(requested.append)
        dialog.profile.setCurrentIndex(dialog.profile.findData(False))
        self.assertFalse(dialog.clear_data_button.isEnabled())
        self.assertFalse(dialog.reset_permissions_button.isEnabled())
        dialog._profile_action('reset-permissions')
        self.assertEqual(requested,[])
        dialog.profile.setCurrentIndex(dialog.profile.findData(True))
        dialog._profile_action('reset-permissions')
        self.assertEqual(requested,['reset-permissions'])

    def test_remembered_size_is_one_guarded_transaction_and_respects_the_lock(self):
        from little_launcher.app import LauncherApplication
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            controller.edit(lambda doc:doc['Launchers'].append({'Id':'web','Kind':1,'Future':9}))
            owner=object.__new__(LauncherApplication)
            owner.controller=controller
            replies=[]
            owner.browser=SimpleNamespace(send=replies.append)
            owner.web_size({'id':'web','width':640,'height':480,'token':'synthetic'})
            self.assertEqual(controller.launcher('web')['WebFlyoutWidth'],640)
            self.assertEqual(controller.launcher('web')['WebFlyoutHeight'],480)
            self.assertEqual(controller.launcher('web')['Future'],9)
            owner.web_size({'id':'web','width':True,'height':480})
            self.assertFalse(replies[-1]['allowed'])
            controller.edit(lambda doc:doc['Launchers'][0].update(WebLockSize=True))
            revision=controller.revision
            owner.web_size({'id':'web','width':900,'height':600})
            self.assertEqual(controller.revision,revision)
            self.assertFalse(replies[-1]['allowed'])

    def test_synced_profile_change_cannot_redirect_a_stale_maintenance_action(self):
        from little_launcher.app import LauncherApplication
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            controller.edit(lambda doc:doc['Launchers'].append({'Id':'web','Kind':1,'WebSharedProfile':True}))
            owner=object.__new__(LauncherApplication)
            owner.controller=controller
            requests=[]
            owner.browser=SimpleNamespace(send=requests.append)
            owner.profile_action('web','clear-data',False)
            self.assertEqual(requests,[])
            owner.profile_action('web','reset-permissions',True)
            self.assertEqual(requests[0]['action'],'reset-permissions')

    def test_failed_reauthentication_keeps_the_existing_connected_account(self):
        from little_launcher.onedrive import CloudHTTPError
        class Transport:
            def sign_in(self,*_):raise CloudHTTPError(401,'invalid_client')
        with tempfile.TemporaryDirectory() as temporary:
            sync=LauncherSync(LauncherController(Path(temporary)),Transport())
            sync._account(True,'Existing account')
            sync.sign_in();self.wait(sync)
            self.assertTrue(sync.signed_in)
            self.assertEqual(sync.account_name,'Existing account')
            self.assertTrue(sync.state['SignedIn'])

    def test_lost_credentials_update_the_visible_account_state(self):
        from little_launcher.onedrive import SignedOutError
        class Transport:
            def test_connection(self):raise SignedOutError('Sign in to OneDrive again.')
        with tempfile.TemporaryDirectory() as temporary:
            sync=LauncherSync(LauncherController(Path(temporary)),Transport())
            sync._account(True,'Synthetic Account')
            sync.test_connection();self.wait(sync)
            self.assertFalse(sync.signed_in)
            self.assertFalse(sync.state['SignedIn'])

    def test_preview_scope_tracks_downloaded_ids_and_restricts_both_application_identities(self):
        path=Path(__file__).resolve().parents[1]/'dev/preview_integration.py'
        spec=importlib.util.spec_from_file_location('preview_scope_for_test',path)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        loaded=set()
        def call(owner,*args):
            operation=args[2].rsplit('.',1)[-1]
            if operation=='isScriptLoaded':return str(args[3] in loaded).lower()
            if operation=='loadScript':loaded.add(args[4])
            if operation=='unloadScript':loaded.discard(args[3])
            return ''
        with tempfile.TemporaryDirectory() as temporary, patch.object(module.TemporaryPreviewIntegration,'_call',call):
            helper=module.TemporaryPreviewIntegration(Path(temporary),[{'Id':'downloaded-guid'}])
            helper.load()
            self.assertTrue(helper.supports_launcher('downloaded-guid'))
            source=(Path(temporary)/'temporary-kwin.js').read_text()
            self.assertIn(helper.application_id,source)
            self.assertIn(helper.browser_id,source)
            self.assertNotIn("'io.github.RyanEwen.LittleLauncher'",source)
            self.assertNotIn("'io.github.RyanEwen.LittleLauncher.Web'",source)
            helper.sync_launchers([{'Id':'newly-downloaded'}])
            self.assertFalse(helper.supports_launcher('downloaded-guid'))
            self.assertTrue(helper.supports_launcher('newly-downloaded'))
            helper.close()
