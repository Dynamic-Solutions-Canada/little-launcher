"""Native Wayland placement of a downloaded shortcut and a preloaded web launcher."""
import http.server
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication
from little_launcher.browser import BrowserHost
from little_launcher.controller import LauncherController
from little_launcher.flyout import ShortcutFlyout
from compositor_probe import CompositorProbe
from preview_integration import TemporaryPreviewIntegration

app=QApplication([])
app.setQuitOnLastWindowClosed(False)
root=Path(tempfile.mkdtemp(prefix='parity-placement-',dir='/work'))
controller=LauncherController(root)
shortcut={'Id':'downloaded-shortcut','Name':'Downloaded shortcut','ViewMode':1,'Items':[{'Name':'Example','Path':'https://example.com','IsWebsite':True}]}
controller.edit(lambda doc:doc['Launchers'].append(shortcut))
helper=TemporaryPreviewIntegration(root,controller.launchers)
app.setDesktopFileName(helper.application_id)
os.environ['LITTLE_LAUNCHER_PREVIEW_ID']=helper.preview_id
os.environ['LITTLE_LAUNCHER_ELECTRON']='/opt/electron/electron'
helper.load()
probe=CompositorProbe(['LittleLauncherFlyout:downloaded-shortcut:', 'LittleLauncherFlyout:downloaded-web:', 'LittleLauncherWindow:downloaded-regular:', 'Downloaded regular fixture'],resource_class=None)
window=ShortcutFlyout(shortcut,[1100,770],controller=controller)
browser=BrowserHost(root)
errors=[]
browser.failed.connect(errors.append)
browser.quit_ready.connect(app.quit)
loaded=threading.Event()
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self,*_):pass
    def do_GET(self):
        loaded.set();self.send_response(200);self.send_header('Content-Type','text/html');self.end_headers();self.wfile.write(b'<title>Wayland web fixture</title>Disposable fixture')
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
result={}

def verify(row):
    assert row, 'No compositor observation for the owned test window'
    area=row['workArea']
    x=max(area['x']+8,min(area['x']+area['width']-row['width']-8,1100-row['width']/2))
    y=area['y']+area['height']-row['height']-8
    assert abs(row['x']-round(x))<=1 and abs(row['y']-round(y))<=1,row
    assert row.get('wayland') is True or 'wayland' in row.get('clientType','').lower() or 'XdgToplevelWindow' in row.get('clientType',''),row
    assert row.get('x11') is not True,row
    return row

def open_web():
    try:
        result['shortcut']=verify(probe.find('LittleLauncherFlyout:downloaded-shortcut:'))
        window.close()
        web={'Id':'downloaded-web','Name':'Downloaded web','Kind':1,'WebHiddenPolicy':2,
             'WebHomeUrl':f'http://127.0.0.1:{server.server_port}/','WebFlyoutWidth':480,'WebFlyoutHeight':320}
        controller.edit(lambda doc:doc['Launchers'].append(web))
        helper.sync_launchers(controller.launchers)
        assert helper.supports_launcher(web['Id'])
        browser.configure([web])
        started=time.monotonic()
        def wait_preload():
            if loaded.is_set() and browser.ready:
                browser.show(web,[1100,770])
                QTimer.singleShot(900,open_regular)
            elif time.monotonic()-started<12:QTimer.singleShot(100,wait_preload)
            else:
                result['error']='Preload did not become ready';finish()
        wait_preload()
    except Exception as error:
        result['error']=str(error);finish()

def open_regular():
    try:
        result['web']=verify(probe.find('LittleLauncherFlyout:downloaded-web:'))
        web=controller.launcher('downloaded-web').copy()
        regular={**web,'Id':'downloaded-regular','Name':'Downloaded regular fixture','WebRegularWindow':True,'WebAnchor':8}
        controller.edit(lambda doc:doc['Launchers'].append(regular))
        helper.sync_launchers(controller.launchers)
        browser.configure([web,regular])
        # Match a saved Keep running + Regular window launcher: preload has no activation anchor.
        QTimer.singleShot(600,lambda:browser.show(regular,[1100,770]))
        QTimer.singleShot(1500,finish)
    except Exception as error:
        result['error']=str(error);finish()

def finish():
    try:
        if 'error' not in result:
            row=probe.find('Downloaded regular fixture')
            assert row, ('Regular native window did not restore its human title', list(probe.records.values()))
            frame=row['frame'];area=row['workArea']
            assert abs(frame['x']-(area['x']+(area['width']-frame['width'])/2))<=1,row
            assert abs(frame['y']-(area['y']+area['height']-frame['height']-8))<=1,row
            assert 'XdgToplevelWindow' in row['clientType'],row
            result['regularBottomCenter']=row
            result.update(passed=True,downloadedIdsScoped=True,preloadedFirstOpenAnchored=True,preloadedRegularBottomCenter=True,nativeWayland=True)
        if errors:result['browserErrors']=errors
    except Exception as error:result['error']=str(error)
    finally:
        (root/'result.json').write_text(json.dumps(result,indent=2)+'\n')
        print(json.dumps(result,indent=2),flush=True)
        probe.close();helper.close();server.shutdown();window.hide()
        browser.request_quit()

QTimer.singleShot(100,window.show)
QTimer.singleShot(1100,open_web)
QTimer.singleShot(20000,lambda:(result.update(error='Fixture deadline expired'),finish()))
app.exec()
assert result.get('passed') and 'error' not in result,result
