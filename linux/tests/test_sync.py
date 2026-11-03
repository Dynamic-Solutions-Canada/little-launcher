"""Sync data preservation, conflict/auth boundaries and device-local resource policy."""
import base64
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from urllib.request import Request

from little_launcher.document import DocumentError
from little_launcher.onedrive import (AUTHORITY, ITEM, CloudHTTPError, OneDrive, SafeRedirect,
                                      SyncError, callback_parameters)
from little_launcher.portable_icons import MAX_ICON_BYTES, icon_bytes, pack_icons, unpack_icons

PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==')


class MemorySecrets:
    def __init__(self, value=None):
        self.value = value
    def load(self):
        return deepcopy(self.value)
    def save(self, value):
        self.value = deepcopy(value)
    def clear(self):
        self.value = None


class SyncBoundaryTests(unittest.TestCase):
    def test_icon_travels_between_distinct_machine_paths_and_back(self):
        with tempfile.TemporaryDirectory() as temp:
            first, second = Path(temp) / 'first', Path(temp) / 'second'
            first.mkdir()
            image = first / 'chosen.png'
            image.write_bytes(PNG)
            document = {'Launchers': [{'Id': 'same', 'TrayIconMode': 'Custom', 'CustomTrayIconPath': str(image), 'Future': 8}]}
            wire = pack_icons(document)
            self.assertNotIn('CustomTrayIconData', document['Launchers'][0])
            image.unlink()  # The original machine's file is unavailable on the receiving device.
            received = unpack_icons(wire, second)
            self.assertEqual(Path(received['Launchers'][0]['CustomTrayIconPath']).read_bytes(), PNG)
            self.assertEqual(received['Launchers'][0]['Future'], 8)
            returned = unpack_icons(pack_icons(received), first)
            self.assertEqual(returned['Launchers'][0]['CustomTrayIconData'], wire['Launchers'][0]['CustomTrayIconData'])
            self.assertEqual(Path(returned['Launchers'][0]['CustomTrayIconPath']).read_bytes(), PNG)
            self.assertNotEqual(returned['Launchers'][0]['CustomTrayIconPath'], received['Launchers'][0]['CustomTrayIconPath'])

    def test_bad_icon_cannot_write_a_remote_path(self):
        with tempfile.TemporaryDirectory() as temp:
            outside = Path(temp) / 'never-write.png'
            document = {'Launchers': [{'Id': 'x', 'TrayIconMode': 'Custom', 'CustomTrayIconPath': str(outside), 'CustomTrayIconData': 'bad'}]}
            with self.assertRaises(DocumentError):
                unpack_icons(document, Path(temp) / 'data')
            self.assertFalse(outside.exists())
            for value in ('', '%%%%', base64.b64encode(b'<svg/>').decode(), 'A' * (2 * MAX_ICON_BYTES)):
                with self.assertRaises(DocumentError):
                    icon_bytes(value)

    def test_only_matching_unambiguous_oauth_callback_completes(self):
        for target in ('/?code=secret', '/favicon.ico?state=s&code=c', '/?state=wrong&code=c',
                       '/?state=s&state=s&code=c', '/?state=s&code=c&code=d', '/?state=s&code=c&error=e', '/?state=s&code='):
            self.assertIsNone(callback_parameters(target, 's'))
        self.assertEqual(callback_parameters('/?state=s&code=c', 's'), ('code', 'c'))
        self.assertEqual(callback_parameters('/?state=s&error=access_denied', 's'), ('error', 'access_denied'))

    def test_redirect_strips_bearer_token_on_cross_origin_download(self):
        request = Request(ITEM + ':/content', headers={'Authorization': 'Bearer PRIVATE'})
        result = SafeRedirect().redirect_request(request, None, 302, 'Found', {}, 'https://storage.example/download')
        self.assertFalse(result.has_header('Authorization'))
        with self.assertRaises(SyncError):
            SafeRedirect().redirect_request(request, None, 302, 'Found', {}, 'http://storage.example/download')

    def test_refresh_retains_refresh_token_and_granted_scope(self):
        secrets = MemorySecrets({'access_token': 'old', 'refresh_token': 'retained', 'scope': 'wide', 'expires_at': 0, 'account_name': 'Existing account'})
        calls = []
        def request(method, url, **kwargs):
            calls.append((method, url, kwargs))
            return json.dumps({'access_token': 'new', 'expires_in': 3600}).encode(), {}
        service = OneDrive(secrets, request)
        self.assertEqual(service.access_token(), 'new')
        self.assertEqual(secrets.value['refresh_token'], 'retained')
        self.assertEqual(secrets.value['scope'], 'wide')
        self.assertEqual(secrets.value['account_name'], 'Existing account')
        self.assertIn(b'scope=wide', calls[0][2]['body'])

    def test_revoked_refresh_is_cleared_and_error_is_sanitized(self):
        secrets = MemorySecrets({'access_token': 'old', 'refresh_token': 'PRIVATE', 'expires_at': 0})
        def request(*args, **kwargs):
            raise CloudHTTPError(400, 'invalid_grant')
        with self.assertRaises(CloudHTTPError) as error:
            OneDrive(secrets, request).access_token()
        self.assertIsNone(secrets.value)
        self.assertNotIn('PRIVATE', str(error.exception))

    def test_inconsistent_download_is_not_applied(self):
        secrets = MemorySecrets({'access_token': 'valid', 'expires_at': time.time() + 3600})
        replies = [{'eTag': 'first'}, {'Launchers': []}, {'eTag': 'changed'}]
        def request(*args, **kwargs):
            return json.dumps(replies.pop(0)).encode(), {}
        with self.assertRaisesRegex(SyncError, 'changed during download'):
            OneDrive(secrets, request).download()

    def test_upload_uses_existing_item_replace_and_exact_revision_without_leaking_bearer(self):
        secrets = MemorySecrets({'access_token': 'valid', 'expires_at': time.time() + 3600})
        calls = []
        def request(method, url, **kwargs):
            calls.append((method, url, kwargs))
            if method=='GET':return b'{"id":"file!fixture","eTag":"expected"}',{}
            if method == 'POST':
                if json.loads(kwargs['body'])['item']['@microsoft.graph.conflictBehavior']=='fail':raise CloudHTTPError(409)
                return b'{"uploadUrl":"https://storage.example/session"}', {}
            return b'{"eTag":"new"}', {}
        revision = OneDrive(secrets, request).upload({'Launchers': []}, 'expected')
        self.assertEqual(revision, 'new')
        self.assertTrue(calls[1][1].endswith('/items/file%21fixture/createUploadSession'))
        self.assertEqual(calls[1][2]['headers']['If-Match'], 'expected')
        self.assertEqual(calls[1][2]['token'], 'valid')
        self.assertNotIn('token', calls[2][2])
        self.assertIn('Content-Range', calls[2][2]['headers'])

    def test_upload_metadata_change_rejects_before_creating_or_sending_a_session(self):
        calls=[]
        def request(method,url,**kwargs):
            calls.append(method);return b'{"id":"fixture","eTag":"changed"}',{}
        with self.assertRaises(CloudHTTPError)as error:
            OneDrive(MemorySecrets({'access_token':'valid','expires_at':time.time()+3600}),request).upload({'Launchers':[]},'reviewed')
        self.assertEqual(error.exception.status,412);self.assertEqual(calls,['GET'])

    def test_failed_session_is_cancelled_and_never_retried_as_an_unconditional_write(self):
        calls=[]
        def request(method,url,**kwargs):
            calls.append(method)
            if method=='GET':return b'{"id":"fixture","eTag":"expected"}',{}
            if method=='POST':return b'{"uploadUrl":"https://storage.example/session"}',{}
            if method=='PUT':raise CloudHTTPError(404)
            return b'',{}
        with self.assertRaises(CloudHTTPError):
            OneDrive(MemorySecrets({'access_token':'valid','expires_at':time.time()+3600}),request).upload({'Launchers':[]},'expected')
        self.assertEqual(calls,['GET','POST','PUT','DELETE'])

    def test_cancelled_sign_in_preserves_existing_credentials(self):
        secrets = MemorySecrets({'access_token': 'kept'})
        cancel = threading.Event()
        cancel.set()
        with self.assertRaisesRegex(SyncError, 'cancelled'):
            OneDrive(secrets).sign_in(lambda _: None, cancel)
        self.assertEqual(secrets.value['access_token'], 'kept')


