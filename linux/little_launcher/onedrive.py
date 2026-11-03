"""OneDrive app-folder transport, PKCE and desktop credential storage (no Qt dependency)."""
import base64
import hashlib
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit, quote
from urllib.request import Request, HTTPRedirectHandler, build_opener

from .document import DocumentError

CLIENT_ID = 'eca1f6fc-ab2b-4dc6-8e93-86c74f846a26'
AUTHORITY = 'https://login.microsoftonline.com/consumers/oauth2/v2.0/'
SCOPE = 'Files.ReadWrite.AppFolder offline_access'
ITEM = 'https://graph.microsoft.com/v1.0/me/drive/special/approot:/launchers.json'
MAX_DOCUMENT = 32 * 1024 * 1024


class SyncError(DocumentError):
    pass


class SignedOutError(SyncError):
    pass


class CloudHTTPError(SyncError):
    def __init__(self, status, oauth_error=''):
        self.status = status
        self.oauth_error = oauth_error if oauth_error in ('invalid_grant', 'invalid_client', 'access_denied', 'interaction_required') else ''
        label = {401: 'Sign in to OneDrive again.', 403: 'OneDrive denied access to the app folder.',
                 409: 'OneDrive reported an upload conflict. Click Upload again to review the latest copy.',
                 412: 'The cloud copy changed after review. Click Upload again to review it.',
                 429: 'OneDrive is busy. Try again later.'}.get(status, 'OneDrive request failed.')
        super().__init__(f'{label} (HTTP {status}' + (f', {self.oauth_error}' if self.oauth_error else '') + ')')


class SafeRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        if request.get_method() != 'GET' or urlsplit(newurl).scheme != 'https':
            raise SyncError('OneDrive returned an unexpected redirect.')
        redirected = super().redirect_request(request, fp, code, msg, headers, newurl)
        if urlsplit(request.full_url).netloc != urlsplit(newurl).netloc:
            redirected.remove_header('Authorization')
        return redirected


def http(method, url, *, token=None, body=None, headers=None, limit=MAX_DOCUMENT):
    fields = dict(headers or {})
    if token:
        fields['Authorization'] = 'Bearer ' + token
    request = Request(url, data=body, headers=fields, method=method)
    try:
        with build_opener(SafeRedirect()).open(request, timeout=30) as response:
            raw = response.read(limit + 1)
            if len(raw) > limit:
                raise SyncError('The cloud response is too large; no settings were changed.')
            return raw, dict(response.headers)
    except HTTPError as error:
        # Never expose raw OAuth responses, callback addresses, tokens or provider error text.
        name = ''
        if url == AUTHORITY + 'token':
            try:
                name = json.loads(error.read(64 * 1024)).get('error', '')
            except (ValueError, UnicodeError):
                pass
        raise CloudHTTPError(error.code, name) from None
    except (URLError, TimeoutError, OSError):
        raise SyncError('Could not reach OneDrive. Check your connection and try again.') from None


class SecretStore:
    """Use Secret Service (KWallet/GNOME Keyring), with no plaintext fallback."""
    def __init__(self, directory):
        self.namespace = hashlib.sha256(str(Path(directory).resolve()).encode()).hexdigest()

    def _run(self, verb, value=None):
        executable=shutil.which('secret-tool')
        if not executable:
            bundled=Path(os.environ.get('APPDIR','/nonexistent'))/'usr/bin/secret-tool'
            executable=str(bundled) if bundled.is_file() and os.access(bundled,os.X_OK) else None
        if not executable:
            raise SyncError('The desktop credential utility is unavailable. Use the complete AppImage build.')
        args = [executable, verb]
        if verb == 'store':
            args += ['--label=Little Launcher OneDrive']
        args += ['application', 'io.github.RyanEwen.LittleLauncher', 'profile', self.namespace, 'provider', 'onedrive']
        try:
            result = subprocess.run(args, input=value, capture_output=True, text=True, timeout=60)
        except (OSError, subprocess.TimeoutExpired):
            raise SyncError('The desktop credential service did not respond. Unlock your wallet and try again.') from None
        if result.returncode:
            if verb in ('lookup', 'clear') and result.returncode == 1 and not result.stderr.strip():
                return ''
            raise SyncError('The desktop credential service is unavailable or locked. Unlock your wallet and try again.')
        return result.stdout.strip()

    def load(self):
        raw = self._run('lookup')
        if not raw:
            return None
        try:
            result = json.loads(raw)
            if not isinstance(result, dict) or not isinstance(result.get('access_token'), str):
                raise ValueError()
            return result
        except ValueError:
            raise SyncError('The saved OneDrive sign-in is unreadable. Sign in again.') from None

    def save(self, tokens):
        self._run('store', json.dumps(tokens))

    def clear(self):
        self._run('clear')


