"""XDG precedence regressions that do not depend on the host's installed applications."""
from pathlib import Path
import tempfile
import unittest

from little_launcher.catalog import DesktopCatalog


class DesktopCatalogTests(unittest.TestCase):
    def test_hidden_user_override_does_not_resurrect_system_application(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            user, system = root / 'user', root / 'system'
            for path in (user, system):
                (path / 'applications').mkdir(parents=True)
            (system / 'applications/editor.desktop').write_text('[Desktop Entry]\nType=Application\nName=Editor\nExec=editor\n')
            (user / 'applications/editor.desktop').write_text('[Desktop Entry]\nType=Application\nName=Editor\nHidden=true\n')
            catalog = DesktopCatalog([user, system], desktop='KDE')
            self.assertEqual(catalog.applications(), [])
            self.assertEqual(catalog.resolve('editor.desktop')['Hidden'], 'true')

    def test_desktop_specific_entries_and_nested_ids_resolve_without_exec_interpretation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            apps = root / 'applications/vendor'
            apps.mkdir(parents=True)
            (apps / 'tool.desktop').write_text('[Desktop Entry]\nType=Application\nName=Tool\nOnlyShowIn=KDE;\nExec=tool $(never-run)\n')
            catalog = DesktopCatalog([root], desktop='KDE')
            self.assertEqual(catalog.applications()[0]['id'], 'vendor-tool.desktop')
            self.assertEqual(catalog.resolve('vendor-tool.desktop')['Exec'], 'tool $(never-run)')
            self.assertEqual(DesktopCatalog([root], desktop='GNOME').applications(), [])
