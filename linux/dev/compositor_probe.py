"""Private-fixture geometry for owned QA windows; never connects to the host compositor."""
import asyncio
import json
import os
from pathlib import Path
import threading
from uuid import uuid4

from dbus_next.aio import MessageBus
from dbus_next.service import ServiceInterface, method
from little_launcher.desktop import DesktopIntegration


class CompositorProbe:
    def __init__(self, prefix, resource_class='io.github.RyanEwen.LittleLauncher'):
        assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
        self.latest = None
        self.records = {}
        self.sequence = 0
        self.ready = threading.Event()
        self.identity = 'll-qa-probe-' + uuid4().hex
        probe = self
        class Observer(ServiceInterface):
            def __init__(self): super().__init__('org.littlelauncher.QAProbe')
            @method()
            def Record(self, value: 's'):
                probe.sequence += 1
                probe.latest = json.loads(value)
                probe.latest['sequence'] = probe.sequence
                probe.records[probe.latest['title']] = probe.latest
        async def observe():
            bus = await MessageBus().connect()
            bus.export('/Probe', Observer())
            await bus.request_name('org.littlelauncher.QAProbe')
            self.ready.set()
            await asyncio.Future()
        threading.Thread(target=lambda: asyncio.run(observe()), daemon=True).start()
        assert self.ready.wait(5)
        self.integration = DesktopIntegration()
        script = Path('/work') / (self.identity + '.js')
        script.write_text('''function record(window) {
if ((CLASS!==null && String(window.resourceClass)!==CLASS) || !PREFIX.some(function(prefix){return String(window.caption).indexOf(prefix)===0;})) return;
var client=window.clientGeometry;
callDBus('org.littlelauncher.QAProbe','/Probe','org.littlelauncher.QAProbe','Record',JSON.stringify({
 frame:{x:window.frameGeometry.x,y:window.frameGeometry.y,width:window.frameGeometry.width,height:window.frameGeometry.height},
 title:window.caption,x:client.x,y:client.y,width:client.width,height:client.height,active:window.active,
 resourceClass:String(window.resourceClass),clientType:String(window),wayland:window.waylandClient,x11:window.x11Client,
 workArea:(function(){var output=workspace.screenAt({x:client.x+client.width/2,y:client.y+client.height/2});var area=workspace.clientArea(KWin.PlacementArea,output,workspace.currentDesktop);return{x:area.x,y:area.y,width:area.width,height:area.height};})()}));
}
workspace.windowAdded.connect(function(window) {
// A newly mapped Wayland window can initially have an empty caption.
// Attach first; record() filters every later observation to the requested scope.
record(window);
window.frameGeometryChanged.connect(function(){record(window);});
window.captionChanged.connect(function(){record(window);});
window.activeChanged.connect(function(){record(window);});
});
workspace.windowList().forEach(record);'''.replace('PREFIX', json.dumps(prefix if isinstance(prefix, list) else [prefix])).replace('CLASS', json.dumps(resource_class)))
        self.integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.loadScript', str(script), self.identity)
        self.integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.start')

    def find(self, prefix):
        candidates = [row for title, row in self.records.items() if title.startswith(prefix)]
        return max(candidates, key=lambda row: row['sequence']) if candidates else None

    def close(self):
        self.integration._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.unloadScript', self.identity)
