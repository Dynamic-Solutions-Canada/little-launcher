"""Native launcher forms, preserving opaque fields from the Windows document."""
from copy import deepcopy

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFileDialog, QFormLayout,
    QGroupBox, QHBoxLayout, QLabel, QLineEdit, QPushButton, QScrollArea, QFrame,
    QSpinBox, QTabWidget, QVBoxLayout, QWidget, QMessageBox,
)

from .document import DocumentError, is_heading, normalize_web_address, new_shortcut


class FormDialog(QDialog):
    """Native dialog button order and a scrolling form shared by the domain's editors."""

    def __init__(self, title, parent=None):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(560, 620)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(24, 8, 24, 24)
        scroll = QScrollArea()
        self.form_scroll = scroll
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setWidgetResizable(True)
        body = QWidget()
        self.sections = QVBoxLayout(body)
        self.sections.setAlignment(Qt.AlignTop)
        scroll.setWidget(body)
        outer.addWidget(scroll)
        buttons = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        outer.addWidget(buttons)

    def validate(self):
        """Domain forms override this hook; failure keeps the form open without saving."""
        pass

    def accept(self):
        try:
            self.validate()
        except DocumentError as error:
            QMessageBox.warning(self, 'Check these details', str(error))
            return
        super().accept()

    def section(self, title, plain=False):
        if plain:
            group = QWidget()
            form = QFormLayout(group)
            form.setRowWrapPolicy(QFormLayout.WrapAllRows)
        else:
            group = QGroupBox(title)
            form = QFormLayout(group)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.sections.addWidget(group)
        return form



class ShortcutDialog(FormDialog):
    """Edit a full original row; cancelling changes nothing."""

    TYPES = ['Application', 'Website', 'Heading', 'Group', 'Column break']

    def __init__(self, item=None, parent=None, *, icon_directory=None):
        super().__init__('Edit Item' if item else 'Add Item', parent)
        self.original = deepcopy(item) if item else new_shortcut('', '')
        self.icon_directory=icon_directory
        self.source_tabs = QTabWidget()
        self.sections.addWidget(self.source_tabs)
        from .app_picker import ApplicationList
        self.catalog = ApplicationList()
        self.source_tabs.addTab(self.catalog, 'Apps && web apps')
        custom = QWidget()
        custom_form = QFormLayout(custom)
        custom_form.setRowWrapPolicy(QFormLayout.WrapAllRows)
        self.source_tabs.addTab(custom, 'File or link')
        form = self.section('Details', plain=True)
        self.kind = QComboBox()
        self.kind.addItems(self.TYPES)
        index = 0
        if self.original.get('IsColumnBreak'):
            index = 4
        elif self.original.get('IsGroup'):
            index = 3
        elif is_heading(self.original) and item:
            index = 2
        elif self.original.get('IsWebsite'):
            index = 1
        self.kind.setCurrentIndex(index)
        self.name = QLineEdit(self.original.get('Name', ''))
        self.target = QLineEdit(self.original.get('Path', ''))
        self.arguments = QLineEdit(self.original.get('Arguments', ''))
        browse = QPushButton('Browse…')
        browse.clicked.connect(self._browse)
        target_row = QWidget()
        target_layout = QHBoxLayout(target_row)
        target_layout.setContentsMargins(0, 0, 0, 0)
        target_layout.addWidget(self.target)
        target_layout.addWidget(browse)
        custom_form.addRow('Path or link', target_row)
        custom_form.addRow('Type', self.kind)
        form.addRow('Name', self.name)
        self.icon_fields={key:self.original.get(key,'') for key in ('IconGlyph','IconColor')}
        self.icon_path = QLineEdit(self.original.get('IconPath', ''))
        icon_row = QWidget()
        icon_layout = QHBoxLayout(icon_row)
        icon_layout.setContentsMargins(0, 0, 0, 0)
        self.icon_preview=QLabel()
        icon_layout.addWidget(self.icon_preview)
        icon_button = QPushButton('Choose…')
        icon_button.clicked.connect(self._pick_icon)
        icon_layout.addWidget(icon_button)
        form.addRow('Icon', icon_row)
        form.addRow('Arguments', self.arguments)
        self.app_window = QCheckBox('Open website in an application window')
        self.app_window.setChecked(self.original.get('OpenInAppWindow', False))
        self.app_window.setEnabled(index == 1)
        form.addRow(self.app_window)
        self.target.textChanged.connect(self._target_changed)
        self.kind.currentIndexChanged.connect(self._kind_changed)
        self.target.setEnabled(index < 2)
        self._target_changed()
        self.catalog.selected.connect(self._application_selected)
        self.catalog.activated.connect(self.accept)
        self.source_tabs.setCurrentIndex(1 if item else 0)
        self.icon_path.textChanged.connect(self._refresh_icon)
        self.target.textChanged.connect(self._refresh_icon)
        self._refresh_icon()

    def _application_selected(self, entry):
        self.kind.setCurrentIndex(0)
        self.name.setText(entry['Name'])
        self.target.setText(entry['_path'])
        self.arguments.clear()

    def _refresh_icon(self):
        from .desktop import item_icon
        item={**self.original,**self.icon_fields,'IconPath':self.icon_path.text(),'Path':self.target.text(),'IsWebsite':self.kind.currentIndex()==1}
        self.icon_preview.setPixmap(item_icon(item).pixmap(32,32))
        self.icon_preview.setToolTip(self.icon_path.text() or self.icon_fields.get('IconGlyph') or 'Automatic icon')

    def _pick_icon(self):
        from .icon_picker import IconPicker
        item={**self.original,**self.icon_fields,'IconPath':self.icon_path.text(),'Path':self.target.text(),'IsWebsite':self.kind.currentIndex()==1}
        dialog=IconPicker({},self,item=item,cache_directory=self.icon_directory)
        if dialog.exec()==QDialog.Accepted:
            selected=dialog.value()
            self.icon_fields={key:selected[key] for key in ('IconGlyph','IconColor')}
            self.icon_path.setText(selected['IconPath'])
            self._refresh_icon()

    def _target_changed(self):
        self.arguments.setEnabled(self.kind.currentIndex() == 0 and not self.target.text().endswith('.desktop'))
        self.arguments.setToolTip('Choose an executable for extra arguments. Installed desktop entries use their own launch command.')

    def _kind_changed(self, index):
        structural = index >= 2
        self.target.setEnabled(not structural)
        self._target_changed()
        self.app_window.setEnabled(index == 1)

    def validate(self):
        value = self.value()
        if self.kind.currentIndex() < 2 and not value['Path']:
            raise DocumentError('Choose a target before saving this shortcut.')
        if self.kind.currentIndex() in (2, 3) and not value['Name']:
            raise DocumentError('Enter a name for this heading or group.')

    def _choose_application(self):
        from .app_picker import ApplicationPicker
        dialog = ApplicationPicker(self)
        if dialog.exec() == QDialog.Accepted:
            entry = dialog.value()
            self.kind.setCurrentIndex(0)
            self.name.setText(entry['Name'])
            self.target.setText(entry['_path'])
            self.arguments.clear()

    def _browse(self):
        path, _ = QFileDialog.getOpenFileName(self, 'Choose application or file')
        if path:
            self.target.setText(path)

    def value(self):
        """Apply recognized fields only, retaining unknown item and child metadata."""
        item = deepcopy(self.original)
        index = self.kind.currentIndex()
        item.update(Name=self.name.text().strip(), Path=self.target.text().strip() if index < 2 else '',
                    Arguments=self.arguments.text(), IsWebsite=index == 1,
                    IsGroup=index == 3, IsColumnBreak=index == 4,
                    OpenInAppWindow=index == 1 and self.app_window.isChecked())
        if index == 1:
            item['Path'] = normalize_web_address(item['Path'])
        if item['Path'] != self.original.get('Path') or item['IsWebsite'] != self.original.get('IsWebsite', False):
            item['IsPwa'] = False
        if index < 2 and not item['Name']:
            from urllib.parse import urlsplit
            from pathlib import Path
            item['Name'] = urlsplit(item['Path']).hostname if index == 1 else Path(item['Path']).name
        item.update(self.icon_fields)
        item['IconPath'] = self.icon_path.text()
        if index == 3:
            item.setdefault('Children', [])
        return item


