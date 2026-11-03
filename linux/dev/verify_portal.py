"""Interactive fixture driver for actual private PipeWire capture, never the host desktop.

Starts a localhost test page and accepts only its screen-capture permission. Its button
must be clicked through the private X server, followed by the private KDE source picker.
"""
import http.server
import json
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
import queue
import subprocess
import threading
import time
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.document import shows_bookmark_bar, web_address

reports = queue.Queue()
root = Path('/work/portal-qualification')
root.mkdir(exist_ok=True)


class Fixture(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/html')
        self.end_headers()
        if self.path.startswith('/result?'):
            reports.put(self.path)
            self.wfile.write(b'ok')
        else:
            self.wfile.write(b'''<html><style>body{background:#cf7c1b}button{margin:24px;width:240px;height:64px}</style>
            <button id="share">Share this private desktop</button><video autoplay muted></video><script>
            document.getElementById('share').onclick = async () => {
              try {
                const stream=await navigator.mediaDevices.getDisplayMedia({video:true,audio:false});
                const video=document.querySelector('video');
                video.srcObject=stream;
                await video.play();
                await new Promise(resolve => video.requestVideoFrameCallback(resolve));
                await new Promise(resolve => video.requestVideoFrameCallback(resolve));
                const canvas=document.createElement('canvas');
                canvas.width=video.videoWidth;
                canvas.height=video.videoHeight;
                const context=canvas.getContext('2d', {willReadFrequently:true});
                context.drawImage(video,0,0);
                const pixels=context.getImageData(0,0,canvas.width,canvas.height).data;
                let low=255, high=0;
                for(let i=0;i<pixels.length;i+=4) {
                  low=Math.min(low,pixels[i],pixels[i+1],pixels[i+2]);
                  high=Math.max(high,pixels[i],pixels[i+1],pixels[i+2]);
                }
                fetch('/result?active=yes&width='+video.videoWidth+'&height='+video.videoHeight+
                      '&frames=2&pixelRange='+(high-low));
              } catch(error) { fetch('/result?active=no&error='+encodeURIComponent(error.name)); }
            };</script></html>''')


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Fixture)
threading.Thread(target=server.serve_forever, daemon=True).start()
process = subprocess.Popen(['/opt/electron/electron', '/source/linux/browser',
    '--launcher-data=' + str(root / 'data')], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=(root / 'engine.stderr').open('w'), text=True)
messages = queue.Queue()
records = []


def send(message):
    if message.get('type') in ('show', 'configure'):
        message = {**message, 'presentation': {'homeUrl': web_address(message['launcher']),
                   'showBookmarkBar': shows_bookmark_bar(message['launcher'])}}
    process.stdin.write(json.dumps(message) + '\n')
    process.stdin.flush()


def read():
    for line in process.stdout:
        try:
            message = json.loads(line)
            records.append(message)
            print(json.dumps(message), flush=True)
            if message.get('type') == 'permission':
                send({'type': 'reply', 'token': message['token'],
                      'allowed': message.get('permission') in ('media', 'display-capture')})
            messages.put(message)
        except ValueError:
            pass


threading.Thread(target=read, daemon=True).start()
try:
    while messages.get(timeout=20).get('type') != 'ready':
        pass
    send({'type': 'show', 'launcher': {'Id': 'portal-test', 'Name': 'Private capture qualification',
        'Kind': 1, 'WebUrl': f'http://127.0.0.1:{server.server_port}/'}, 'anchor': [1100, 770],
        'theme': {'window': '#eff0f1', 'text': '#232629', 'button': '#eff0f1',
            'highlight': '#3daee9', 'font': 'Noto Sans', 'fontSize': 10}})
    print('READY: click the page button, then select a source in the private KDE picker.', flush=True)
    result = reports.get(timeout=120)
    print(result, flush=True)
    report={key:values[0] for key,values in parse_qs(urlsplit(result).query).items()}
    assert report.get('active') == 'yes', report
    assert int(report.get('frames', 0)) >= 2, report
    assert int(report.get('pixelRange', 0)) > 30, report
    assert int(report.get('width', 0)) > 0 and int(report.get('height', 0)) > 0, report
    send({'type': 'quit'})
    while messages.get(timeout=10).get('type') != 'quit-blocked':
        pass
    (root / 'result.json').write_text(json.dumps({'passed': True, 'receivedVideo': report,
        'unconfirmedQuitBlocked': True}, indent=2))
    print('PASS: two real portal video frames received; capture is protected against unconfirmed quit.', flush=True)
finally:
    if process.poll() is None:
        send({'type': 'quit', 'confirmed': True})
        process.wait(timeout=15)
    (root / 'protocol.json').write_text(json.dumps(records, indent=2))
    server.shutdown()
