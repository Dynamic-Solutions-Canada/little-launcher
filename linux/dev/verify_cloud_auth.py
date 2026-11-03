"""Synthetic loopback OAuth qualification in the private fixture; no vendor or wallet calls."""
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import urlopen

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime', 'Use only the private fixture'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from little_launcher.onedrive import AUTHORITY, OneDrive

class SyntheticStore:
    value = None
    def save(self, value):
        self.value = value

store = SyntheticStore()
records, browser_threads, failures = {}, [], []

def browser(url):
    values = parse_qs(urlsplit(url).query)
    records['challenge'] = values['code_challenge'][0]
    redirect = values['redirect_uri'][0]
    state = values['state'][0]
    def callback():
        try:
            for fields in ({'state': 'unrelated', 'code': 'ignored'},
                           [('state', state), ('code', 'one'), ('code', 'two')]):
                try:
                    urlopen(redirect + '?' + urlencode(fields), timeout=5)
                    raise AssertionError('An unrelated callback was accepted')
                except HTTPError as error:
                    assert error.code == 400
            with urlopen(redirect + '?' + urlencode({'state': state, 'code': 'synthetic-code'}), timeout=5) as response:
                assert response.status == 200
                assert b'Return to Little Launcher to confirm sign-in' in response.read()
            records['unrelatedRejected'] = True
        except Exception as error:
            failures.append(type(error).__name__)
    worker = threading.Thread(target=callback)
    worker.start()
    browser_threads.append(worker)

def token(method, url, **kwargs):
    if method == 'GET': return b'{"owner":{"user":{"displayName":"Synthetic account"}}}', {}
    assert method == 'POST' and url == AUTHORITY + 'token'
    values = parse_qs(kwargs['body'].decode())
    assert values['code'] == ['synthetic-code']
    calculated = base64.urlsafe_b64encode(hashlib.sha256(values['code_verifier'][0].encode()).digest()).rstrip(b'=').decode()
    assert calculated == records['challenge']
    records['pkceVerified'] = True
    return b'{"access_token":"synthetic-access","refresh_token":"synthetic-refresh","expires_in":3600}', {}

message = OneDrive(store, token).sign_in(browser, threading.Event())
for worker in browser_threads:
    worker.join(timeout=5)
assert not failures, failures
assert store.value and store.value['refresh_token'] == 'synthetic-refresh'
assert 'Signed in' in message
print(json.dumps({'passed': True, 'loopbackRejectsUnrelatedAndDuplicateCallbacks': records['unrelatedRejected'],
                  'pkceVerified': records['pkceVerified'], 'tokensSavedAfterExchange': True,
                  'realAccountOrWalletUsed': False}, indent=2))