try:
    from PySide6.QtWidgets import QApplication
    from little_launcher.controller import LauncherController
    from little_launcher.sync import LauncherSync, apply_remote, fingerprint
    QT = True
except ImportError:
    QT = False


@unittest.skipUnless(QT, 'Native sync state checks need Qt')
class NativeSyncTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.controller = LauncherController(self.directory)
        self.controller.edit(lambda doc: doc['Launchers'].append({'Id': 'local', 'Name': 'Local', 'Platform':'linux', 'Future': 4}))
        self.messages = []

    def wait(self, service):
        deadline = time.monotonic() + 5
        while service.job and time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(.002)
        self.assertIsNone(service.job)

    def test_download_old_windows_file_is_backed_up_and_cannot_upload(self):
        class Transport:
            def download(self):
                return {'Launchers': [{'Id': 'cloud', 'Items': [{'Path': 'C:\\tool.exe', 'FutureItem': 9}]}]}, 'etag'
            def upload(self, *_):
                raise AssertionError('An older unqualified file must not be uploaded')
        sync = LauncherSync(self.controller, Transport())
        sync.status.connect(self.messages.append)
        sync.download()
        self.wait(sync)
        self.assertEqual(self.controller.launchers[0]['Id'], 'cloud')
        backups = list((self.directory / 'backups').glob('*.json'))
        self.assertEqual(json.loads(backups[0].read_text())['Launchers'][0]['Id'], 'local')
        self.assertFalse(sync.state['Compatible'])
        self.assertFalse(sync.set_automatic(True))
        sync.upload()
        self.assertIn('updated Windows', self.messages[-1])

    def test_automatic_prerequisites_and_preference_failure_are_visible(self):
        from unittest.mock import patch
        sync = LauncherSync(self.controller, object())
        sync.status.connect(self.messages.append)
        self.assertIn('Sign in', sync.automatic_unavailable_reason())
        sync.signed_in = True
        sync.save_state(Enabled=False)
        self.assertIn('Turn on', sync.automatic_unavailable_reason())
        sync.save_state(Enabled=True)
        self.assertIn('Download', sync.automatic_unavailable_reason())
        sync.save_state(Baseline='known', ETag='known', Compatible=False)
        self.assertIn('Windows', sync.automatic_unavailable_reason())
        self.assertFalse(sync.set_automatic(True))
        sync.save_state(Compatible=True)
        self.assertEqual(sync.automatic_unavailable_reason(), '')
        with patch('little_launcher.sync.QTimer.singleShot'):
            self.assertTrue(sync.set_automatic(True))
        with patch.object(self.controller.preferences, 'set', side_effect=OSError('fixture')):
            self.assertFalse(sync.set_automatic(False))
        self.assertTrue(sync.state['Automatic'])
        self.assertIn('previous choice was kept', self.messages[-1])
        sync.timer.stop();sync.debounce.stop()

    def test_cloud_page_shows_compatibility_reason_next_to_disabled_toggle(self):
        from little_launcher.sync_page import CloudSyncPage
        sync = LauncherSync(self.controller, object())
        sync.signed_in = True
        sync.save_state(Enabled=True, Baseline='known', ETag='known', Compatible=False)
        page = CloudSyncPage(sync, lambda:None, lambda:None, lambda:None, lambda:None)
        self.assertFalse(page.automatic.isEnabled())
        self.assertIn('Windows', page.automatic_row.subtitle.text())
        self.assertFalse(page.upload.isEnabled())
        self.assertTrue(page.download.isEnabled())
        sync.save_state(Compatible=True)
        self.assertTrue(page.automatic.isEnabled())
        self.assertTrue(page.upload.isEnabled())
        page.close();sync.timer.stop();sync.debounce.stop()

    def test_legacy_or_malformed_local_writer_proof_cannot_enable_upload(self):
        sync=LauncherSync(self.controller,object());sync.signed_in=True
        sync.state.update(Baseline='known',ETag='known',Compatible=True)
        for version in [None,1,'2',True]:
            sync.state['CompatibilityVersion']=version
            self.assertIn('Windows',sync.automatic_unavailable_reason())
        sync.save_state(Compatible=True)
        self.assertEqual(sync.automatic_unavailable_reason(),'')
        sync.timer.stop();sync.debounce.stop()

    def test_disable_is_local_durable_and_does_not_mark_portable_data_changed(self):
        document, revision = deepcopy(self.controller.document), self.controller.revision
        notifications = []
        self.controller.changed.connect(lambda: notifications.append('portable'))
        self.controller.local_changed.connect(lambda: notifications.append('local'))
        self.assertTrue(self.controller.set_enabled('local', False))
        self.assertFalse(self.controller.enabled('local'))
        self.assertEqual(self.controller.document, document)
        self.assertEqual(self.controller.revision, revision)
        self.assertEqual(notifications, ['local'])
        reopened = LauncherController(self.directory)
        self.assertFalse(reopened.enabled('local'))
        self.assertTrue(reopened.set_enabled('local', True))
        self.assertTrue(reopened.enabled('local'))

    def test_remote_state_cannot_enable_disabled_launcher_or_import_session(self):
        self.controller.set_enabled('local', False)
        self.controller.edit(lambda doc: doc['Launchers'][0].update(WebSessionTabs=['local'], PinAumid='local-pin'))
        remote = {'Launchers': [{'Id': 'local', 'Name': 'Remote', 'FutureRemote': 7,
                                'WebSessionTabs': ['foreign'], 'PinAumid': 'foreign-pin'}]}
        merged = apply_remote(self.controller.document, remote)
        self.assertEqual(merged['Launchers'][0]['WebSessionTabs'], ['local'])
        self.assertEqual(merged['Launchers'][0]['PinAumid'], 'local-pin')
        self.assertEqual(merged['Launchers'][0]['FutureRemote'], 7)
        self.controller.edit(lambda doc: doc.update(merged))
        self.assertFalse(self.controller.enabled('local'))

    def test_conflicting_upload_preserves_both_copies(self):
        class Transport:
            def download(self):
                return {'PlatformVisibilityVersion': 2, 'Launchers': []}, 'new-etag'
            def upload(self, *_):
                raise AssertionError('Conflicting cloud revision must not be overwritten')
        sync = LauncherSync(self.controller, Transport())
        sync.status.connect(self.messages.append)
        sync.save_state(Compatible=True, ETag='old-etag', Baseline='old', Automatic=True)
        sync.upload()
        self.wait(sync)
        self.assertEqual(self.controller.launchers[0]['Id'], 'local')
        self.assertFalse(sync.state['Automatic'])
        self.assertIn('another device', self.messages[-1])

    def test_local_edit_during_download_wins_over_network_result(self):
        started, release = threading.Event(), threading.Event()
        class Transport:
            def download(self):
                started.set()
                release.wait(3)
                return {'PlatformVisibilityVersion': 2, 'Launchers': [{'Id': 'cloud'}]}, 'etag'
        sync = LauncherSync(self.controller, Transport())
        sync.status.connect(self.messages.append)
        sync.download()
        self.assertTrue(started.wait(2))
        self.controller.edit(lambda doc: doc['Launchers'][0].update(Name='Edited during download'))
        release.set()
        self.wait(sync)
        self.assertEqual(self.controller.launchers[0]['Name'], 'Edited during download')
        self.assertIn('changed during download', self.messages[-1])

    def test_automatic_poll_cannot_apply_old_writer_over_clean_local_copy(self):
        class Transport:
            def download(self):
                return {'Launchers': [{'Id': 'old-writer'}]}, 'etag'
        sync = LauncherSync(self.controller, Transport())
        sync.status.connect(self.messages.append)
        sync.save_state(Compatible=True, ETag='previous', Baseline=fingerprint(self.controller.document), Automatic=True)
        sync.automatic()
        self.wait(sync)
        self.assertEqual(self.controller.launchers[0]['Id'], 'local')
        self.assertFalse(sync.state['Automatic'])
        self.assertIn('update the Windows writer', self.messages[-1])

    def test_disabled_browser_is_removed_before_queued_preload_can_start(self):
        from little_launcher.browser import BrowserHost
        host = BrowserHost(self.directory)
        with patch.object(host, '_start'):
            host.configure([{'Id': 'page', 'Kind': 1, 'WebUrl': 'https://example.com', 'WebHiddenPolicy': 2}])
            self.assertEqual(len(host.pending), 1)
            host.configure([], disabled={'page'})
            self.assertFalse(any(message.get('launcher', {}).get('Id') == 'page' for message in host.pending))
            host.ready = True
            host.configure([{'Id': 'page', 'Kind': 1, 'WebUrl': 'https://example.com'}])
            with patch.object(host, 'send') as send:
                host.configure([], disabled={'page'})
                self.assertEqual(send.call_args.args[0]['confirmedIds'], ['page'])

    def test_manual_icon_upload_reviews_latest_revision_and_preserves_exact_new_bytes(self):
        from little_launcher.sync import fingerprint
        class Transport:
            writes=[]
            def download(inner):return deepcopy(remote),'fresh-etag'
            def upload(inner,document,etag):inner.writes.append((deepcopy(document),etag));return 'uploaded-etag'
        baseline=deepcopy(self.controller.document);remote={**baseline,'PlatformVisibilityVersion':2}
        image=self.directory/'selected.png';image.write_bytes(PNG)
        self.controller.edit(lambda doc:doc['Launchers'][0].update(Kind=1,TrayIconMode='Custom',CustomTrayIconPath=str(image)))
        transport=Transport();sync=LauncherSync(self.controller,transport);reviews=[];sync.upload_review_ready.connect(reviews.append)
        sync.save_state(Compatible=True,ETag='old-etag',Baseline=fingerprint(baseline))
        sync.prepare_upload();self.wait(sync)
        self.assertEqual(len(reviews),1);self.assertFalse(reviews[0]['conflict']);self.assertEqual(reviews[0]['iconChanges'],1)
        self.assertFalse(transport.writes);self.assertIsNone(sync.job,'Review is emitted only after preparation ends')
        sync.confirm_upload(reviews[0]['token']);self.wait(sync)
        wire,etag=transport.writes[0];self.assertEqual(etag,'fresh-etag')
        self.assertEqual(base64.b64decode(wire['Launchers'][0]['CustomTrayIconData']),PNG)
        backup=next((self.directory/'backups').glob('before-onedrive-upload-*'));self.assertEqual(json.loads(backup.read_text()),remote)
        sync.confirm_upload(reviews[0]['token']);self.assertEqual(len(transport.writes),1)

    def test_review_cancel_and_both_local_and_cloud_races_never_overwrite(self):
        from little_launcher.sync import fingerprint
        class Transport:
            etag='cloud-1';writes=[]
            def download(inner):return {'PlatformVisibilityVersion':2,'Launchers':[{'Id':'local','Name':'Cloud'}]},inner.etag
            def upload(inner,*args):inner.writes.append(args);return 'next'
        transport=Transport();sync=LauncherSync(self.controller,transport);reviews=[];errors=[]
        sync.upload_review_ready.connect(reviews.append);sync.manual_failed.connect(errors.append)
        sync.save_state(Compatible=True,ETag='old',Baseline='unknown-old-content')
        original=deepcopy(self.controller.document)
        sync.prepare_upload();self.wait(sync);self.assertTrue(reviews[-1]['conflict'])
        sync.cancel_upload_review(reviews[-1]['token']);self.assertIsNone(sync._upload_review);self.assertFalse(transport.writes)
        sync.prepare_upload();self.wait(sync);token=reviews[-1]['token'];transport.etag='cloud-2'
        sync.confirm_upload(token);self.wait(sync);self.assertFalse(transport.writes);self.assertIn('changed again',errors[-1])
        self.assertEqual(original,self.controller.document)
        sync.prepare_upload();self.wait(sync);token=reviews[-1]['token']
        self.controller.edit(lambda doc:doc['Launchers'][0].update(Name='New local edit'))
        sync.confirm_upload(token);self.assertFalse(transport.writes);self.assertIn('local copy changed',errors[-1])

    def test_revision_only_cloud_changes_do_not_block_latest_local_update(self):
        from little_launcher.sync import fingerprint
        before=deepcopy(self.controller.document)
        class Transport:
            writes=[]
            def download(inner):return {**deepcopy(before),'PlatformVisibilityVersion':2},'new-revision-same-content'
            def upload(inner,document,etag):inner.writes.append((document,etag));return 'uploaded'
        transport=Transport();sync=LauncherSync(self.controller,transport)
        sync.save_state(Compatible=True,ETag='old-revision',Baseline=fingerprint(before))
        self.controller.edit(lambda doc:doc['Launchers'][0].update(Name='Latest local change'))
        sync.upload();self.wait(sync)
        self.assertEqual(transport.writes[0][1],'new-revision-same-content');self.assertEqual(transport.writes[0][0]['Launchers'][0]['Name'],'Latest local change')

    def test_latest_cloud_copy_is_offered_when_local_has_not_changed(self):
        from little_launcher.sync import fingerprint
        class Transport:
            def download(inner):return {'PlatformVisibilityVersion':2,'Launchers':[{'Id':'local','Name':'Latest cloud change'}]},'new-revision'
            def upload(inner,*args):raise AssertionError('An unchanged local copy must not overwrite newer cloud changes')
        sync=LauncherSync(self.controller,Transport());reviews=[];sync.upload_review_ready.connect(reviews.append)
        sync.save_state(Compatible=True,ETag='old',Baseline=fingerprint(self.controller.document))
        sync.prepare_upload();self.wait(sync);self.assertEqual(reviews[0]['action'],'download');self.assertFalse(reviews[0]['conflict'])
        sync.confirm_upload(reviews[0]['token']);self.wait(sync)
        self.assertEqual(self.controller.launchers[0]['Name'],'Latest cloud change')

    def test_same_content_revisions_update_baseline_without_rewriting_the_cloud(self):
        class Transport:
            def download(inner):return {**deepcopy(self.controller.document),'PlatformVisibilityVersion':2},'new-etag'
            def upload(inner,*args):raise AssertionError('Identical launcher data should not be reuploaded')
        sync=LauncherSync(self.controller,Transport());reviews=[];sync.upload_review_ready.connect(reviews.append)
        sync.save_state(Compatible=True,ETag='old',Baseline='previous')
        sync.prepare_upload();self.wait(sync);self.assertFalse(reviews);self.assertEqual(sync.state['ETag'],'new-etag')

    def test_destination_collapse_does_not_disable_sync_or_hide_header_state(self):
        from little_launcher.sync_page import CloudSyncPage,Destination
        sync=LauncherSync(self.controller,object());sync.signed_in=True;sync.account_name='Synthetic account';sync.save_state(Enabled=True)
        page=CloudSyncPage(sync,lambda:None,lambda:None,lambda:None,lambda:None)
        for destination in page.findChildren(Destination):self.assertFalse(destination.expand.isChecked());self.assertTrue(destination.body.isHidden())
        self.assertTrue(page.onedrive.enabled.isChecked());self.assertEqual(page.onedrive.status.text(),'Synthetic account')
        page.onedrive.expand.click();self.assertFalse(page.onedrive.body.isHidden());page.onedrive.expand.click();self.assertTrue(sync.state['Enabled'])
        page.close()
