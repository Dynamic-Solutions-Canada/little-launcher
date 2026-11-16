"""Native AppImage update worker lifetime and explicit install/restart intent."""
import os
import platform
import subprocess
import threading
from PySide6.QtCore import QObject,QThread,QTimer,Signal
from PySide6.QtWidgets import QMessageBox
from . import i18n
from .desktop import application_version
from .updates import UpdateError,check_release,current_appimage,download_release,sync_directory


class UpdateJob(QThread):
    progress=Signal(int)
    def __init__(self,operation,version,architecture,release=None,destination=None,parent=None):
        super().__init__(parent);self.operation=operation;self.version=version;self.architecture=architecture
        self.release=release;self.destination=destination;self.last_percent=-1;self.canceled=threading.Event();self.result=None;self.error=''
    def run(self):
        try:
            if self.operation=='check':self.result=check_release(self.version,self.architecture,cancelled=self.canceled.is_set)
            else:self.result=download_release(self.release,self.destination,architecture=self.architecture,cancelled=self.canceled.is_set,progress=self.report_progress)
            if self.canceled.is_set() and self.result is not None and self.operation=='download':self.result.discard();self.result=None
        except UpdateError as error:self.error=str(error)
        except Exception:self.error=i18n.t('linuxUpdates.error.requestFailed')

    def report_progress(self,done,total):
        percent=int(done*100/total)
        if percent!=self.last_percent:self.last_percent=percent;self.progress.emit(percent)


class UpdateController(QObject):
    changed=Signal()
    busy_changed=Signal(bool)
    install_requested=Signal()
    def __init__(self,controller,parent=None):
        super().__init__(parent);self.controller=controller;self.version=application_version();self.architecture='arm64' if platform.machine() in ('aarch64','arm64') else 'x64'
        self.image=None if os.environ.get('LITTLE_LAUNCHER_PREVIEW_ID') else current_appimage()
        self.job=None;self.release=None;self.staged=None;self.install_queued=False;self.percent=0
        self.state='idle';self.status=i18n.t('linuxUpdates.status.idle')
        self.periodic=QTimer(self);self.periodic.setInterval(6*60*60*1000);self.periodic.timeout.connect(self.auto_check)
        if self.image:
            self.periodic.start();QTimer.singleShot(15000,self.auto_check)

    def automatic(self):return self.controller.preferences.data.get('CheckForUpdatesAutomatically',True)
    def set_automatic(self,value):
        try:self.controller.preferences.set('CheckForUpdatesAutomatically',bool(value))
        except OSError:self.status=i18n.t('linuxUpdates.status.preferenceNotSaved')
        self.changed.emit()
    def auto_check(self):
        if self.automatic() and not self.job and not self.staged:self.check()
    def check(self):
        if self.job or self.staged:return
        self.state='checking';self.status=i18n.t('about.update.checkingStatus')
        self.start(UpdateJob('check',self.version,self.architecture,parent=self))
    def download(self):
        if self.job or self.staged or not self.release or not self.image:return
        self.state='downloading';self.status=i18n.t('linuxUpdates.status.downloading');self.percent=0
        self.start(UpdateJob('download',self.version,self.architecture,self.release,self.image,parent=self))
    def start(self,job):
        self.job=job;job.progress.connect(self.update_progress);job.finished.connect(self.complete)
        self.busy_changed.emit(True);self.changed.emit();job.start()
    def update_progress(self,value):self.percent=value;self.changed.emit()
    def complete(self):
        job=self.job
        if not job:return
        self.job=None
        if job.canceled.is_set():self.state='idle';self.status=i18n.t('linuxUpdates.status.canceled')
        elif job.error:self.state='error';self.status=job.error
        elif job.operation=='check':
            self.release=job.result;self.state='available' if self.release else 'current'
            self.status=i18n.t('about.update.available',latest=self.release.version,current=self.version) if self.release else i18n.t('about.update.upToDate',current=self.version)
        else:
            self.staged=job.result;self.state='ready';self.status=i18n.t('linuxUpdates.status.ready')
        job.deleteLater();self.changed.emit();self.busy_changed.emit(False)
    def cancel(self):
        if self.job:
            self.job.canceled.set();self.status=i18n.t('linuxUpdates.status.canceling')
        elif self.staged:
            try:self.staged.discard()
            except OSError:self.status=i18n.t('linuxUpdates.status.discardFailed');self.changed.emit();return
            self.staged=None;self.state='available' if self.release else 'idle';self.status=i18n.t('linuxUpdates.status.discarded')
        self.install_queued=False;self.changed.emit()
    def request_install(self):
        if not self.staged or self.job or self.install_queued:return
        self.install_queued=True;self.status=i18n.t('linuxUpdates.status.waitingToRestart');self.changed.emit();self.install_requested.emit()
    def cancel_restart(self):
        self.install_queued=False
        if self.staged:self.status=i18n.t('linuxUpdates.status.restartCanceled')
        self.changed.emit()
    def finish_restart(self,exit_code=0):
        if not self.install_queued or not self.staged:
            if self.staged:
                try:self.staged.discard()
                except OSError:pass
                self.staged=None
            return exit_code
        backup=None
        try:
            backup=self.staged.install()
            subprocess.Popen([str(self.image),'--background','--data-dir',str(self.controller.directory)],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
            return 0
        except (UpdateError,OSError) as error:
            if backup:
                try:os.replace(backup,self.image);sync_directory(self.image.parent)
                except OSError:pass
            QMessageBox.critical(None,i18n.t('linuxUpdates.installFailed.title'),i18n.t('linuxUpdates.installFailed.message',error=str(error)))
            return 1
