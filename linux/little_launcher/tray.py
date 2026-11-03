"""Native StatusNotifierItem and dbusmenu, retaining host-supplied activation coordinates.

Each icon owns a bus connection: disconnecting it removes the icon from the host even
when other launchers remain. Qt signals transfer actions back to the GUI thread.
"""
import asyncio
import threading

from dbus_next import Variant
from dbus_next.aio import MessageBus
from dbus_next.constants import PropertyAccess
from dbus_next.service import ServiceInterface, dbus_property, method, signal
from PySide6.QtCore import QObject, Signal


class TrayEvents(QObject):
    activated = Signal(str, int, int)
    menu_action = Signal(str, str)
    unavailable = Signal(str)


class LauncherMenu(ServiceInterface):
    """Small desktop-owned menu; action IDs retain their meaning across layout reads."""

    ACTIONS = {5: ("Edit Launcher Settings", "launcher-settings"),
               3: ("App Settings", "settings"), 4: ("Exit", "quit")}

    def __init__(self, identifier, events):
        super().__init__("com.canonical.dbusmenu")
        self.identifier = identifier
        self.order = (5, 10, 3, 11, 4) if identifier != 'settings' else (3, 11, 4)
        self.events = events

    @dbus_property(access=PropertyAccess.READ)
    def Version(self) -> 'u':
        return 3

    @dbus_property(access=PropertyAccess.READ)
    def TextDirection(self) -> 's':
        return "ltr"

    @dbus_property(access=PropertyAccess.READ)
    def Status(self) -> 's':
        return "normal"

    @dbus_property(access=PropertyAccess.READ)
    def IconThemePath(self) -> 'as':
        return []

    def _properties(self, identifier):
        if identifier == 0:
            return {"children-display": Variant('s', 'submenu')}
        if identifier in (10, 11):
            return {'type': Variant('s', 'separator'), 'visible': Variant('b', True)}
        label = self.ACTIONS.get(identifier, ("", ""))[0]
        return {"label": Variant('s', label), "enabled": Variant('b', True),
                "visible": Variant('b', True)}

    @method()
    def GetLayout(self, parent: 'i', depth: 'i', names: 'as') -> 'u(ia{sv}av)':
        children = []
        if parent == 0 and depth != 0:
            children = [Variant('(ia{sv}av)', [key, self._properties(key), []])
                        for key in self.order]
        return [1, [parent, self._properties(parent), children]]

    @method()
    def GetGroupProperties(self, identifiers: 'ai', names: 'as') -> 'a(ia{sv})':
        return [[identifier, self._properties(identifier)] for identifier in identifiers]

    @method()
    def GetProperty(self, identifier: 'i', name: 's') -> 'v':
        return self._properties(identifier).get(name, Variant('s', ''))

    @method()
    def Event(self, identifier: 'i', event: 's', data: 'v', timestamp: 'u'):
        if event == "clicked" and identifier in self.ACTIONS and identifier in self.order:
            self.events.menu_action.emit(self.identifier, self.ACTIONS[identifier][1])

    @method()
    def EventGroup(self, events: 'a(isvu)') -> 'ai':
        for identifier, event, data, timestamp in events:
            self.Event(identifier, event, data, timestamp)
        return []

    @method()
    def AboutToShow(self, identifier: 'i') -> 'b':
        return False

    @method()
    def AboutToShowGroup(self, identifiers: 'ai') -> 'aiai':
        return [[], []]