def callback_parameters(target, state):
    """Unrelated or ambiguous loopback requests never consume the authorization attempt."""
    parsed = urlsplit(target)
    values = parse_qs(parsed.query, keep_blank_values=True)
    if parsed.path != '/' or values.get('state') != [state]:
        return None
    code, error = values.get('code'), values.get('error')
    if code and len(code) == 1 and code[0] and error is None:
        return ('code', code[0])
    if error and len(error) == 1 and error[0] and code is None:
        return ('error', error[0])
    return None


class OneDrive:
    def __init__(self, secrets_store, request=http):
        self.secrets = secrets_store
        self.request = request
        self.client_id = os.environ.get('LITTLELAUNCHER_ONEDRIVE_CLIENT_ID', CLIENT_ID)

    def _token(self, parameters, previous=None):
        raw, _ = self.request('POST', AUTHORITY + 'token', body=urlencode({
            'client_id': self.client_id, **parameters}).encode(),
            headers={'Content-Type': 'application/x-www-form-urlencoded'}, limit=1024 * 1024)
        try:
            values = json.loads(raw)
            token = values['access_token']
            if not isinstance(token, str) or not token:
                raise ValueError()
            expires = int(values.get('expires_in', 3600))
        except (ValueError, KeyError, TypeError):
            raise SyncError('OneDrive returned an invalid sign-in response.') from None
        return {'access_token': token, 'refresh_token': values.get('refresh_token') or (previous or {}).get('refresh_token', ''),
                'scope': values.get('scope') or (previous or {}).get('scope', SCOPE),
                'expires_at': time.time() + max(0, expires),
                'account_name': (previous or {}).get('account_name', '')}

    def account_state(self):
        tokens = self.secrets.load()
        return {'signedIn': tokens is not None, 'name': tokens.get('account_name', '') if tokens else ''}

    def test_connection(self):
        self.request('GET', 'https://graph.microsoft.com/v1.0/me/drive/special/approot?$select=id', token=self.access_token())
        return 'OneDrive connection succeeded.'

    def sign_in(self, open_browser, cancelled):
        if cancelled.is_set():
            raise SyncError('Sign-in cancelled. The existing account was kept.')
        state = secrets.token_urlsafe(32)
        verifier = secrets.token_urlsafe(48)
        challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
        answer = []
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def setup(self):
                super().setup()
                self.connection.settimeout(2)

            def do_GET(self):
                result = callback_parameters(self.path, state) if len(self.path) <= 16384 else None
                status, text = 400, 'This request does not match the sign-in attempt.'
                if result:
                    answer.append(result)
                    status, text = 200, 'Authorization received. Return to Little Launcher to confirm sign-in.'
                raw = text.encode()
                self.send_response(status)
                self.send_header('Content-Type', 'text/plain; charset=utf-8')
                self.send_header('Cache-Control', 'no-store')
                self.send_header('Content-Length', str(len(raw)))
                self.send_header('Connection', 'close')
                self.end_headers()
                self.wfile.write(raw)
        with HTTPServer(('127.0.0.1', 0), Handler) as server:
            server.timeout = 0.5
            redirect = f'http://localhost:{server.server_port}/'
            url = AUTHORITY + 'authorize?' + urlencode({'client_id': self.client_id, 'response_type': 'code',
                'redirect_uri': redirect, 'response_mode': 'query', 'scope': SCOPE, 'state': state,
                'code_challenge': challenge, 'code_challenge_method': 'S256', 'prompt': 'select_account'})
            open_browser(url)
            deadline = time.monotonic() + 300
            while not answer and not cancelled.is_set() and time.monotonic() < deadline:
                server.handle_request()
        if cancelled.is_set():
            raise SyncError('Sign-in cancelled. The existing account was kept.')
        if not answer:
            raise SyncError('Sign-in timed out. Try again.')
        if answer[0][0] != 'code':
            raise SyncError('OneDrive sign-in was declined. The existing account was kept.')
        tokens = self._token({'grant_type': 'authorization_code', 'code': answer[0][1],
                              'redirect_uri': redirect, 'code_verifier': verifier, 'scope': SCOPE})
        if cancelled.is_set():
            raise SyncError('Sign-in cancelled. The existing account was kept.')
        try:
            raw, _ = self.request('GET', 'https://graph.microsoft.com/v1.0/me/drive?$select=owner', token=tokens['access_token'])
            name = json.loads(raw).get('owner', {}).get('user', {}).get('displayName', '')
            if isinstance(name, str): tokens['account_name'] = name[:256]
        except (SyncError, ValueError, KeyError, TypeError):
            pass
        if cancelled.is_set():
            raise SyncError('Sign-in cancelled. The existing account was kept.')
        self.secrets.save(tokens)
        return 'Signed in to OneDrive on this device.'

    def access_token(self):
        tokens = self.secrets.load()
        if not tokens:
            raise SignedOutError('Sign in to OneDrive first.')
        if tokens.get('expires_at', 0) > time.time() + 120:
            return tokens['access_token']
        if not tokens.get('refresh_token'):
            raise SignedOutError('Sign in to OneDrive again.')
        try:
            renewed = self._token({'grant_type': 'refresh_token', 'refresh_token': tokens['refresh_token'],
                                   'scope': tokens.get('scope') or SCOPE}, tokens)
        except CloudHTTPError as error:
            if error.oauth_error == 'invalid_grant':
                self.secrets.clear()
            raise
        self.secrets.save(renewed)
        return renewed['access_token']

    def download(self):
        token = self.access_token()
        try:
            raw, _ = self.request('GET', ITEM + '?$select=eTag,lastModifiedDateTime', token=token)
        except CloudHTTPError as error:
            if error.status == 404:
                return None
            raise
        metadata = json.loads(raw)
        etag = metadata.get('eTag')
        if not isinstance(etag, str) or not etag:
            raise SyncError('OneDrive did not return a file revision. Nothing was changed.')
        raw, _ = self.request('GET', ITEM + ':/content', token=token)
        # A content redirect cannot reliably carry If-Match. Verify metadata again afterwards.
        check, _ = self.request('GET', ITEM + '?$select=eTag', token=token)
        if json.loads(check).get('eTag') != etag:
            raise SyncError('The cloud copy changed during download. Try again.')
        try:
            document = json.loads(raw)
        except (ValueError, UnicodeError):
            raise SyncError('The OneDrive launcher file is unreadable. Nothing was changed.') from None
        return document, etag

    def upload(self, document, etag):
        """Replace the exact existing file only while its reviewed revision is current.

        Resolve the app-folder file to its immutable ID before opening the upload
        session. A failed session is cancelled and never retried unconditionally.
        Upload URLs are preauthenticated and must not receive the account token.
        """
        if not isinstance(etag, str) or not etag or etag == '*':
            raise SyncError('Download the existing OneDrive file before uploading.')
        body = json.dumps(document, ensure_ascii=False).encode()
        if len(body) > MAX_DOCUMENT:
            raise SyncError('The launcher document exceeds the 32 MiB sync limit.')
        token = self.access_token()
        raw, _ = self.request('GET', ITEM + '?$select=id,eTag', token=token, limit=65536)
        metadata = json.loads(raw)
        identifier = metadata.get('id')
        if metadata.get('eTag') != etag:
            raise CloudHTTPError(412)
        if not isinstance(identifier, str) or not 0 < len(identifier) <= 512:
            raise SyncError('OneDrive did not identify the existing launcher file. Nothing was uploaded.')
        # Update the exact existing item. conflictBehavior=fail rejects that existing
        # filename with409 even with a current If-Match. The frozen revision still
        # guards session creation and the provider invalidates stale sessions.
        target = 'https://graph.microsoft.com/v1.0/me/drive/items/' + quote(identifier, safe='')
        raw, _ = self.request('POST', target + '/createUploadSession', token=token,
            body=json.dumps({'item': {'@microsoft.graph.conflictBehavior': 'replace'}}).encode(),
            headers={'Content-Type': 'application/json', 'If-Match': etag})
        upload_url = json.loads(raw).get('uploadUrl', '')
        if not isinstance(upload_url, str) or urlsplit(upload_url).scheme != 'https':
            raise SyncError('OneDrive returned an invalid upload session.')
        result = None
        try:
            chunk_size = 16 * 320 * 1024
            for offset in range(0, len(body), chunk_size):
                chunk = body[offset:offset + chunk_size]
                raw, _ = self.request('PUT', upload_url, body=chunk,
                    headers={'Content-Type': 'application/octet-stream',
                             'Content-Range': f'bytes {offset}-{offset + len(chunk) - 1}/{len(body)}'})
                result = json.loads(raw)
            revision = result.get('eTag') if isinstance(result, dict) else None
            if not isinstance(revision, str) or not revision:
                raise SyncError('OneDrive did not confirm the upload. Download before uploading again.')
            return revision
        except (SyncError, ValueError, TypeError):
            try:
                self.request('DELETE', upload_url, limit=1024 * 1024)
            except SyncError:
                pass
            raise
