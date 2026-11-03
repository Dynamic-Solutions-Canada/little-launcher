"""Device-local tab addresses; never part of launcher exports, sync, or browser credentials."""
from copy import deepcopy
import json
from pathlib import Path
from urllib.parse import urlsplit
from .document import DocumentError
from .storage import atomic_write

MAX_BYTES = 4 * 1024 * 1024

def valid_session(value):
    if not isinstance(value, dict) or not isinstance(value.get('tabs'), list) or len(value['tabs']) > 64:
        return False
    if type(value.get('active')) is not int or not 0 <= value['active'] < max(1, len(value['tabs'])):
        return False
    for address in value['tabs']:
        if not isinstance(address, str) or len(address) > 8192:
            return False
        try:
            if urlsplit(address).scheme not in ('http', 'https') or not urlsplit(address).netloc:
                return False
        except ValueError:
            return False
    return True

class WebSessions:
    """Atomic bounded storage independent of portable settings and cloud revisions."""
    def __init__(self, directory):
        self.path = Path(directory) / 'web-sessions.json'
        self.entries = {}
        self.error = False
        try:
            if self.path.exists():
                if self.path.stat().st_size > MAX_BYTES:
                    raise ValueError()
                data = json.loads(self.path.read_text())
                if not isinstance(data, dict) or data.get('version') != 1 or not isinstance(data.get('launchers'), dict):
                    raise ValueError()
                if not all(isinstance(key, str) and valid_session(value) for key, value in data['launchers'].items()):
                    raise ValueError()
                self.entries = data['launchers']
        except (OSError, ValueError, UnicodeError):
            self.error = True

    def get(self, identifier):
        return deepcopy(self.entries.get(identifier))

    def save(self, identifier, value):
        if self.error:
            raise DocumentError('Saved browser tabs are unreadable. The existing session file was preserved.')
        if not isinstance(identifier, str) or not valid_session(value):
            raise DocumentError('The browser returned an invalid tab session.')
        if self.entries.get(identifier) == value:
            return
        candidate = deepcopy(self.entries)
        if value['tabs']:
            candidate[identifier] = deepcopy(value)
        else:
            candidate.pop(identifier, None)
        raw = (json.dumps({'version': 1, 'launchers': candidate}, indent=2) + '\n').encode()
        if len(raw) > MAX_BYTES:
            raise DocumentError('Saved browser tabs exceed the local storage limit. Existing sessions were preserved.')
        atomic_write(self.path, raw)
        self.entries = candidate
