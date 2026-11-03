"""Native About-page update controls follow Windows check/status/action layout."""
from .item_toolbar import ActionFlowLayout
from PySide6.QtCore import QSignalBlocker
from PySide6.QtWidgets import QCheckBox,QLabel,QProgressBar,QPushButton,QVBoxLayout,QWidget

class UpdateControls(QWidget):
    def __init__(self,updates,parent=None):
        super().__init__(parent);self.updates=updates
        layout=QVBoxLayout(self);layout.setContentsMargins(0,0,0,0)
        self.status=QLabel();self.status.setWordWrap(True);layout.addWidget(self.status)
        buttons=QWidget();row=ActionFlowLayout(buttons);self.check=QPushButton('Check for Updates');self.check.clicked.connect(updates.check);row.addWidget(self.check)
        self.download=QPushButton('Download Update');self.download.clicked.connect(updates.download);row.addWidget(self.download)
        self.install=QPushButton('Install and Restart');self.install.clicked.connect(updates.request_install);row.addWidget(self.install)
        self.cancel=QPushButton('Cancel');self.cancel.clicked.connect(updates.cancel);row.addWidget(self.cancel);layout.addWidget(buttons)
        self.progress=QProgressBar();self.progress.setRange(0,100);layout.addWidget(self.progress)
        self.automatic=QCheckBox('Check for updates automatically');self.automatic.setChecked(updates.automatic());self.automatic.toggled.connect(updates.set_automatic);layout.addWidget(self.automatic)
        self.notice=QLabel('Self-update is available when running a writable AppImage. Downloads are verified before installation; settings and browser sign-ins stay in their existing folders.')
        self.notice.setWordWrap(True);layout.addWidget(self.notice)
        updates.changed.connect(self.refresh);self.refresh()
    def refresh(self):
        update=self.updates;busy=bool(update.job)
        self.status.setText(update.status)
        with QSignalBlocker(self.automatic):self.automatic.setChecked(update.automatic())
        self.check.setEnabled(not busy and not update.staged)
        self.download.setVisible(bool(update.release) and not update.staged);self.download.setEnabled(not busy and bool(update.image))
        self.install.setVisible(bool(update.staged));self.install.setEnabled(not busy and not update.install_queued)
        self.cancel.setVisible(busy or bool(update.staged));self.cancel.setText('Cancel' if busy else 'Discard Download')
        self.cancel.setEnabled(not update.install_queued)
        self.progress.setVisible(update.state=='downloading');self.progress.setValue(update.percent)
