"""Opt-in transient KWin integration scoped to this disposable preview's unique app identities and launcher IDs."""
import atexit
import hashlib
import json
from pathlib import Path
import re
import subprocess
from uuid import uuid4

from little_launcher.desktop import DesktopIntegration, ROOT
from little_launcher.positions import SERVICE


class TemporaryPreviewIntegration(DesktopIntegration):
    """Load a private copy without installing files or changing kwinrc; unload on normal quit."""

    def __init__(self, directory, launchers):
        self.SCRIPT_ID = 'little-launcher-preview-' + uuid4().hex
        self.directory = Path(directory)
        self.preview_id = hashlib.sha256(str(self.directory.resolve()).encode()).hexdigest()[:16]
        self.application_id = 'io.github.RyanEwen.LittleLauncher.Preview.' + self.preview_id
        self.browser_id = 'io.github.RyanEwen.LittleLauncher.Web.Preview.' + self.preview_id
        self.identifiers = sorted(item['Id'] for item in launchers)
        self.loaded = False
        super().__init__()

    def load(self):
        """Permit only this preview instance and its current launcher IDs, including downloads."""
        if not self.identifiers or any(not re.fullmatch(r'[a-zA-Z0-9-]+', value) for value in self.identifiers):
            raise OSError('No valid preview launcher identities were found.')
        source = (ROOT / 'desktop/kwin/contents/code/main.js').read_text()
        source = source.replace("'io.github.RyanEwen.LittleLauncher.Web'", repr(self.browser_id))
        source = source.replace("'io.github.RyanEwen.LittleLauncher'", repr(self.application_id))
        source = source.replace("'org.littlelauncher.Placement'", repr(SERVICE + '.Preview.p' + self.preview_id), 1)
        pattern = '([a-zA-Z0-9-]+)'
        if source.count(pattern) != 3:
            raise OSError('Preview helper scoping needs review before this helper can be loaded.')
        scoped = '(' + '|'.join(self.identifiers) + ')'
        path = self.directory / 'temporary-kwin.js'
        path.write_text(source.replace(pattern, scoped))
        self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.loadScript', str(path), self.SCRIPT_ID)
        self.loaded = True
        atexit.register(self.close)
        self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.start')
        self.refresh()
        if not self.enabled:
            self.close()
            raise OSError('The temporary preview integration could not be started.')
        (self.directory / 'temporary-integration.json').write_text(json.dumps({'scriptId': self.SCRIPT_ID, 'launcherIds': self.identifiers, 'applicationId': self.application_id, 'browserId': self.browser_id}, indent=2)+'\n')

    def sync_launchers(self, launchers):
        identifiers = sorted(item['Id'] for item in launchers)
        if identifiers == self.identifiers:
            return
        self.close()
        self.identifiers = identifiers
        if identifiers:
            self.load()
        else:
            self.enabled = False

    def supports_launcher(self, identifier):
        """Downloaded launchers are eligible only within the uniquely identified preview instance."""
        return self.enabled and identifier in self.identifiers

    def close(self):
        """Remove only this run's script registration; keep KDE's saved configuration intact."""
        if not self.loaded:
            return
        try:
            self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.unloadScript', self.SCRIPT_ID)
            self.loaded = False
            (self.directory / 'temporary-integration.json').unlink(missing_ok=True)
        except (OSError, subprocess.SubprocessError):
            # Retain the loaded flag so atexit can retry after the Qt quit callback.
            pass
