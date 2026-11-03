"""GUI-thread document ownership and transaction notifications."""
from copy import deepcopy
from pathlib import Path

from PySide6.QtCore import QObject, Signal

from .document import DocumentError, launcher_hidden, launcher_supported, validate_document
from .storage import DocumentStore, LocalPreferences


class LauncherController(QObject):
    changed = Signal()
    failed = Signal(str)
    hidden_notice = Signal(str)
    local_changed = Signal()

    def __init__(self, directory: Path):
        super().__init__()
        self.directory = directory
        self.store = DocumentStore(directory / 'settings.json')
        self.preferences = LocalPreferences(directory / 'linux.json')
        self.document, self.revision = self.store.read()

    @property
    def launchers(self):
        return self.document['Launchers']

    def launcher(self, identifier):
        return next((item for item in self.launchers if item['Id'] == identifier), None)

    def enabled(self, identifier):
        launcher=self.launcher(identifier)
        return launcher is not None and launcher_supported(launcher) and identifier not in self.preferences.data.get('DisabledLauncherIds', [])

    def set_enabled(self, identifier, enabled):
        if enabled and not launcher_supported(self.launcher(identifier) or {}):
            self.failed.emit('This shortcut launcher belongs to another operating system. Create a Linux launcher instead.');return False
        disabled = set(self.preferences.data.get('DisabledLauncherIds', []))
        disabled.discard(identifier) if enabled else disabled.add(identifier)
        try:
            self.preferences.set('DisabledLauncherIds', sorted(disabled))
        except OSError as error:
            self.failed.emit(str(error))
            return False
        self.local_changed.emit()
        return True

    def edit(self, mutation):
        """Commit a detached transaction and publish only after durable save succeeds."""
        candidate = deepcopy(self.document)
        try:
            mutation(candidate)
            revision = self.store.commit(candidate, self.revision)
        except (DocumentError, OSError) as error:
            self.failed.emit(str(error))
            return False
        self.document = candidate
        self.revision = revision
        self.changed.emit()
        return True

    def replace_launcher(self, launcher, expected=None):
        """Replace exactly one stable launcher identity, retaining every other launcher."""
        def replace(document):
            for index, existing in enumerate(document['Launchers']):
                if existing['Id'] == launcher['Id']:
                    if expected is not None and existing != expected:
                        raise DocumentError('This launcher changed while the editor was open. Reopen it before saving.')
                    read_only = existing.get('IsShared') and not existing.get('IsSharedOwner') and not existing.get('SharedTwoWay')
                    if read_only and any(launcher.get(field, []) != existing.get(field, [])
                                         for field in ('Items', 'WebBookmarks')):
                        raise DocumentError('This subscribed launcher is read-only.')
                    document['Launchers'][index] = deepcopy(launcher)
                    return
            raise DocumentError('This launcher was removed. Reload before editing it.')
        return self.edit(replace)

    def import_document(self, document):
        """Merge explicitly imported launcher identities; importing never clears unrelated rows."""
        from .portable_icons import unpack_icons, preserve_icons
        imported = unpack_icons(validate_document(document), self.directory)
        previously_hidden = {item['Id'] for item in self.launchers if launcher_hidden(item)}
        def merge(current):
            if 'Extensions' in imported:
                current['Extensions'] = deepcopy(imported['Extensions'])
            positions = {item['Id']: index for index, item in enumerate(current['Launchers'])}
            for item in imported['Launchers']:
                if item['Id'] in positions:
                    current['Launchers'][positions[item['Id']]] = preserve_icons(current['Launchers'][positions[item['Id']]],item)
                else:
                    current['Launchers'].append(item)
        saved = self.edit(merge)
        if saved:
            noticed = set(self.preferences.data.get('HiddenLauncherNotices', []))
            newly_hidden = [item for item in self.launchers if launcher_hidden(item)
                            and item['Id'] not in previously_hidden and item['Id'] not in noticed]
            if newly_hidden:
                noticed.update(item['Id'] for item in newly_hidden)
                try:
                    self.preferences.set('HiddenLauncherNotices', sorted(noticed))
                except OSError:
                    # The import is already durable; a notice preference must not report it as failed.
                    pass
                names = ', '.join(item.get('Name', 'Launcher') for item in newly_hidden)
                self.hidden_notice.emit(names + '\n\nThese shortcut launchers belong to Windows and are disabled on Linux. They stay in the launcher list and sync. Create a separate Linux shortcut launcher for this system.')
        return saved
