"""Native searchable installed applications, reusable inside the Windows-shaped item form."""
from copy import deepcopy
from pathlib import Path
import shlex
from PySide6.QtCore import QEvent, QUrl, Qt, Signal
from PySide6.QtGui import QDesktopServices, QIcon
from PySide6.QtWidgets import QComboBox, QDialog, QDialogButtonBox, QFrame, QHBoxLayout, QLineEdit, QListWidget, QListWidgetItem, QMenu, QMessageBox, QVBoxLayout, QWidget

from .catalog import DesktopCatalog
from .desktop import ICON, launch_item


def is_web_app(entry):
    try:tokens=shlex.split(entry.get('Exec',''))
    except ValueError:tokens=[]
    return entry.get('StartupWMClass','').casefold().startswith('crx_') or any(token.startswith(('--app-id=','--app=')) for token in tokens)


class ApplicationList(QWidget):
    selected = Signal(dict)
    activated = Signal()

    def __init__(self, parent=None, entries=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.search = QLineEdit()
        self.search.setPlaceholderText('Search installed applications')
        search_row=QHBoxLayout();search_row.addWidget(self.search,1)
        self.source=QComboBox();self.source.addItems(['All apps','Applications','Web apps']);search_row.addWidget(self.source);layout.addLayout(search_row)
        self.list = QListWidget()
        self.list.setFrameShape(QFrame.NoFrame)
        self.list.setMinimumHeight(180)
        layout.addWidget(self.list)
        self.entries = DesktopCatalog().applications() if entries is None else entries
        self.source.currentIndexChanged.connect(lambda *_:self.filter(self.search.text()))
        self.list.setContextMenuPolicy(Qt.NoContextMenu)
        self.list.installEventFilter(self);self.list.viewport().installEventFilter(self)
        self.search.textChanged.connect(self.filter)
        self.list.currentItemChanged.connect(lambda current, previous: self.selected.emit(current.data(Qt.UserRole)) if current else None)
        self.list.itemDoubleClicked.connect(lambda *_: self.activated.emit())
        self.filter('')

    def filter(self, text):
        self.list.clear()
        for entry in self.entries:
            if text.casefold() not in (entry['Name'] + ' ' + entry.get('GenericName', '')).casefold():
                continue
            web=is_web_app(entry)
            if self.source.currentIndex()==1 and web or self.source.currentIndex()==2 and not web:continue
            item = QListWidgetItem(QIcon.fromTheme(entry.get('Icon', ''), QIcon(str(ICON))), entry['Name'])
            item.setData(Qt.UserRole, entry)
            item.setToolTip(entry['Name'] + '\n' + entry['_path'])
            self.list.addItem(item)
        # Selecting a filter result is an explicit user choice; opening a form never replaces its source.

    def row_menu(self,item):
        entry=deepcopy(item.data(Qt.UserRole));menu=QMenu(self)
        menu.addAction('Open',lambda:self.open_entry(entry))
        folder=Path(entry['_path']).parent
        action=menu.addAction('Open containing folder',lambda:self.open_folder(folder))
        action.setEnabled(folder.is_dir())
        return menu

    def open_entry(self,entry):
        try:launch_item({'Name':entry['Name'],'Path':entry['_path'],'Arguments':''})
        except (OSError,ValueError) as error:QMessageBox.warning(self,'Could not open application',str(error))

    def open_folder(self,folder):
        if folder.is_dir() and not QDesktopServices.openUrl(QUrl.fromLocalFile(str(folder))):
            QMessageBox.warning(self,'Could not open folder','The file manager could not open this application’s folder.')

    def eventFilter(self,widget,event):
        item=None;point=None
        if widget is self.list.viewport() and event.type()==QEvent.MouseButtonPress and event.button()==Qt.RightButton:
            point=event.position().toPoint();item=self.list.itemAt(point)
        elif widget is self.list and event.type()==QEvent.KeyPress and (event.key()==Qt.Key_Menu or event.key()==Qt.Key_F10 and event.modifiers() & Qt.ShiftModifier):
            item=self.list.currentItem()
            if item:point=self.list.visualItemRect(item).center()
        else:return False
        if item:
            self.context_menu=self.row_menu(item)
            self.context_menu.popup(self.list.viewport().mapToGlobal(point))
        return True

    def value(self):
        return self.list.currentItem().data(Qt.UserRole) if self.list.currentItem() else None


class ApplicationPicker(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle('Choose an application')
        self.resize(520, 560)
        layout = QVBoxLayout(self)
        self.catalog = ApplicationList(self)
        self.search, self.list, self.entries = self.catalog.search, self.catalog.list, self.catalog.entries
        layout.addWidget(self.catalog)
        self.catalog.activated.connect(self.accept)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def filter(self, text):
        self.catalog.filter(text)

    def accept(self):
        if self.catalog.value() is not None:
            super().accept()

    def value(self):
        return self.catalog.value()
