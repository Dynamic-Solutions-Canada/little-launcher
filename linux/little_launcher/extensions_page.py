"""Native extension management; packages are global, vault/sign-in data stays profile-local."""
from pathlib import Path
from PySide6.QtCore import QThread,Signal,Qt
from PySide6.QtWidgets import QComboBox,QDialog,QDialogButtonBox,QFileDialog,QHBoxLayout,QLabel,QListWidget,QMessageBox,QPushButton,QVBoxLayout
from . import i18n
from .extension_packages import extract_package,import_folder,manifest

class PackageJob(QThread):
    def __init__(self,root,source,archive=False,parent=None):
        super().__init__(parent);self.root=root;self.source=source;self.archive=archive;self.result=None;self.error=''
    def run(self):
        try:self.result=extract_package(self.source,self.root) if self.archive else import_folder(self.source,self.root)
        except Exception:self.error=i18n.t('webExtensions.packageFailed')

class ExtensionsDialog(QDialog):
    def __init__(self,controller,browser,identifier=None,parent=None):
        super().__init__(parent);self.controller=controller;self.browser=browser;self.job=None;self.entries=[];self.quit_pending=False
        self.setWindowTitle(i18n.t('system.extensions.section'));self.resize(620,520)
        layout=QVBoxLayout(self);layout.setContentsMargins(24,20,24,20);layout.setSpacing(12)
        intro=QLabel(i18n.t('webExtensions.profilesIntro'));intro.setWordWrap(True);layout.addWidget(intro)
        self.profile=QComboBox()
        for launcher in controller.launchers:
            if launcher.get('Kind')==1 and controller.enabled(launcher['Id']):
                label=i18n.t('webExtensions.profile.shared' if launcher.get('WebSharedProfile') else 'webExtensions.profile.private')
                self.profile.addItem(i18n.t('webExtensions.profile.entry',launcher=launcher.get('Name',i18n.t('launchers.defaultName.web')),profile=label),launcher['Id'])
        if identifier and self.profile.findData(identifier)>=0:self.profile.setCurrentIndex(self.profile.findData(identifier))
        layout.addWidget(QLabel(i18n.t('webExtensions.profile.label')));layout.addWidget(self.profile)
        self.list=QListWidget();layout.addWidget(self.list,1)
        actions=QHBoxLayout();self.buttons=[]
        for label,action in [('webExtensions.open',self.open_extension),('webExtensions.toggle',self.toggle),('common.remove',self.remove)]:
            button=QPushButton(i18n.t(label));button.clicked.connect(action);actions.addWidget(button);self.buttons.append(button)
        layout.addLayout(actions)
        install=QHBoxLayout();self.archive=QPushButton(i18n.t('webExtensions.addZip'));self.archive.clicked.connect(self.load_archive);install.addWidget(self.archive)
        self.unpacked=QPushButton(i18n.t('webExtensions.loadFolder'));self.unpacked.clicked.connect(self.load_folder);install.addWidget(self.unpacked);layout.addLayout(install)
        self.status=QLabel(i18n.t('webExtensions.chooseProfile'));self.status.setWordWrap(True);layout.addWidget(self.status)
        buttons=QDialogButtonBox(QDialogButtonBox.Close);buttons.rejected.connect(self.reject);layout.addWidget(buttons)
        browser.extensions_state.connect(self.receive);self.profile.currentIndexChanged.connect(self.refresh);self.finished.connect(lambda *_:self.disconnect_browser())
        self.refresh()
    def disconnect_browser(self):
        try:self.browser.extensions_state.disconnect(self.receive)
        except RuntimeError:pass
    def command(self,action,**values):
        identifier=self.profile.currentData();launcher=self.controller.launcher(identifier) if identifier else None
        if not launcher:self.status.setText(i18n.t('webExtensions.noWebLauncher'));return
        self.browser.send({'type':'extensions','action':action,'launcher':launcher,**values})
        self.status.setText(i18n.t('webExtensions.updating'))
    def refresh(self):self.command('list')
    def receive(self,message):
        if message.get('id')!=self.profile.currentData():return
        self.entries=message.get('entries',[]);self.list.clear()
        for entry in self.entries:
            state=i18n.t('common.on' if entry.get('enabled') else 'common.off')
            if entry.get('error'):state=entry['error']
            self.list.addItem(f"{entry.get('name',i18n.t('webTabs.extensionFallbackTitle'))} · {entry.get('version','')} · {state}")
        self.status.setText(message.get('error') or i18n.t('webExtensions.manageHint'))
    def selected(self):
        index=self.list.currentRow();return self.entries[index] if 0<=index<len(self.entries) else None
    def open_extension(self):
        entry=self.selected()
        if entry:self.command('open',id=entry['id'])
    def toggle(self):
        entry=self.selected()
        if entry:self.command('enable',path=entry['path'],enabled=not entry.get('enabled'))
    def remove(self):
        entry=self.selected()
        if entry and QMessageBox.question(self,i18n.t('webExtensions.remove.title'),i18n.t('webExtensions.remove.message'),QMessageBox.Yes|QMessageBox.No,QMessageBox.No)==QMessageBox.Yes:self.command('remove',path=entry['path'])
    def start_package(self,source,archive=False):
        if self.job:return
        self.job=PackageJob(self.controller.directory/'browser/extensions/packages',source,archive,parent=self);self.job.finished.connect(self.package_ready)
        self.archive.setEnabled(False);self.unpacked.setEnabled(False);self.status.setText(i18n.t('webExtensions.preparing'));self.job.start()
    def load_archive(self):
        file,_=QFileDialog.getOpenFileName(self,i18n.t('webExtensions.chooseZip'),'',i18n.t('webExtensions.zipFiles')+' (*.zip)')
        if file:self.start_package(file,archive=True)
    def load_folder(self):
        folder=QFileDialog.getExistingDirectory(self,i18n.t('webExtensions.chooseFolder'))
        if folder:self.start_package(folder)
    def package_ready(self):
        job=self.job;self.job=None;self.archive.setEnabled(True);self.unpacked.setEnabled(True)
        if job.error:self.status.setText(job.error)
        elif job.result and not self.quit_pending:
            data=manifest(job.result);permissions=data.get('permissions',[])+data.get('host_permissions',[])
            text=i18n.t('webExtensions.add.message',permissions='\n'.join(str(p) for p in permissions) or i18n.t('common.none'))
            prompt=QMessageBox(QMessageBox.Question,i18n.t('webExtensions.add.title'),text,QMessageBox.Yes|QMessageBox.No,self);prompt.setTextFormat(Qt.PlainText);prompt.setDefaultButton(QMessageBox.No)
            if prompt.exec()==QMessageBox.Yes:self.command('add',path=str(job.result))
            else:self.status.setText(i18n.t('webExtensions.notEnabled'))
        job.deleteLater()
    def reject(self):
        if self.job:self.status.setText(i18n.t('webExtensions.waitBeforeClosing'));return
        super().reject()
    def closeEvent(self,event):
        if self.job:event.ignore();return
        super().closeEvent(event)
