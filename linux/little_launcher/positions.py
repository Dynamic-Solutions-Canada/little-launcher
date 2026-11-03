"""KDE-owned logical frame positions, stored only in machine preferences."""
import os
from PySide6.QtCore import ClassInfo, QObject, Slot
from PySide6.QtDBus import QDBusConnection

SERVICE = 'org.littlelauncher.Placement'
INTERFACE = 'org.littlelauncher.Placement'

def placement_service():
    preview = os.environ.get('LITTLE_LAUNCHER_PREVIEW_ID', '')
    return SERVICE + '.Preview.p' + preview if preview and all(c in '0123456789abcdef' for c in preview) else SERVICE

def valid_position(value):
    return isinstance(value, list) and len(value) == 2 and all(type(part) is int and abs(part) <= 1_000_000 for part in value)

@ClassInfo(**{'D-Bus Interface': INTERFACE})
class PlacementBridge(QObject):
    """Only current enabled web launchers using Last Position can update bounded local coordinates."""
    def __init__(self, controller, parent=None):
        super().__init__(parent)
        self.controller = controller
        self.bus = None
        self.service = placement_service()
        self.anchors = {item['Id']: item.get('WebAnchor', 0) for item in controller.launchers}
        controller.changed.connect(self.sync_anchors)

    def start(self):
        if 'KDE' not in os.environ.get('XDG_CURRENT_DESKTOP', '').upper().split(':'):
            return False
        bus = QDBusConnection.sessionBus()
        if not bus.isConnected() or not bus.registerService(self.service):
            return False
        if not bus.registerObject('/Placement', self, QDBusConnection.ExportAllSlots):
            bus.unregisterService(self.service)
            return False
        self.bus = bus
        return True

    def sync_anchors(self):
        anchors = {item['Id']: item.get('WebAnchor', 0) for item in self.controller.launchers}
        changed = {identifier for identifier, anchor in self.anchors.items() if anchors.get(identifier) != anchor}
        self.anchors = anchors
        positions = self.controller.preferences.data.get('WebPositions', {})
        if changed.intersection(positions):
            try:
                self.controller.preferences.set('WebPositions', {key: value for key, value in positions.items() if key not in changed})
            except OSError:
                self.controller.failed.emit('The previous dragged window position could not be cleared.')

    def get(self, identifier):
        value = self.controller.preferences.data.get('WebPositions', {}).get(identifier)
        return value[:] if valid_position(value) else None

    @Slot(str, int, int)
    def Record(self, identifier, x, y):
        launcher = self.controller.launcher(identifier)
        value = [x, y]
        if not launcher or launcher.get('Kind') != 1 or launcher.get('WebAnchor') != 10 or not self.controller.enabled(identifier) or not valid_position(value) or self.get(identifier) == value:
            return
        positions = dict(self.controller.preferences.data.get('WebPositions', {}))
        positions[identifier] = value
        try:
            self.controller.preferences.set('WebPositions', positions)
        except OSError:
            self.controller.failed.emit('The dragged window position could not be saved.')
            return
        self.controller.local_changed.emit()

    def close(self):
        if self.bus:
            self.bus.unregisterObject('/Placement')
            self.bus.unregisterService(self.service)
            self.bus = None
