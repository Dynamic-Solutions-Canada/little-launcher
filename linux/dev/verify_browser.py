"""Exercise real Electron isolation, persistent partitions, permission denial, and sleep.

Run only in the private development desktop. The HTTP server and cookies are disposable
fixtures; no host browser data, devices, display, or account is involved.
"""
import argparse
import http.server
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import tempfile
import time
import sys
from urllib.parse import parse_qs, urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.document import shows_bookmark_bar, web_address

parser = argparse.ArgumentParser()
parser.add_argument('--engine', default='/opt/electron/electron')
parser.add_argument('--application', default='/source/linux/browser')
options = parser.parse_args()

observations = queue.Queue()
site_events = []


class Fixture(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        url = urlsplit(self.path)
        label = parse_qs(url.query).get('label', [''])[0]
        if url.path == '/redirect':
            self.send_response(302)
            self.send_header('Location', '/page?label=navigation-end')
            self.end_headers()
            return
        self.send_response(200)
        self.send_header('Content-Type', 'text/html')
        if url.path == '/page':
            observations.put({'type': 'document', 'label': label, 'cookie': self.headers.get('Cookie')})
            self.send_header('Set-Cookie', f'fixture={label}; Path=/; Max-Age=3600; SameSite=Lax')
            self.end_headers()
            script = '''<h1>Local browser qualification</h1><script>
              fetch('/report?label=LABEL&node=' + typeof require + '&bridge=' + typeof window.launcher + '&visibility=' + document.visibilityState);
              navigator.geolocation.getCurrentPosition(() => fetch('/geo?label=LABEL&allowed=yes'),
                () => fetch('/geo?label=LABEL&allowed=no'));
            </script>'''.replace('LABEL', label)
            if label == 'navigation-start':
                script += '<script>setTimeout(() => { location.href="/redirect"; }, 200);</script>'
            if label == 'media-test':
                script += '''<script>navigator.mediaDevices.getUserMedia({audio:true}).then(stream => {
                  fetch('/capture?label=media-test&allowed=yes');
                  setTimeout(() => stream.getTracks().forEach(track => track.stop()), 100);
                }).catch(() => fetch('/capture?label=media-test&allowed=no'));</script>'''
            if label == 'keep-running':
                script += '<script>setTimeout(() => fetch("/timer?label=keep-running&active=yes"), 250);</script>'
            self.wfile.write(script.encode())
        else:
            self.end_headers()
            if url.path in ('/report', '/geo', '/capture', '/timer'):
                observations.put({'type': url.path[1:], **{key: value[0] for key, value in parse_qs(url.query).items()}})
            self.wfile.write(b'ok')


server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Fixture)
threading.Thread(target=server.serve_forever, daemon=True).start()
root = Path(tempfile.mkdtemp(prefix='browser-qualification-', dir='/work'))
theme = {'window': '#eff0f1', 'text': '#232629', 'base': '#ffffff', 'button': '#eff0f1',
         'highlight': '#3daee9', 'font': 'Noto Sans', 'fontSize': 10}
messages = queue.Queue()
records = []


def send(process, message):
    if message.get('type') in ('show', 'configure'):
        message = {**message, 'presentation': {'homeUrl': web_address(message['launcher']),
                   'showBookmarkBar': shows_bookmark_bar(message['launcher'])}}
    process.stdin.write(json.dumps(message) + '\n')
    process.stdin.flush()


