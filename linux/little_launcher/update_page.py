"""Native About-page update controls follow Windows check/status/action layout."""
from . import i18n
from .item_toolbar import ActionFlowLayout
from PySide6.QtCore import QSignalBlocker
from PySide6.QtWidgets import QCheckBox,QLabel,QProgressBar,QPushButton,QVBoxLayout,QWidget

class UpdateControls(QWidget):
    def __init__(self,updates,parent=None):
        super().__init__(parent);self.updates=updates
        layout=QVBoxLayout(self);layout.setContentsMargins(0,0,0,0)
        self.status=QLabel();self.status.setWordWrap(True);layout.addWidget(self.status)
        buttons=QWidget();row=ActionFlowLayout(buttons);self.check=QPushButton(i18n.t('about.update.checkButton'));self.check.clicked.connect(updates.check);row.addWidget(self.check)
        self.download=QPushButton(i18n.t('linuxUpdates.download'));self.download.clicked.connect(updates.download);row.addWidget(self.download)
        self.install=QPushButton(i18n.t('linuxUpdates.install'));self.install.clicked.connect(updates.request_install);row.addWidget(self.install)
        self.cancel=QPushButton(i18n.t('common.cancel'));self.cancel.clicked.connect(updates.cancel);row.addWidget(self.cancel);layout.addWidget(buttons)
        self.progress=QProgressBar();self.progress.setRange(0,100);layout.addWidget(self.progress)
        self.automatic=QCheckBox(i18n.t('linuxUpdates.automatic'));self.automatic.setChecked(updates.automatic());self.automatic.toggled.connect(updates.set_automatic);layout.addWidget(self.automatic)
        self.notice=QLabel(i18n.t('linuxUpdates.notice'))
        self.notice.setWordWrap(True);layout.addWidget(self.notice)
        updates.changed.connect(self.refresh);self.refresh()
    def refresh(self):
        update=self.updates;busy=bool(update.job)
        self.status.setText(update.status)
        with QSignalBlocker(self.automatic):self.automatic.setChecked(update.automatic())
        self.check.setEnabled(not busy and not update.staged)
        self.download.setVisible(bool(update.release) and not update.staged);self.download.setEnabled(not busy and bool(update.image))
        self.install.setVisible(bool(update.staged));self.install.setEnabled(not busy and not update.install_queued)
        self.cancel.setVisible(busy or bool(update.staged));self.cancel.setText(i18n.t('common.cancel') if busy else i18n.t('linuxUpdates.discard'))
        self.cancel.setEnabled(not update.install_queued)
        self.progress.setVisible(update.state=='downloading');self.progress.setValue(update.percent)
