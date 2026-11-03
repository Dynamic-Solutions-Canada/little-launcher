"""Native single-bookmark picker matching the Windows browser/profile/search/confirm flow."""
from pathlib import Path
from PySide6.QtCore import Qt
from PySide6.QtWidgets import QComboBox,QDialog,QDialogButtonBox,QFileDialog,QHBoxLayout,QLabel,QLineEdit,QTreeWidget,QTreeWidgetItem,QVBoxLayout,QPushButton
from .browser_bookmarks import BookmarkSource,read_bookmarks,sources
from .document import DocumentError

class BookmarkPicker(QDialog):
    def __init__(self,parent=None,catalog=None,*,multiple=False):
        super().__init__(parent)
        self.multiple=multiple;self.checked=set();self.visible_rows={};self.loading=False
        self.setWindowTitle('Import Browser Bookmarks' if multiple else 'Choose a browser bookmark');self.resize(560,520)
        self.catalog=sources() if catalog is None else catalog
        self.records=[]
        layout=QVBoxLayout(self);layout.setContentsMargins(20,16,20,16);layout.setSpacing(12)
        row=QHBoxLayout();self.browser=QComboBox();self.profile=QComboBox()
        row.addWidget(self.browser,1);row.addWidget(self.profile,1);layout.addLayout(row)
        self.search=QLineEdit();self.search.setPlaceholderText('Search bookmarks');layout.addWidget(self.search)
        self.list=QTreeWidget();self.list.setColumnCount(2);self.list.setHeaderLabels(['Name','Address']);self.list.setRootIsDecorated(False)
        self.list.setColumnWidth(0,220);layout.addWidget(self.list,1)
        if multiple:
            actions=QHBoxLayout();select=QPushButton('Select visible');clear=QPushButton('Clear selection')
            select.clicked.connect(self.select_visible);clear.clicked.connect(self.clear_selection);actions.addWidget(select);actions.addWidget(clear);actions.addStretch();layout.addLayout(actions)
        self.status=QLabel();self.status.setWordWrap(True);layout.addWidget(self.status)
        footer=QHBoxLayout();browse=QPushButton('Choose bookmark file…');browse.clicked.connect(self.choose_file);footer.addWidget(browse);footer.addStretch()
        buttons=QDialogButtonBox(QDialogButtonBox.Ok|QDialogButtonBox.Cancel);self.confirm=buttons.button(QDialogButtonBox.Ok);self.confirm.setText('Import selected' if multiple else 'Select');self.confirm.setEnabled(False)
        buttons.accepted.connect(self.accept);buttons.rejected.connect(self.reject);footer.addWidget(buttons);layout.addLayout(footer)
        self.browser.currentIndexChanged.connect(self.populate_profiles);self.profile.currentIndexChanged.connect(self.load)
        self.search.textChanged.connect(self.filter)
        self.list.itemSelectionChanged.connect(lambda:self.confirm.setEnabled(bool(self.checked) if self.multiple else self.selected() is not None))
        self.list.itemChanged.connect(self.save_checks)
        if not multiple:self.list.itemDoubleClicked.connect(lambda *_:self.accept())
        self.browser.addItems(list(dict.fromkeys(source.browser for source in self.catalog)))
        if not self.catalog:self.status.setText('No supported browser profiles found. Choose an exported Chrome/Edge bookmark JSON, Firefox places.sqlite, or bookmark HTML file.')
        self.search.setFocus()

    def populate_profiles(self):
        self.profile.clear()
        for source in self.catalog:
            if source.browser==self.browser.currentText():self.profile.addItem(source.profile,source)

    def load(self):
        source=self.profile.currentData();self.records=[];self.checked.clear()
        if source:
            try:self.records=read_bookmarks(source.path);self.status.setText(f'{len(self.records)} bookmarks. Select one to continue.')
            except DocumentError as error:self.status.setText(str(error))
        self.filter()

    def filter(self):
        query=self.search.text().casefold();self.loading=True;self.list.blockSignals(True);self.list.clear();self.visible_rows={}
        folders={}
        try:
            for index,record in enumerate(self.records):
                if query and query not in ' '.join(record.values()).casefold():continue
                item=QTreeWidgetItem([record['Name'],record['Url']]);item.setToolTip(0,record['Folder'] or record['Name']);item.setToolTip(1,record['Url'])
                if self.multiple:
                    parent=None;parts=[]
                    for name in record['Folder'].split('/'):
                        if not name:continue
                        parts.append(name);key=tuple(parts)
                        if key not in folders:
                            folder=QTreeWidgetItem([name,'']);folder.setFlags(folder.flags()|Qt.ItemIsUserCheckable|Qt.ItemIsAutoTristate)
                            if parent:parent.addChild(folder)
                            else:self.list.addTopLevelItem(folder)
                            folders[key]=folder
                        parent=folders[key]
                    if parent:parent.addChild(item)
                    else:self.list.addTopLevelItem(item)
                    item.setData(0,Qt.UserRole,index);item.setFlags(item.flags()|Qt.ItemIsUserCheckable)
                    item.setCheckState(0,Qt.Checked if index in self.checked else Qt.Unchecked);self.visible_rows[index]=item
                else:
                    item.setData(0,Qt.UserRole,record);self.list.addTopLevelItem(item)
            if self.multiple:self.list.expandToDepth(0)
        finally:
            self.list.blockSignals(False);self.loading=False
        self.update_selection()

    def save_checks(self,*_):
        if not self.multiple or self.loading:return
        for index,item in self.visible_rows.items():
            if item.checkState(0)==Qt.Checked:self.checked.add(index)
            else:self.checked.discard(index)
        self.update_selection()

    def update_selection(self):
        self.confirm.setEnabled(bool(self.checked) if self.multiple else False)
        if self.multiple and self.records:self.status.setText(f'{len(self.records)} bookmarks. {len(self.checked)} selected. Search keeps your selection.')

    def select_visible(self):
        self.checked.update(self.visible_rows);self.filter()

    def clear_selection(self):
        self.checked.clear();self.filter()

    def choose_file(self):
        path,_=QFileDialog.getOpenFileName(self,'Choose bookmark file',filter='Bookmarks (Bookmarks *.json *.html *.htm *.sqlite *.db);;All files (*)')
        if not path:return
        self.catalog.append(BookmarkSource('Bookmark file',path.rsplit('/',1)[-1],Path(path)))
        if self.browser.findText('Bookmark file')<0:self.browser.addItem('Bookmark file')
        self.browser.setCurrentText('Bookmark file');self.populate_profiles();self.profile.setCurrentIndex(self.profile.count()-1)

    def selected(self):
        item=self.list.currentItem()
        return item.data(0,Qt.UserRole) if item and item.isSelected() else None

    def value(self):
        if self.multiple:
            if not self.checked:raise DocumentError('Select at least one bookmark.')
            return [{'Name':record['Name'],'Url':record['Url']} for index,record in enumerate(self.records) if index in self.checked]
        selected=self.selected()
        if not selected:raise DocumentError('Select a bookmark.')
        return {'Name':selected['Name'],'Url':selected['Url']}

    def accept(self):
        if bool(self.checked) if self.multiple else self.selected() is not None:super().accept()