class BookmarkDialog(FormDialog):
    """Native name/address form; changes to bookmarks never change the launcher's home."""

    def __init__(self, name, address, parent=None, editing=False):
        super().__init__('Edit bookmark' if editing else 'Add bookmark', parent)
        self.resize(480, 240)
        form = self.section('Bookmark')
        self.name = QLineEdit(name)
        self.address = QLineEdit(address)
        form.addRow('Name', self.name)
        form.addRow('Address', self.address)

    def validate(self):
        self.value()

    def value(self):
        from urllib.parse import urlsplit
        address = normalize_web_address(self.address.text())
        return {'Name': self.name.text().strip() or urlsplit(address).hostname, 'Url': address}


class NameDialog(QDialog):
    """Compact native counterpart of the shared Windows TextPromptWindow."""

    def __init__(self, title, name='', parent=None, placeholder='Name', accept_text='Save'):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(400, 168)
        self.setWindowModality(Qt.WindowModal)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 8, 20, 20)
        self.name = QLineEdit(name)
        self.name.setPlaceholderText(placeholder)
        self.name.selectAll()
        layout.addWidget(self.name)
        layout.addStretch()
        buttons = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        save = buttons.button(QDialogButtonBox.Save)
        save.setText(accept_text)
        save.setMinimumWidth(90)
        save.setDefault(True)
        save.setEnabled(bool(name.strip()))
        self.name.textChanged.connect(lambda text: save.setEnabled(bool(text.strip())))
        self.name.returnPressed.connect(self.accept)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def accept(self):
        if self.name.text().strip():
            super().accept()

    def value(self):
        name = self.name.text().strip()
        if not name:
            raise DocumentError('Enter a name.')
        return name


class BookmarkAddressDialog(NameDialog):
    """Single-address editor that validates before closing and never changes the caption."""

    def __init__(self, address, parent=None):
        super().__init__('Edit bookmark address', address, parent, placeholder='Address')

    def value(self):
        return normalize_web_address(super().value())

    def accept(self):
        try:
            self.value()
        except DocumentError as error:
            QMessageBox.warning(self, 'Edit bookmark address', str(error))
            return
        super().accept()


class BookmarkFolderDialog(NameDialog):
    """Keep bookmark-specific serialization out of the shared single-name prompt."""

    def __init__(self, name, parent=None):
        super().__init__('Rename folder' if name else 'Add folder', name, parent)

    def value(self):
        return {'Name': super().value(), 'IsFolder': True}


# Preserve callers while launcher-specific sections and rows live in their own module.
from .launcher_settings import LauncherDialog
