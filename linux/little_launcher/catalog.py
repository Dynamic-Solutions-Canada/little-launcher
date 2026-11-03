"""XDG application identities, including user overrides and desktop visibility rules."""
import configparser
import os
from pathlib import Path
import shutil


class DesktopCatalog:
    """Read installed desktop entries without interpreting their Exec field as shell code."""

    def __init__(self, directories=None, desktop=None):
        if directories is None:
            directories = [Path(os.environ.get('XDG_DATA_HOME', Path.home() / '.local/share'))]
            directories += [Path(value) for value in os.environ.get('XDG_DATA_DIRS', '/usr/local/share:/usr/share').split(':')]
        self.directories = [Path(value) / 'applications' for value in directories]
        self.desktop = set((desktop if desktop is not None else os.environ.get('XDG_CURRENT_DESKTOP', '')).split(':'))

    def _entries(self):
        """First desktop ID wins, even when a user override explicitly hides the app."""
        seen = set()
        for directory in self.directories:
            if not directory.is_dir():
                continue
            for path in sorted(directory.rglob('*.desktop')):
                identifier = str(path.relative_to(directory)).replace('/', '-')
                if identifier not in seen:
                    seen.add(identifier)
                    yield identifier, path

    @staticmethod
    def read(path):
        """Parse an installed descriptor; all launch execution remains with gio/gtk-launch."""
        parser = configparser.ConfigParser(interpolation=None, strict=False)
        parser.optionxform = str
        try:
            parser.read(path, encoding='utf-8')
            if parser.has_section('Desktop Entry'):
                return {**dict(parser['Desktop Entry']), '_path': str(path)}
        except (OSError, UnicodeError, configparser.Error):
            pass
        return None

    def resolve(self, identifier):
        path = Path(identifier)
        if path.is_absolute():
            return self.read(path) if path.is_file() else None
        return next((self.read(path) for identity, path in self._entries() if identity == identifier), None)

    def applications(self):
        """Return discoverable applications, respecting the active DE and XDG override order."""
        result = []
        for identifier, path in self._entries():
            entry = self.read(path)
            if not entry or entry.get('Type') != 'Application' or not entry.get('Name'):
                continue
            if any(entry.get(key, '').lower() == 'true' for key in ('Hidden', 'NoDisplay')):
                continue
            only = set(filter(None, entry.get('OnlyShowIn', '').split(';')))
            excluded = set(filter(None, entry.get('NotShowIn', '').split(';')))
            if (only and not only.intersection(self.desktop)) or excluded.intersection(self.desktop):
                continue
            required = entry.get('TryExec')
            if required and not shutil.which(required):
                continue
            if not entry.get('Exec') and entry.get('DBusActivatable', '').lower() != 'true':
                continue
            result.append({**entry, 'id': identifier})
        return sorted(result, key=lambda entry: entry['Name'].casefold())
