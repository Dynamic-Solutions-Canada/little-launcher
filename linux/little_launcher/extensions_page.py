"""Native extension management; packages are global, vault/sign-in data stays profile-local."""
from pathlib import Path
from PySide6.QtCore import QThread,Signal,Qt
from PySide6.QtWidgets import QComboBox,QDialog,QDialogButtonBox,QFileDialog,QHBoxLayout,QLabel,QListWidget,QMessageBox,QPushButton,QVBoxLayout
from .extension_packages import extract_package,import_folder,manifest

class PackageJob(QThread):
    def __init__(self,root,source,archive=False,parent=None):
        super().__init__(parent);self.root=root;self.source=source;self.archive=archive;self.result=None;self.error=''
    def run(self):
        try:self.result=extract_package(self.source,self.root) if self.archive else import_folder(self.source,self.root)
        except Exception:self.error='The extension package could not be prepared. Existing extensions and sign-ins were kept.'

class ExtensionsDialog(QDialog):
    def __init__(self,controller,browser,identifier=None,parent=None):
        super().__init__(parent);self.controller=controller;self.browser=browser;self.job=None;self.entries=[];self.quit_pending=False
        self.setWindowTitle('Browser Extensions');self.resize(620,520)
        layout=QVBoxLayout(self);layout.setContentsMargins(24,20,24,20);layout.setSpacing(12)
        intro=QLabel('Each browser profile has its own extension settings and sign-ins. Launchers using the same profile share them.');intro.setWordWrap(True);layout.addWidget(intro)
        self.profile=QComboBox()
        for launcher in controller.launchers:
            if launcher.get('Kind')==1 and controller.enabled(launcher['Id']):
                label='Shared browser profile' if launcher.get('WebSharedProfile') else 'Private browser profile'
                self.profile.addItem(f"{launcher.get('Name','Web launcher')} — {label}",launcher['Id'])
        if identifier and self.profile.findData(identifier)>=0:self.profile.setCurrentIndex(self.profile.findData(identifier))
        layout.addWidget(QLabel('Sign-in profile to manage or open'));layout.addWidget(self.profile)
        self.list=QListWidget();layout.addWidget(self.list,1)
        actions=QHBoxLayout();self.buttons=[]
        for label,action in [('Open extension',self.open_extension),('Enable / Disable',self.toggle),('Remove',self.remove)]:
            button=QPushButton(label);button.clicked.connect(action);actions.addWidget(button);self.buttons.append(button)
        layout.addLayout(actions)
        install=QHBoxLayout();self.archive=QPushButton('Add extension ZIP…');self.archive.clicked.connect(self.load_archive);install.addWidget(self.archive)
        self.unpacked=QPushButton('Load extension folder…');self.unpacked.clicked.connect(self.load_folder);install.addWidget(self.unpacked);layout.addLayout(install)
        self.status=QLabel('Choose a web launcher profile. Open its launcher before opening an extension popup.');self.status.setWordWrap(True);layout.addWidget(self.status)
        buttons=QDialogButtonBox(QDialogButtonBox.Close);buttons.rejected.connect(self.reject);layout.addWidget(buttons)
        browser.extensions_state.connect(self.receive);self.profile.currentIndexChanged.connect(self.refresh);self.finished.connect(lambda *_:self.disconnect_browser())
        self.refresh()
    def disconnect_browser(self):
        try:self.browser.extensions_state.disconnect(self.receive)
        except RuntimeError:pass
    def command(self,action,**values):
        identifier=self.profile.currentData();launcher=self.controller.launcher(identifier) if identifier else None
        if not launcher:self.status.setText('Add or enable a web launcher first.');return
        self.browser.send({'type':'extensions','action':action,'launcher':launcher,**values})
        self.status.setText('Updating extension state…')
    def refresh(self):self.command('list')
    def receive(self,message):
        if message.get('id')!=self.profile.currentData():return
        self.entries=message.get('entries',[]);self.list.clear()
        for entry in self.entries:
            state='On' if entry.get('enabled') else 'Off'
            if entry.get('error'):state=entry['error']
            self.list.addItem(f"{entry.get('name','Extension')} · {entry.get('version','')} · {state}")
        self.status.setText(message.get('error') or 'Open a launcher, then use its Extensions button or Open extension here. Installed extensions are available across this app; their settings and sign-ins stay in the selected browser profile.')
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
        if entry and QMessageBox.question(self,'Remove extension','Remove this extension from Little Launcher? Its saved profile data is kept.',QMessageBox.Yes|QMessageBox.No,QMessageBox.No)==QMessageBox.Yes:self.command('remove',path=entry['path'])
    def start_package(self,source,archive=False):
        if self.job:return
        self.job=PackageJob(self.controller.directory/'browser/extensions/packages',source,archive,parent=self);self.job.finished.connect(self.package_ready)
        self.archive.setEnabled(False);self.unpacked.setEnabled(False);self.status.setText('Preparing the extension package…');self.job.start()
    def load_archive(self):
        file,_=QFileDialog.getOpenFileName(self,'Choose extension ZIP','','Extension ZIP (*.zip)')
        if file:self.start_package(file,archive=True)
    def load_folder(self):
        folder=QFileDialog.getExistingDirectory(self,'Choose extension folder')
        if folder:self.start_package(folder)
    def package_ready(self):
        job=self.job;self.job=None;self.archive.setEnabled(True);self.unpacked.setEnabled(True)
        if job.error:self.status.setText(job.error)
        elif job.result and not self.quit_pending:
            data=manifest(job.result);permissions=data.get('permissions',[])+data.get('host_permissions',[])
            text='Add this extension to Little Launcher?\n\nRequested permissions:\n'+('\n'.join(str(p) for p in permissions) or 'None')+'\n\nOnly install extensions you trust. Some Chrome APIs may be unavailable.'
            prompt=QMessageBox(QMessageBox.Question,'Add browser extension',text,QMessageBox.Yes|QMessageBox.No,self);prompt.setTextFormat(Qt.PlainText);prompt.setDefaultButton(QMessageBox.No)
            if prompt.exec()==QMessageBox.Yes:self.command('add',path=str(job.result))
            else:self.status.setText('Extension was not enabled.')
        job.deleteLater()
    def reject(self):
        if self.job:self.status.setText('Wait for the extension package to finish preparing before closing.');return
        super().reject()
    def closeEvent(self,event):
        if self.job:event.ignore();return
        super().closeEvent(event)
