"""GUI-thread sync ownership with background network/auth and guarded full-document writes."""
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import threading
import time
from uuid import uuid4

from PySide6.QtCore import QObject, QThread, QTimer, Signal, QUrl
from PySide6.QtGui import QDesktopServices

from .document import DocumentError, VISIBILITY_VERSION, require_sync_compatibility, sync_envelope, validate_document
from .onedrive import CloudHTTPError, OneDrive, SecretStore, SignedOutError, SyncError
from .portable_icons import pack_icons, unpack_icons, preserve_icons
from .icons import LauncherIcons
from .storage import atomic_write

# State produced by a particular machine must never arrive from another.
LOCAL_LAUNCHER_FIELDS = ('WebFlyoutPosition', 'WebSessionTabs', 'WebSessionActiveTab', 'PinAumid')


def fingerprint(document):
    portable = deepcopy(document['Launchers'])
    for launcher in portable:
        for key in LOCAL_LAUNCHER_FIELDS:
            launcher.pop(key, None)
        if launcher.get('CustomTrayIconData'):
            launcher.pop('CustomTrayIconPath', None)
    return hashlib.sha256(json.dumps({'Launchers': portable, 'Extensions': document.get('Extensions')},
                                    sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def apply_remote(current, downloaded):
    imported = validate_document(downloaded)
    result = deepcopy(current)
    local = {item['Id']: item for item in current['Launchers']}
    for index,item in enumerate(imported['Launchers']):
        item=preserve_icons(local.get(item['Id'],{}),item);imported['Launchers'][index]=item
        for key in LOCAL_LAUNCHER_FIELDS:
            item.pop(key, None)
            if key in local.get(item['Id'], {}):
                item[key] = deepcopy(local[item['Id']][key])
    result['Launchers'] = imported['Launchers']
    if 'Extensions' in imported:
        result['Extensions'] = deepcopy(imported['Extensions'])
    return result


def sync_decision(local_hash, remote_hash, baseline_hash, revision_changed):
    """Classify whole-document changes against the last successful sync.

    A provider revision can change without the portable content changing. That
    alone is not a conflict. Differing content changed on both sides is.
    """
    if local_hash == remote_hash:
        return 'same'
    remote_changed = revision_changed and remote_hash != baseline_hash
    if not remote_changed:
        return 'local'
    if local_hash == baseline_hash:
        return 'remote'
    return 'conflict'


def upload_changes(local, remote):
    """Summarize a native review without treating device-local paths as edits."""
    left = {item['Id']: item for item in local['Launchers']}
    right = {item['Id']: item for item in remote['Launchers']}

    def portable(item):
        result = deepcopy(item)
        for field in LOCAL_LAUNCHER_FIELDS:
            result.pop(field, None)
        if result.get('CustomTrayIconData'):
            result.pop('CustomTrayIconPath', None)
        return result

    def icon(item):
        fields = ('TrayIconMode', 'CustomTrayIconData', 'TrayIconPreviewMode', 'TrayIconPreviewData')
        return tuple(item.get(field, '') for field in fields)

    changed = [key for key in left.keys() & right.keys() if portable(left[key]) != portable(right[key])]
    icons = [key for key in changed if icon(left[key]) != icon(right[key])]
    return {
        'added': len(left.keys() - right.keys()),
        'removed': len(right.keys() - left.keys()),
        'changed': len(changed),
        'iconChanges': len(icons),
        'iconNames': [str(left[key].get('Name') or 'Unnamed launcher')[:120] for key in sorted(icons)[:10]],
    }


class SyncJob(QThread):
    result = Signal(object)
    failed = Signal(str)
    browse = Signal(str)
    signed_out = Signal()

    def __init__(self, operation, parent):
        super().__init__(parent)
        self.operation = operation
        self.cancelled = threading.Event()
        self.browser_error = ''

    def run(self):
        try:
            self.result.emit(self.operation(self))
        except Exception as error:
            if isinstance(error, SignedOutError) or (isinstance(error, CloudHTTPError) and (error.status == 401 or error.oauth_error == 'invalid_grant')):
                self.signed_out.emit()
            # Auth/network helpers already sanitize errors. Avoid raw URLs and response bodies.
            self.failed.emit(self.browser_error or (str(error) if isinstance(error, DocumentError) else 'Sync could not complete. Local and cloud copies were preserved; try again.'))


class LauncherSync(QObject):
    status = Signal(str)
    busy_changed = Signal(bool)
    account_changed = Signal(bool, str)
    state_changed = Signal()
    upload_review_ready = Signal(object)
    manual_failed = Signal(str)

    def __init__(self, controller, transport=None):
        super().__init__(controller)
        self.controller = controller
        self.transport = transport or OneDrive(SecretStore(controller.directory))
        self.job = None
        self._upload_review = None
        self._review_to_emit = None
        self.cancellable = False
        self.timer = QTimer(self)
        self.timer.setInterval(controller.preferences.data.get('OneDriveSync', {}).get('IntervalMinutes', 5) * 60_000)
        self.timer.timeout.connect(self.automatic)
        self.timer.start()
        self.debounce = QTimer(self)
        self.debounce.setSingleShot(True)
        self.debounce.setInterval(3000)
        self.debounce.timeout.connect(self.automatic)
        controller.changed.connect(self.debounce.start)
        self.state = deepcopy(controller.preferences.data.get('OneDriveSync', {}))
        self.signed_in = bool(self.state.get('SignedIn', self.state.get('Baseline')))
        self.account_name = self.state.get('AccountName', '')
        if self.state.get('Automatic'):
            QTimer.singleShot(0, self.automatic)

    def save_state(self, **changes):
        if 'Compatible' in changes:changes['CompatibilityVersion']=VISIBILITY_VERSION if changes['Compatible'] else 0
        state = {**self.state, **changes}
        self.controller.preferences.set('OneDriveSync', state)
        self.state = state
        self.state_changed.emit()

    def _run(self, operation, complete, cancellable=False, check_account_failure=True, interactive=False):
        if self.job:
            return
        job = SyncJob(operation, self)
        self.job = job
        self.cancellable = cancellable
        self.busy_changed.emit(True)
        job.browse.connect(lambda url: self._open_browser(job, url))
        def receive(value):
            try:
                complete(value)
            except (DocumentError, OSError) as error:
                self._failed(str(error) if isinstance(error,DocumentError) else 'The sync result could not be saved. Your launcher data was preserved; try again.', interactive)
        job.result.connect(receive)
        if check_account_failure:
            job.signed_out.connect(lambda: self._account(False))
        job.failed.connect(lambda message:self._failed(message,interactive))
        job.finished.connect(self._finished)
        job.start()

    def _open_browser(self, job, url):
        if self.job is not job or job.cancelled.is_set():
            return
        if not QDesktopServices.openUrl(QUrl(url)):
            job.browser_error = 'The default browser could not open OneDrive sign-in. Check your default browser and try again.'
            job.cancelled.set()
            self.status.emit(job.browser_error)

    def _failed(self, message, interactive=False):
        # Persistently pause after an error/conflict. No repeated interactive auth/wallet prompts.
        if self.state.get('Automatic'):
            try:
                self.save_state(Automatic=False)
            except OSError:
                self.state['Automatic'] = False
        self.status.emit(message)
        if interactive:
            self.manual_failed.emit(message)

    def _finished(self):
        job, self.job = self.job, None
        job.deleteLater()
        self.busy_changed.emit(False)
        review, self._review_to_emit = self._review_to_emit, None
        if review is not None and self._upload_review is review:
            self.upload_review_ready.emit({key:value for key,value in review.items()if key not in ('envelope','etag','baseline','revision','expires','remoteHash')})

    def cancel(self):
        if self.job:
            self.job.cancelled.set()

    def _account(self, signed_in, name=''):
        self.signed_in, self.account_name = signed_in, name
        self.save_state(SignedIn=signed_in, AccountName=name)
        self.account_changed.emit(signed_in, name)

    def check_account(self):
        if self.job or self._upload_review is not None:
            return
        def complete(state):
            self._account(state['signedIn'], state.get('name', ''))
        self._run(lambda _: self.transport.account_state(), complete)

    def test_connection(self):
        self.status.emit('Testing enabled destinations…')
        self._run(lambda _: self.transport.test_connection(), self.status.emit)

    def set_enabled(self, enabled):
        self.save_state(Enabled=enabled, Automatic=self.state.get('Automatic', False) and enabled)

    def set_interval(self, minutes):
        if type(minutes) is int and 1 <= minutes <= 1440:
            self.save_state(IntervalMinutes=minutes)
            self.timer.setInterval(minutes * 60_000)

    def sign_in(self):
        self.status.emit('Complete OneDrive sign-in in your browser. You can cancel here.')
        def operation(job):
            message = self.transport.sign_in(job.browse.emit, job.cancelled)
            return message, self.transport.account_state()
        def complete(result):
            message, account = result
            self.save_state(Automatic=False, Baseline=None, ETag=None, Compatible=False, Enabled=True)
            self._account(account['signedIn'], account.get('name', ''))
            self.status.emit(message + ' Download your cloud launchers to review them.')
        self._run(operation, complete, cancellable=True, check_account_failure=False)

    def sign_out(self):
        def complete(_):
            self.save_state(Automatic=False, Baseline=None, ETag=None, Compatible=False)
            self._account(False)
            self.status.emit('Signed out on this device. Cloud data and local launchers were kept.')
        self._run(lambda _: self.transport.secrets.clear(), complete)

    def writer_compatible(self):
        version=self.state.get('CompatibilityVersion',0)
        return self.state.get('Compatible') is True and type(version)is int and version>=VISIBILITY_VERSION

    def automatic_unavailable_reason(self):
        if not self.signed_in:
            return 'Sign in to OneDrive to use automatic sync.'
        if not self.state.get('Enabled', True):
            return 'Turn on the OneDrive destination above to use automatic sync.'
        if not self.state.get('Baseline') or not self.state.get('ETag'):
            return 'Download your launchers from OneDrive first, then turn on automatic sync.'
        if not self.writer_compatible():
            return ('Automatic sync needs a newer cloud file. Update Little Launcher on Windows '
                    'to a version with Linux sync support, upload your launchers there, then download '
                    'them here. This prevents older versions from losing Windows- or Linux-only launchers and their icons. '
                    'Manual download is still available.')
        return ''

    def set_automatic(self, enabled):
        reason = self.automatic_unavailable_reason() if enabled else ''
        if reason:
            self.status.emit(reason)
            return False
        try:
            self.save_state(Automatic=enabled)
        except OSError:
            self.status.emit('The automatic sync preference could not be saved. Your previous choice was kept.')
            return False
        if enabled:
            QTimer.singleShot(0, self.automatic)
        return True

    def automatic(self):
        if self.job or self._upload_review is not None or not self.state.get('Automatic') or not self.state.get('Enabled', True):
            return
        try:
            dirty = fingerprint(pack_icons(self.controller.document,LauncherIcons.glyph_png)) != self.state.get('Baseline')
            self.upload() if dirty else self.download(automatic=True)
        except DocumentError as error:
            self._failed(str(error))

    def download(self, automatic=False):
        if self.job:
            return
        revision = self.controller.revision
        self.status.emit('Downloading launchers from OneDrive…')
        def complete(result):
            try:
                if result is None:
                    raise SyncError('No launchers.json was found in the OneDrive app folder. Upload from Windows first.')
                downloaded, etag = result
                validate_document(downloaded)
                compatible = True
                try:
                    require_sync_compatibility(downloaded)
                except DocumentError:
                    compatible = False
                    if automatic:
                        raise SyncError('Automatic sync paused: update the Windows writer. Manual download is still available.')
                if self.controller.revision != revision:
                    raise SyncError('Local launchers changed during download. Your edits were kept; download again when ready.')
                incoming = unpack_icons(downloaded, self.controller.directory)
                candidate = apply_remote(self.controller.document, incoming)
                # Back up the exact full local document before any sync-driven replacement.
                stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
                backup = self.controller.directory / 'backups' / ('before-onedrive-' + stamp + '.json')
                changed = candidate != self.controller.document
                if changed:
                    atomic_write(backup, (json.dumps(self.controller.document, indent=2, ensure_ascii=False) + '\n').encode())
                    if not self.controller.edit(lambda document: document.update(candidate)):
                        raise SyncError('Downloaded launchers were not applied. Local changes were kept.')
                self._account(True, self.account_name)
                baseline = fingerprint(pack_icons(self.controller.document,LauncherIcons.glyph_png))
                self.save_state(Baseline=baseline, ETag=etag, Compatible=compatible,
                                Automatic=self.state.get('Automatic', False) and compatible)
                self.status.emit(f'Downloaded {len(incoming["Launchers"])} launchers. ' +
                    ('Local data was backed up before replacement.' if changed else 'No launcher changes.') +
                    ('' if compatible else ' This older Windows file is available for testing; uploads and automatic sync stay off until Windows is updated.'))
            except (DocumentError, OSError) as error:
                self._failed(str(error))
        self._run(lambda _: self.transport.download(), complete, interactive=not automatic)

    def prepare_upload(self):
        """Prepare a concrete manual replacement without changing either copy."""
        if self.job:
            return
        self._upload_review = None
        if not self.writer_compatible() or not self.state.get('ETag'):
            self._failed('Download a file written by the updated Windows version before uploading.', True)
            return
        try:
            snapshot = pack_icons(self.controller.document, LauncherIcons.glyph_png)
            envelope = sync_envelope(snapshot)
            for launcher in envelope['Launchers']:
                for field in LOCAL_LAUNCHER_FIELDS:
                    launcher.pop(field, None)
            baseline, revision = fingerprint(snapshot), self.controller.revision
        except (DocumentError, OSError):
            self._failed('The launcher icons could not be prepared. Your local and cloud copies were kept.', True)
            return
        self.status.emit('Checking OneDrive before upload…')
        def complete(remote):
            if remote is None:
                raise SyncError('The OneDrive copy is missing. Nothing was uploaded.')
            document, etag = remote
            validate_document(document)
            require_sync_compatibility(document)
            if self.controller.revision != revision:
                raise SyncError('Local launchers changed during preparation. Click Upload again to review the latest copy.')
            decision = sync_decision(baseline, fingerprint(document), self.state.get('Baseline'), etag != self.state.get('ETag'))
            if decision == 'same':
                self.save_state(Baseline=baseline, ETag=etag)
                self.status.emit('Launchers and icons are already up to date in OneDrive.')
                return
            review = {'action':'download' if decision=='remote' else 'upload','conflict':decision=='conflict',
                      'remoteHash':fingerprint(document),'token':uuid4().hex, 'revision':revision, 'baseline':baseline,
                      'envelope':envelope, 'etag':etag, 'expires':time.monotonic()+300,
                      'cloudChanged':decision in ('remote','conflict'),
                      **upload_changes(snapshot, document)}
            self._upload_review = review
            self._review_to_emit = review
        self._run(lambda _: self.transport.download(), complete, interactive=True)

    def cancel_upload_review(self, token):
        if self._upload_review and self._upload_review['token'] == token:
            self._upload_review = None

    def confirm_upload(self, token, *, use_cloud=False):
        """Commit only the exact local content and cloud revision the user reviewed."""
        if self.job:
            return
        review = self._upload_review
        if not review or review['token'] != token:
            return
        self._upload_review = None
        try:
            if (review['expires'] < time.monotonic() or self.controller.revision != review['revision']
                    or fingerprint(pack_icons(self.controller.document,LauncherIcons.glyph_png)) != review['baseline']):
                raise SyncError('The local copy changed or this review expired. Click Upload again; nothing was uploaded.')
        except (DocumentError, OSError) as error:
            self._failed(str(error) if isinstance(error,DocumentError) else 'The icons changed. Click Upload again.', True)
            return
        if use_cloud or review['action'] == 'download':
            self.download()
            return
        self.status.emit('Uploading the reviewed launcher copy to OneDrive…')
        backup = self.controller.directory/'backups'/('before-onedrive-upload-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')+'.json')
        def operation(_):
            remote = self.transport.download()
            if not remote or remote[1] != review['etag'] or fingerprint(remote[0]) != review['remoteHash']:
                raise SyncError('OneDrive changed again after your review. Click Upload to review the new copy; nothing was overwritten.')
            require_sync_compatibility(remote[0])
            atomic_write(backup,(json.dumps(remote[0],indent=2,ensure_ascii=False)+'\n').encode())
            return self.transport.upload(review['envelope'],review['etag'])
        def complete(etag):
            self.save_state(Baseline=review['baseline'],ETag=etag)
            self.status.emit('Launchers and their icons uploaded to OneDrive. Download them on the other device.')
        self._run(operation, complete, interactive=True)

    def upload(self):
        if self.job or self._upload_review is not None:
            return
        if not self.writer_compatible() or not self.state.get('ETag'):
            self._failed('Download a file written by the updated Windows version before uploading. This protects OS-specific shortcuts from older writers.')
            return
        try:
            snapshot = pack_icons(self.controller.document,LauncherIcons.glyph_png)
            envelope = sync_envelope(snapshot)
            for launcher in envelope['Launchers']:
                for field in LOCAL_LAUNCHER_FIELDS:
                    launcher.pop(field, None)
            baseline, etag = fingerprint(snapshot), self.state['ETag']
        except DocumentError as error:
            self._failed(str(error))
            return
        self.status.emit('Uploading launchers to OneDrive…')
        def operation(_):
            remote = self.transport.download()
            if not remote:
                raise SyncError('The OneDrive copy is missing. Nothing was uploaded.')
            require_sync_compatibility(remote[0])
            decision=sync_decision(baseline,fingerprint(remote[0]),self.state.get('Baseline'),remote[1]!=etag)
            if decision == 'same':return remote[1],False
            if decision in ('conflict','remote'):
                raise SyncError('OneDrive changed on another device. Use Upload to review the conflict; your local edits were kept.')
            return self.transport.upload(envelope, remote[1]),True
        def complete(result):
            new_etag,uploaded=result
            self.save_state(Baseline=baseline, ETag=new_etag)
            self.status.emit('Launchers and custom launcher images uploaded to OneDrive.' if uploaded else 'Launchers and icons are already up to date in OneDrive.')
        self._run(operation, complete)
