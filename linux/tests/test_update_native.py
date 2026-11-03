"""Native update state, guarded restart intent and stable AppImage startup entries."""
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
try:
    from PySide6.QtWidgets import QApplication
    from little_launcher.controller import LauncherController
    from little_launcher.desktop import desktop_argument,register_appimage,set_autostart
    from little_launcher.update_controller import UpdateController
    from little_launcher.update_page import UpdateControls
    from little_launcher.updates import download_release
    from test_updates import image,release
    import io
    QT=True
except ImportError:
    QT=False

@unittest.skipUnless(QT,'Native update checks require Qt')
class NativeUpdateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.app=QApplication.instance() or QApplication([])
    def wait(self,check):
        deadline=time.monotonic()+3
        while not check() and time.monotonic()<deadline:self.app.processEvents();time.sleep(.002)
        self.assertTrue(check())
    def test_check_state_and_source_build_does_not_offer_install(self):
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            with patch('little_launcher.update_controller.current_appimage',return_value=None):updates=UpdateController(controller)
            controls=UpdateControls(updates)
            with patch('little_launcher.update_controller.check_release',return_value=release(image())):
                updates.check();self.wait(lambda:updates.job is None)
            self.assertEqual(updates.state,'available');self.assertFalse(controls.download.isEnabled())
            updates.set_automatic(False);self.assertFalse(updates.automatic());controls.close()
    def test_declined_browser_quit_clears_restart_intent_and_keeps_verified_download(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'Launcher.AppImage';path.write_bytes(image(b'old'));path.chmod(0o755)
            controller=LauncherController(root)
            with patch('little_launcher.update_controller.current_appimage',return_value=None):updates=UpdateController(controller)
            updates.image=path;updates.staged=download_release(release(image(b'new')),path,opener=lambda url:io.BytesIO(image(b'new')))
            called=[];updates.install_requested.connect(lambda:called.append(True))
            updates.request_install();self.assertTrue(updates.install_queued);self.assertEqual(called,[True])
            updates.cancel_restart();self.assertFalse(updates.install_queued);self.assertTrue(updates.staged.staged.exists())
            updates.cancel();self.assertIsNone(updates.staged);self.assertEqual(path.read_bytes(),image(b'old'))
    def test_restart_exec_failure_restores_previous_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'Launcher.AppImage';old=image(b'old');new=image(b'new');path.write_bytes(old);path.chmod(0o755)
            with patch('little_launcher.update_controller.current_appimage',return_value=None):updates=UpdateController(LauncherController(root))
            updates.image=path;updates.staged=download_release(release(new),path,opener=lambda url:io.BytesIO(new));updates.install_queued=True
            with patch('little_launcher.update_controller.subprocess.Popen',side_effect=OSError('fixture exec failure')),patch('little_launcher.update_controller.QMessageBox.critical'):
                self.assertEqual(updates.finish_restart(),1)
            self.assertEqual(path.read_bytes(),old)
    def test_startup_and_app_registration_use_stable_image_path_and_preserve_disable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'Launcher with spaces.AppImage';path.write_bytes(image());path.chmod(0o755)
            with patch.dict('os.environ',{'APPIMAGE':str(path),'APPDIR':'/tmp/temporary-mount','XDG_CONFIG_HOME':str(root/'config'),'XDG_DATA_HOME':str(root/'data')}):
                set_autostart(True);startup=root/'config/autostart/little-launcher.desktop'
                self.assertIn('Exec="'+str(path)+'" --background',startup.read_text());self.assertNotIn('temporary-mount',startup.read_text())
                register_appimage();self.assertTrue((root/'data/applications/io.github.RyanEwen.LittleLauncher.desktop').is_file())
                self.assertIn('NoDisplay=true',(root/'data/applications/io.github.RyanEwen.LittleLauncher.Web.desktop').read_text())
                set_autostart(False);self.assertFalse(startup.exists())
            with self.assertRaises(ValueError):desktop_argument('/tmp/path\nInjected=true')

    def test_failed_automatic_check_preference_restores_checkbox(self):
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            with patch('little_launcher.update_controller.current_appimage',return_value=None):updates=UpdateController(controller)
            controls=UpdateControls(updates)
            with patch.object(controller.preferences,'set',side_effect=OSError('fixture disk error')):controls.automatic.setChecked(False)
            self.assertTrue(controls.automatic.isChecked());self.assertTrue(updates.automatic())

    def test_normal_exit_without_install_discards_staged_file_and_preserves_current_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'Launcher.AppImage';old=image(b'old');new=image(b'new');path.write_bytes(old);path.chmod(0o755)
            with patch('little_launcher.update_controller.current_appimage',return_value=None):updates=UpdateController(LauncherController(root))
            updates.image=path;updates.staged=download_release(release(new),path,opener=lambda url:io.BytesIO(new));pending=updates.staged.staged
            self.assertEqual(updates.finish_restart(),0);self.assertFalse(pending.exists());self.assertEqual(path.read_bytes(),old)

    def test_startup_preference_failure_rolls_back_os_entry_and_control(self):
        from little_launcher.settings import SettingsWindow
        class Integration:
            enabled=False
            def refresh(self):pass
            def supports_launcher(self,identifier):return False
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary));controller.preferences.set('StartAtLogin',False)
            window=SettingsWindow(controller,Integration())
            with patch('little_launcher.settings.set_autostart') as startup,patch.object(controller.preferences,'set',side_effect=OSError('fixture disk error')),patch('little_launcher.settings.QMessageBox.warning'):
                window.startup_control.setChecked(True)
            self.assertEqual([call.args[0] for call in startup.call_args_list],[True,False]);self.assertFalse(window.startup_control.isChecked());window.close()