def start():
    errors = (root / 'engine.stderr').open('a')
    process = subprocess.Popen([options.engine, options.application,
        '--launcher-data=' + str(root / 'data'), '--use-fake-device-for-media-stream'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=errors, text=True)
    def read():
        for line in process.stdout:
            try:
                message = json.loads(line)
                records.append(message)
                if message.get('type') == 'close-launcher':
                    send(process, {'type': 'reply', 'token': message['token'], 'allowed': False})
                if message.get('type') == 'permission':
                    send(process, {'type': 'reply', 'token': message['token'], 'allowed': message.get('permission') == 'media'})
                messages.put(message)
            except ValueError:
                pass
    threading.Thread(target=read, daemon=True).start()
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        message = messages.get(timeout=20)
        if message.get('type') == 'ready':
            return process, message
    raise AssertionError('Browser did not become ready')


def page(process, identifier, label, shared=False, background=False, modern=False):
    config = {'Id': identifier, 'Name': label, 'Kind': 1, 'WebSharedProfile': shared,
              'WebUrl': f'http://127.0.0.1:{server.server_port}/page?label={label}'}
    if modern:
        config['WebHomeUrl'] = config.pop('WebUrl')
        config['WebBookmarks'] = [{'Name': 'Separate bookmark', 'Url': f'http://127.0.0.1:{server.server_port}/page?label=wrong-bookmark'}]
        config['WebShowBookmarkBar'] = False
    if background:
        config['WebHiddenPolicy'] = 2
    send(process, {'type': 'configure' if background else 'show', 'launcher': config, 'theme': theme})
    observed = {}
    required = {'document', 'report'} if background else {'document', 'report', 'geo'}
    deadline = time.monotonic() + 20
    while not required.issubset(observed) and time.monotonic() < deadline:
        event = observations.get(timeout=20)
        site_events.append(event)
        if event.get('label') == label:
            observed[event['type']] = event
    assert required.issubset(observed), observed
    assert observed['report']['node'] == 'undefined', observed
    assert observed['report']['bridge'] == 'undefined', observed
    if not background:
        assert observed['geo']['allowed'] == 'no', observed
    return config, observed


process, version = start()
try:
    keep_config, background = page(process, 'background', 'keep-running', background=True)
    assert background['report']['visibility'] == 'hidden', background
    while True:
        timer = next((event for event in site_events if event.get('type') == 'timer'), None) or observations.get(timeout=10)
        if timer.get('type') == 'timer':
            assert timer['active'] == 'yes'
            break
    # Geolocation's web standard waits for a hidden document to become visible.
    send(process, {'type': 'show', 'launcher': keep_config, 'theme': theme})
    while True:
        permission = observations.get(timeout=10)
        if permission.get('type') == 'geo' and permission.get('label') == 'keep-running':
            assert permission['allowed'] == 'no'
            break
    config, first = page(process, 'private-a', 'private-a', modern=True)
    _, second = page(process, 'private-b', 'private-b')
    _, shared_first = page(process, 'shared-a', 'shared-a', True)
    _, shared_second = page(process, 'shared-b', 'shared-b', True)
    assert first['document']['cookie'] is None, first
    assert second['document']['cookie'] is None, second
    assert shared_first['document']['cookie'] is None, shared_first
    assert shared_second['document']['cookie'] == 'fixture=shared-a', shared_second
    send(process, {'type': 'show', 'launcher': config, 'theme': theme})
    deadline = time.monotonic() + 10
    while True:
        event = messages.get(timeout=10)
        if event.get('id') == 'private-a' and event.get('frozen') is True:
            break
        assert time.monotonic() < deadline
    _, navigation = page(process, 'navigation', 'navigation-start')
    while True:
        event = observations.get(timeout=10)
        if event.get('type') == 'document' and event.get('label') == 'navigation-end':
            break
    retired = {'background', 'private-a', 'private-b', 'shared-a', 'shared-b', 'navigation'}
    send(process, {'type': 'remove', 'ids': sorted(retired)})
    while retired:
        event = messages.get(timeout=10)
        if event.get('type') == 'removed':
            retired.discard(event.get('id'))
    _, media = page(process, 'protected-media', 'media-test')
    while True:
        capture = next((event for event in site_events if event.get('type') == 'capture'), None) or observations.get(timeout=10)
        if capture.get('type') == 'capture':
            assert capture['allowed'] == 'yes', capture
            break
    send(process, {'type': 'remove', 'ids': ['protected-media']})
    while True:
        event = messages.get(timeout=10)
        if event.get('type') == 'detached' and event.get('id') == 'protected-media':
            break
    send(process, {'type': 'quit'})
    while True:
        event = messages.get(timeout=10)
        if event.get('type') == 'quit-blocked':
            break
    assert process.poll() is None
    # Disabling is an already-confirmed user action and must release even protected work.
    before_disable = len(records)
    send(process, {'type': 'remove', 'ids': ['protected-media'], 'confirmedIds': ['protected-media']})
    while True:
        event = messages.get(timeout=10)
        if event.get('type') == 'removed' and event.get('id') == 'protected-media':
            break
    assert not any(event.get('type') == 'close-launcher' for event in records[before_disable:])
    send(process, {'type': 'quit'})
    assert process.wait(timeout=15) == 0
    process, _ = start()
    _, restored = page(process, 'private-a', 'private-restored')
    assert restored['document']['cookie'] == 'fixture=private-a', restored
    result = {'passed': True, 'engine': version, 'checks': [
        'Remote content has no Node or trusted toolbar bridge',
        'Geolocation requires explicit permission and denial reaches the site',
        'Private launchers have separate cookie stores',
        'Shared launchers use the same local cookie store',
        'Hidden ordinary pages actually freeze in Chromium',
        'Private cookies survive a clean engine restart',
        'Keep-running starts hidden and runs background timers',
        'A real Chromium fake-device capture blocks unconfirmed quit',
        'HTTP page navigation and redirects work with the current event API',
        'Declining to close removed active work keeps it reachable in a regular window',
        'The modern home address opens independently of the first saved bookmark',
        'Confirmed device disable destroys the protected browser and allows ordinary quit',
    ]}
    (root / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))
finally:
    if process.poll() is None:
        send(process, {'type': 'quit', 'confirmed': True})
        process.wait(timeout=15)
    (root / 'protocol.json').write_text(json.dumps(records, indent=2) + '\n')
    (root / 'site-events.json').write_text(json.dumps(site_events, indent=2) + '\n')
    server.shutdown()