class LauncherIndicator(ServiceInterface):
    """Tray-host activation is the anchor authority, rather than a later pointer query."""

    def __init__(self, item, events):
        super().__init__("org.kde.StatusNotifierItem")
        self.item = item
        self.events = events

    @dbus_property(access=PropertyAccess.READ)
    def Category(self) -> 's':
        return 'ApplicationStatus'

    @dbus_property(access=PropertyAccess.READ)
    def Id(self) -> 's':
        return 'little-launcher-' + self.item['id']

    @dbus_property(access=PropertyAccess.READ)
    def Title(self) -> 's':
        return self.item['name']

    @dbus_property(access=PropertyAccess.READ)
    def Status(self) -> 's':
        return 'Active'

    @dbus_property(access=PropertyAccess.READ)
    def WindowId(self) -> 'u':
        return 0

    @dbus_property(access=PropertyAccess.READ)
    def IconName(self) -> 's':
        return self.item.get('icon', 'applications-other')

    @dbus_property(access=PropertyAccess.READ)
    def IconPixmap(self) -> 'a(iiay)':
        return self.item.get('pixmaps', [])

    @dbus_property(access=PropertyAccess.READ)
    def ToolTip(self) -> '(sa(iiay)ss)':
        return ['', [], self.item['name'], 'Little Launcher']

    @dbus_property(access=PropertyAccess.READ)
    def ItemIsMenu(self) -> 'b':
        return False

    @dbus_property(access=PropertyAccess.READ)
    def Menu(self) -> 'o':
        return '/Menu'

    @signal()
    def NewIcon(self):
        pass

    @signal()
    def NewTitle(self):
        pass

    @signal()
    def NewToolTip(self):
        pass

    @method()
    def Activate(self, x: 'i', y: 'i'):
        self.events.activated.emit(self.item['id'], x, y)

    @method()
    def SecondaryActivate(self, x: 'i', y: 'i'):
        self.events.activated.emit(self.item['id'], x, y)

    @method()
    def ContextMenu(self, x: 'i', y: 'i'):
        self.events.menu_action.emit(self.item['id'], 'settings')

    @method()
    def Scroll(self, delta: 'i', orientation: 's'):
        pass

    @method()
    def ProvideXdgActivationToken(self, token: 's'):
        # The token is session-local input, not persisted or logged.
        pass


class TrayBus:
    """Own tray registration on a private asyncio thread and recover after shell restart."""

    def __init__(self):
        self.events = TrayEvents()
        self.loop = asyncio.new_event_loop()
        self.mutex = asyncio.Lock()
        self.items = {}
        self.connections = {}
        self.closed = False
        self.thread = threading.Thread(target=self._run, name='launcher-tray-bus', daemon=True)
        self.thread.start()

    def _run(self):
        asyncio.set_event_loop(self.loop)
        self.loop.create_task(self._watch())
        try:
            self.loop.run_forever()
        finally:
            pending = asyncio.all_tasks(self.loop)
            for task in pending:
                task.cancel()
            if pending:
                self.loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
            self.loop.close()

    def update(self, items):
        """Replace icon metadata without sharing mutable GUI-owned data with the bus thread."""
        if self.closed:
            return
        future = asyncio.run_coroutine_threadsafe(self._update(items), self.loop)
        future.add_done_callback(self._completed)

    def _completed(self, future):
        try:
            future.result()
        except Exception:
            self.events.unavailable.emit('Desktop tray unavailable. Open Little Launcher from Applications to manage launchers.')

    async def _update(self, items):
        async with self.mutex:
            await self._replace(items)

    async def _replace(self, items):
        if self.closed:
            return
        desired = {item['id']: item for item in items}
        for identifier in list(self.connections):
            if identifier not in desired:
                self.connections.pop(identifier)[0].disconnect()
        self.items = desired
        for identifier, item in desired.items():
            if identifier not in self.connections:
                bus = await MessageBus().connect()
                indicator = LauncherIndicator(item, self.events)
                bus.export('/StatusNotifierItem', indicator)
                bus.export('/Menu', LauncherMenu(identifier, self.events))
                self.connections[identifier] = (bus, indicator)
            else:
                indicator = self.connections[identifier][1]
                indicator.item = item
                indicator.emit_properties_changed({'Title': item['name'], 'IconName': indicator.IconName,
                                                   'ToolTip': indicator.ToolTip, 'IconPixmap': indicator.IconPixmap})
                indicator.NewIcon()
                indicator.NewTitle()
                indicator.NewToolTip()
        await asyncio.wait_for(self._register(), timeout=3)

    async def _register(self):
        for bus, _indicator in self.connections.values():
            introspection = await bus.introspect('org.kde.StatusNotifierWatcher', '/StatusNotifierWatcher')
            proxy = bus.get_proxy_object('org.kde.StatusNotifierWatcher', '/StatusNotifierWatcher', introspection)
            await proxy.get_interface('org.kde.StatusNotifierWatcher').call_register_status_notifier_item('/StatusNotifierItem')

    async def _watch(self):
        while not self.closed:
            try:
                async with self.mutex:
                    await asyncio.wait_for(self._register(), timeout=3)
            except Exception:
                pass
            await asyncio.sleep(5)

    def close(self):
        """Unregister every icon before ending the loop; no desktop configuration changes."""
        self.closed = True
        async def disconnect():
            async with self.mutex:
                for bus, _indicator in self.connections.values():
                    bus.disconnect()
                self.connections.clear()
        try:
            asyncio.run_coroutine_threadsafe(disconnect(), self.loop).result(timeout=5)
        finally:
            self.loop.call_soon_threadsafe(self.loop.stop)
            self.thread.join(timeout=1)
