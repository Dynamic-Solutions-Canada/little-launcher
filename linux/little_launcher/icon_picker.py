"""Pending native icon selection; Cancel never changes launcher data."""
from copy import deepcopy
import hashlib
import re
from pathlib import Path
from PySide6.QtCore import QSize, Qt, QTimer, QUrl, QBuffer, QIODevice
from PySide6.QtGui import QColor, QFont, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (QButtonGroup,QColorDialog,QDialog,QDialogButtonBox,QFileDialog,QGridLayout,QHBoxLayout,QLabel,QLineEdit,QPushButton,QSizePolicy,QMessageBox,QTabWidget,QToolButton,QVBoxLayout,QWidget,QScrollArea)
from .document import DocumentError
from .icon_glyphs import PRESETS,canonical_mode,glyph_icon,glyph_parts
from .icons import LauncherIcons
from .site_icon import SiteIconPreview, favicon_url, normalized_png
from .storage import atomic_write
from .document import web_address

EMOJI=['🏠','💬','📧','📅','✅','📋','📁','📄','🌐','🔍','⭐','❤️','📌','⚡','⚙️','🛠️','🖥️','📱','🎵','🎬','🎮','📷','🔒','🔑','🚀','☁️','☀️','🌙','🧠','📚','💼','📝','🛒','💡','🏡','👨\u200d👩\u200d👧\u200d👦']

EMOJI_NAMES=['Home','Chat messages','Email','Calendar','Check','Clipboard','Folder','Document','Globe web','Search','Star','Heart','Pin','Lightning','Settings','Tools','Computer','Phone','Music','Video','Games','Camera','Lock','Key','Rocket','Cloud','Sun','Moon','Brain','Books','Work','Notes','Shopping','Idea','House','Family']

class IconPicker(QDialog):
    def __init__(self,launcher,parent=None,*,item=None,cache_directory=None,page_icon=None):
        super().__init__(parent)
        self.cache_directory=Path(cache_directory) if cache_directory is not None else None
        self.site_png=None
        website=item.get('Path','') if item is not None and item.get('IsWebsite') else web_address(launcher) if item is None and launcher.get('Kind')==1 else ''
        self.site_address=website
        self.item=deepcopy(item) if item is not None else None
        if self.item is not None:
            glyph=self.item.get('IconGlyph','');color=self.item.get('IconColor','')
            mode=('Glyph:'+color+':'+glyph if color else 'Glyph:'+glyph) if glyph else 'Composite'
            launcher={'TrayIconMode':'Custom' if self.item.get('IconPath') else mode,'CustomTrayIconPath':self.item.get('IconPath','')}
        self.original=deepcopy(launcher);self.mode=canonical_mode(launcher.get('TrayIconMode','Composite'))
        self.path=launcher.get('CustomTrayIconPath','');self.color=None
        self.setWindowTitle('Choose icon');self.resize(420,544)
        if self.screen():
            area=self.screen().availableGeometry();self.resize(min(420,area.width()-48),min(544,area.height()-48))
        layout=QVBoxLayout(self);layout.setContentsMargins(12,12,12,12);layout.setSpacing(8)
        self.search=QLineEdit();self.search.setPlaceholderText('Search icons…');self.search.setClearButtonEnabled(True);self.search.setAccessibleName('Search icons');layout.addWidget(self.search)
        self.tabs=QTabWidget();layout.addWidget(self.tabs,1)
        self.buttons=QButtonGroup(self);self.buttons.setExclusive(True)
        self.tiles=[];self.search_tiles=[]
        def page(title):
            scroll=QScrollArea();scroll.setWidgetResizable(True);scroll.setFrameShape(QScrollArea.NoFrame);scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
            body=QWidget();body_layout=QVBoxLayout(body);body_layout.setContentsMargins(4,8,4,4);body_layout.setSpacing(8);scroll.setWidget(body)
            self.tabs.addTab(scroll,title);return body_layout
        def heading(target,text):
            label=QLabel(text);font=label.font();font.setBold(True);label.setFont(font);target.addWidget(label)
        def tile(target,label,mode,index,columns=5,symbol=False):
            button=QToolButton();button.setCheckable(True);button.setToolButtonStyle(Qt.ToolButtonIconOnly if symbol else Qt.ToolButtonTextUnderIcon)
            button.setText(label);button.setToolTip(label);button.setAccessibleName(label);button.setIcon(self.render_icon({**launcher,'TrayIconMode':mode}));button.setIconSize(QSize(28 if symbol else 36,28 if symbol else 36));button.setFixedSize(40 if symbol else 60,40 if symbol else 72)
            button.clicked.connect(lambda checked=False,value=mode:self.choose(value));self.buttons.addButton(button);self.tiles.append((button,mode));self.search_tiles.append((button,label.casefold()));target.addWidget(button,index//columns,index%columns)
        preset_layout=page('Launcher presets' if self.item is None else 'App icons')
        heading(preset_layout,'Launcher icons' if self.item is None else 'App icons')
        grid=QGridLayout();grid.setSpacing(4);grid.setAlignment(Qt.AlignTop|Qt.AlignLeft);preset_layout.addLayout(grid)
        choices=[(mode,mode) for mode in ([] if launcher.get('Kind')==1 else ['Composite'])+[*LauncherIcons.COLORS,*PRESETS]] if self.item is None else [('Automatic','Composite'),*[(name,'Glyph:'+glyph) for name,(glyph,_) in PRESETS.items()]]
        for index,(label,mode) in enumerate(choices):tile(grid,label,mode,index)
        try:site_available=self.cache_directory is not None and bool(favicon_url(website))
        except DocumentError:site_available=False
        self.site_fetch=None
        if site_available:
            heading(preset_layout,'Website icon')
            page_png=normalized_png(page_icon) if page_icon is not None else None
            self.page_button=QPushButton('Use website icon');self.page_button.setCheckable(True);self.page_button.setEnabled(bool(page_png))
            self.page_button.setToolTip('Use the icon from the loaded launcher page.' if page_png else 'Open this launcher and let its website load, then reopen the chooser.')
            if page_png:
                image=QPixmap();image.loadFromData(page_png);self.page_button.setIcon(QIcon(image));self.page_button.setIconSize(QSize(32,32));self.page_button.clicked.connect(lambda:self.site_ready(page_png))
            preset_layout.addWidget(self.page_button)
            self.fetch_button=QPushButton('Fetch website icon');self.fetch_button.clicked.connect(self.fetch_site);preset_layout.addWidget(self.fetch_button)
            self.site_status=QLabel('Choose the loaded website icon, or fetch a public icon.');self.site_status.setWordWrap(True);preset_layout.addWidget(self.site_status)
            self.site_tab=0
            self.site_fetch=SiteIconPreview(self);self.site_fetch.ready.connect(self.site_ready);self.site_fetch.failed.connect(self.site_failed)
            self.finished.connect(lambda *_:self.site_fetch.cancel())
        self.browse_button=QPushButton('Browse for image…');self.browse_button.clicked.connect(self.browse);self.browse_button.setCheckable(True);preset_layout.addWidget(self.browse_button)
        self.file_label=QLabel();self.file_label.setWordWrap(True);self.file_label.setTextFormat(Qt.PlainText);self.file_label.setMinimumWidth(0);self.file_label.setSizePolicy(QSizePolicy.Ignored,QSizePolicy.Preferred);preset_layout.addWidget(self.file_label);preset_layout.addStretch()
        symbols=page('Symbols');heading(symbols,'Icon color')
        palette=QHBoxLayout();palette.setSpacing(4);self.swatches=[]
        for color in [None,'#e74856','#ff8c00','#f9c846','#16a765','#0078d4','#886ce4']:
            button=QToolButton();button.setText('A' if color is None else '●');button.setFixedSize(28,28);button.setCheckable(True);button.setAccessibleName('Default color' if color is None else color);button.setToolTip(button.accessibleName())
            if color:
                swatch=QPixmap(20,20);swatch.fill(Qt.transparent)
                painter=QPainter(swatch);painter.setRenderHint(QPainter.Antialiasing);painter.setPen(Qt.NoPen);painter.setBrush(QColor(color));painter.drawEllipse(2,2,16,16);painter.end()
                button.setText('');button.setIcon(QIcon(swatch));button.setIconSize(QSize(20,20))
            button.clicked.connect(lambda checked=False,value=color:self.set_color(value));palette.addWidget(button);self.swatches.append((button,color))
        palette.addStretch();symbols.addLayout(palette)
        heading(symbols,'Symbols');symbol_grid=QGridLayout();symbol_grid.setAlignment(Qt.AlignTop|Qt.AlignLeft);symbol_grid.setSpacing(4);symbols.addLayout(symbol_grid)
        for index,(name,(glyph,_)) in enumerate(PRESETS.items()):tile(symbol_grid,name,'Glyph:'+glyph,index,8,True)
        self.text=QLineEdit();self.text.setMaxLength(32);self.text.setPlaceholderText('Type or paste an icon character');self.text.setAccessibleName('Icon character')
        row=QHBoxLayout();row.addWidget(self.text,1);self.color_button=QPushButton('Color…');self.color_button.clicked.connect(self.choose_color);row.addWidget(self.color_button);symbols.addLayout(row);symbols.addStretch()
        emoji_layout=page('Emoji');heading(emoji_layout,'Emoji');emoji_grid=QGridLayout();emoji_grid.setSpacing(4);emoji_grid.setAlignment(Qt.AlignTop|Qt.AlignLeft);emoji_layout.addLayout(emoji_grid)
        for index,glyph in enumerate(EMOJI):tile(emoji_grid,EMOJI_NAMES[index]+' '+glyph,'Glyph:'+glyph,index,8,True)
        emoji_layout.addStretch()
        self.selfhst_service=None;self.selfhst_entries=[];self.selfhst_buttons={};self.selfhst_selected=None;self.selfhst_request=None
        if self.cache_directory is not None:
            from .selfhst import SelfhStCatalog
            catalog_tab=QWidget();catalog_layout=QVBoxLayout(catalog_tab);catalog_layout.setContentsMargins(4,8,4,4)
            self.selfhst_search=self.search
            self.selfhst_status=QLabel('Choose this tab to load the selfh.st icon catalog.');self.selfhst_status.setWordWrap(True);catalog_layout.addWidget(self.selfhst_status)
            scroll=QScrollArea();scroll.setWidgetResizable(True);scroll.setFrameShape(QScrollArea.NoFrame);scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff);catalog_layout.addWidget(scroll,1);self.selfhst_scroll=scroll;self.selfhst_grid_widget=QWidget();self.selfhst_grid=QGridLayout(self.selfhst_grid_widget);self.selfhst_grid.setContentsMargins(0,0,0,0);self.selfhst_grid.setAlignment(Qt.AlignTop|Qt.AlignLeft);self.selfhst_grid.setSpacing(4);scroll.setWidget(self.selfhst_grid_widget)
            footer=QLabel('<a href="https://selfh.st/icons/">Icons by selfh.st</a> · <a href="https://github.com/selfhst/icons/blob/main/LICENSE">CC BY 4.0</a>');footer.setOpenExternalLinks(True);catalog_layout.addWidget(footer)
            self.selfhst_retry=QPushButton('Retry catalog');self.selfhst_retry.clicked.connect(lambda:self.selfhst_service.load(force=True));self.selfhst_retry.hide();catalog_layout.addWidget(self.selfhst_retry,0,Qt.AlignRight)
            self.selfhst_tab=self.tabs.addTab(catalog_tab,'selfh.st')
            self.selfhst_service=SelfhStCatalog(self.cache_directory/'selfhst',self)
            self.selfhst_service.loaded.connect(self.selfhst_loaded);self.selfhst_service.icon_ready.connect(self.selfhst_icon_ready);self.selfhst_service.icon_failed.connect(self.selfhst_icon_failed);self.selfhst_service.failed.connect(self.selfhst_failed)
            self.selfhst_timer=QTimer(self);self.selfhst_timer.setSingleShot(True);self.selfhst_timer.timeout.connect(self.selfhst_filter)
            self.finished.connect(lambda *_:self.selfhst_service.close())
        self.preview=QLabel();self.preview.setFixedSize(32,32)
        self.status=QLabel();self.status.setWordWrap(True);layout.addWidget(self.status)
        footer=QHBoxLayout();footer.addWidget(self.preview);footer.addStretch()
        buttons=QDialogButtonBox(QDialogButtonBox.Ok|QDialogButtonBox.Cancel);self.confirm=buttons.button(QDialogButtonBox.Ok);self.confirm.setText('Confirm');self.confirm.setDefault(True)
        buttons.accepted.connect(self.accept);buttons.rejected.connect(self.reject);footer.addWidget(buttons);layout.addLayout(footer)
        parts=glyph_parts(self.mode)
        if parts:
            self.text.setText(parts[0]);self.color=parts[1];self.tabs.setCurrentIndex(2 if parts[0] in EMOJI else 1)
        self.text.textEdited.connect(self.text_changed);self.search.textChanged.connect(self.filter_icons)
        self.tabs.currentChanged.connect(self.tab_changed)
        if self.selfhst_service is not None and self.mode=='Custom' and Path(self.path).parent==self.cache_directory:
            match=re.fullmatch(r'selfhst-([a-z0-9][a-z0-9-]*)-[0-9a-f]{64}\.png',Path(self.path).name)
            if match:self.selfhst_selected=match[1];self.tabs.setCurrentIndex(self.selfhst_tab)
        self.refresh()

    def showEvent(self,event):
        super().showEvent(event);QTimer.singleShot(0,self.reveal_selection)

    def reveal_selection(self):
        if not self.isVisible():return
        if self.selfhst_service is not None and self.tabs.currentIndex()==self.selfhst_tab:
            button=self.selfhst_buttons.get(self.selfhst_selected);scroll=self.selfhst_scroll
        else:
            scroll=self.tabs.currentWidget();button=next((button for button,_ in self.tiles if button.isChecked() and scroll.isAncestorOf(button)),None)
            if button is None and self.tabs.currentIndex()==0:button=self.page_button if self.mode=='Site' and hasattr(self,'page_button') else self.browse_button if self.mode=='Custom' else None
        if button is not None and isinstance(scroll,QScrollArea):scroll.ensureWidgetVisible(button)

    def set_color(self,color):
        self.color=color
        if self.text.text():self.text_changed()
        else:self.refresh()

    def filter_icons(self):
        query=self.search.text().strip().casefold()
        for button,name in self.search_tiles:button.setVisible(not query or query in name)
        if self.selfhst_service is not None and self.tabs.currentIndex()==self.selfhst_tab:self.selfhst_timer.start(180)

    def tab_changed(self,index):
        if self.selfhst_service is not None:self.selfhst_tab_changed(index)
        self.filter_icons()

    def choose(self,mode):
        self.cancel_site()
        parts=glyph_parts(mode)
        if parts and parts[1] is None and self.color and parts[0] in [value[0] for value in PRESETS.values()]:mode='Glyph:'+self.color+':'+parts[0]
        self.mode=mode
        parts=glyph_parts(mode)
        if parts:self.text.setText(parts[0]);self.color=parts[1]
        self.refresh()

    def text_changed(self,*_):
        self.cancel_site()
        prefix=('Glyph:'+self.color+':') if self.color else 'Glyph:'
        self.mode=prefix+self.text.text();self.refresh()

    def choose_color(self):
        color=QColorDialog.getColor(QColor(self.color or '#3daee9'),self,'Choose icon color')
        if color.isValid():self.color=color.name();self.text_changed()

    def browse(self):
        path,_=QFileDialog.getOpenFileName(self,'Choose icon image',filter='Images (*.png *.svg *.ico *.jpg *.jpeg *.webp);;All files (*)')
        if path:self.cancel_site();self.path=path;self.mode='Custom';self.refresh()

    def refresh(self):
        candidate={**self.original,'TrayIconMode':self.mode,'CustomTrayIconPath':self.path}
        self.preview.setPixmap(self.render_icon(candidate).pixmap(32,32))
        self.file_label.setText(Path(self.path).name if self.path else 'Choose an image file.');self.file_label.setToolTip(self.path)
        available=(self.mode in ('Site','SelfhSt') and bool(self.site_png) or self.mode in ['Composite',*LauncherIcons.COLORS,*PRESETS] or
                   self.mode=='Custom' and Path(self.path).is_file() and not QIcon(self.path).isNull() or glyph_icon(self.mode) is not None)
        self.confirm.setEnabled(bool(available))
        self.status.setText('Choose an icon, then Confirm.' if available else 'This saved icon is not available on this device. Choose another icon or image; Cancel keeps the saved choice.')
        self.status.setVisible(not available)
        self.browse_button.setChecked(self.mode=='Custom')
        if hasattr(self,'page_button'):self.page_button.setChecked(self.mode=='Site')
        for button,color in self.swatches:button.setChecked(color==self.color)
        self.color_button.setEnabled(bool(self.text.text().strip()))
        parts=glyph_parts(self.mode)
        self.buttons.setExclusive(False)
        for button,mode in self.tiles:
            other=glyph_parts(mode)
            if other and other[0] in [value[0] for value in PRESETS.values()]:button.setIcon(glyph_icon(mode,color_override=self.color))
            button.setChecked(mode==self.mode or bool(parts and other and parts[0]==other[0]))
        self.buttons.setExclusive(True)

    def render_icon(self,candidate):
        if candidate['TrayIconMode'] in ('Site','SelfhSt') and self.site_png:
            pixmap=QPixmap();pixmap.loadFromData(self.site_png,'PNG');return QIcon(pixmap)
        if self.item is None:return LauncherIcons.resolve(candidate)
        from .desktop import item_icon
        return item_icon({**self.item,**self.item_fields(candidate['TrayIconMode'],candidate.get('CustomTrayIconPath',''))})

    def item_fields(self,mode,path):
        if mode=='Custom':return {'IconPath':path,'IconGlyph':self.item.get('IconGlyph',''),'IconColor':self.item.get('IconColor','')}
        parts=glyph_parts(mode)
        return {'IconPath':'','IconGlyph':parts[0] if parts else '', 'IconColor':parts[1] or '' if parts else ''}

    def cancel_site(self):
        self.selfhst_request=None
        if self.site_fetch and self.site_fetch.reply:
            self.site_fetch.cancel();self.fetch_button.setEnabled(True)
            self.site_status.setText('Fetch canceled. Your chosen icon is kept.')

    def fetch_site(self):
        self.fetch_button.setEnabled(False);self.site_status.setText('Fetching public website icon…')
        self.site_fetch.fetch(self.site_address)

    def site_ready(self,png):
        self.fetch_button.setEnabled(True);self.site_png=png;self.mode='Site'
        self.site_status.setText('Preview ready. Confirm to use this image, or Cancel to keep the saved icon.')
        self.refresh()

    def site_failed(self,message):
        self.fetch_button.setEnabled(True);self.site_status.setText(message)

    def value(self):
        if not self.confirm.isEnabled():raise DocumentError('Choose an available icon.')
        if self.mode in ('Site','SelfhSt'):raise DocumentError('Confirm the website icon before applying it.')
        if self.item is not None:return self.item_fields(self.mode,self.path)
        return {'TrayIconMode':self.mode,'CustomTrayIconPath':self.path if self.mode=='Custom' else self.original.get('CustomTrayIconPath','')}

    def accept(self):
        if self.confirm.isEnabled():
            raster_choice=self.mode in ('Site','SelfhSt')
            if self.mode in ('Site','SelfhSt'):
                path=self.cache_directory/(('selfhst-'+self.selfhst_selected+'-' if self.mode=='SelfhSt' else 'site-choice-')+hashlib.sha256(self.site_png).hexdigest()+'.png')
                try:atomic_write(path,self.site_png)
                except OSError:
                    QMessageBox.warning(self,'Icon could not be saved','The selected website icon could not be saved. Your existing icon is unchanged.');return
                self.path=str(path);self.mode='Custom'
            if self.mode=='Custom' and not raster_choice and self.cache_directory is not None and self.path!=self.original.get('CustomTrayIconPath',''):
                try:
                    if Path(self.path).stat().st_size>1024*1024:raise OSError('Image exceeds 1 MiB')
                    image=QIcon(self.path).pixmap(256,256);buffer=QBuffer();buffer.open(QIODevice.WriteOnly)
                    if image.isNull()or not image.save(buffer,'PNG'):raise OSError('Image cannot be decoded')
                    raw=bytes(buffer.data());path=self.cache_directory/('chosen-'+hashlib.sha256(raw).hexdigest()+'.png');atomic_write(path,raw);self.path=str(path)
                except OSError:
                    QMessageBox.warning(self,'Icon could not be saved','Choose a supported image smaller than 1 MiB. The existing icon is unchanged.');return
            super().accept()

    def selfhst_failed(self,message):
        self.selfhst_status.setText(message);self.selfhst_retry.show()

    def selfhst_loaded(self,entries):
        self.selfhst_retry.hide()
        self.selfhst_entries=entries;self.selfhst_filter()

    def selfhst_filter(self):
        self.selfhst_timer.stop();self.selfhst_request=None
        query=self.selfhst_search.text().strip().casefold()
        matches=[entry for entry in self.selfhst_entries if not query or any(query in entry[key].casefold() for key in ('Name','Reference','Tags','Category'))]
        visible=matches[:120]
        saved=next((entry for entry in matches if entry['Reference']==self.selfhst_selected),None)
        if saved and saved not in visible:visible=visible[:119]+[saved]
        self.selfhst_buttons={}
        while self.selfhst_grid.count():
            item=self.selfhst_grid.takeAt(0)
            if item.widget():item.widget().deleteLater()
        self.selfhst_service.prioritize({entry['Reference']for entry in visible})
        for index,entry in enumerate(visible):
            ref=entry['Reference'];button=QToolButton();button.setText(button.fontMetrics().elidedText(entry['Name'],Qt.ElideRight,68));placeholder=QPixmap(32,32);placeholder.fill(Qt.transparent);button.setIcon(QIcon(placeholder));button.setToolTip(entry['Name']);button.setAccessibleName(entry['Name']);button.setToolButtonStyle(Qt.ToolButtonTextUnderIcon);button.setIconSize(QSize(32,32));button.setFixedSize(84,88);button.setCheckable(True);button.setChecked(ref==self.selfhst_selected)
            button.clicked.connect(lambda checked=False,reference=ref:self.choose_selfhst(reference));self.selfhst_buttons[ref]=button;self.selfhst_grid.addWidget(button,index//4,index%4)
            raw=self.selfhst_service.icon(ref)
            if raw:self.selfhst_icon_ready(ref,raw)
        self.selfhst_status.setText(f"Showing {len(visible)} of {len(matches)} icons. Search to narrow the list.")
        QTimer.singleShot(0,self.reveal_selection)

    def selfhst_icon_ready(self,reference,raw):
        button=self.selfhst_buttons.get(reference)
        if button:
            pixmap=QPixmap();pixmap.loadFromData(raw,'PNG');button.setIcon(QIcon(pixmap.scaled(64,64,Qt.KeepAspectRatio,Qt.SmoothTransformation)))
        if self.selfhst_request==reference:
            self.selfhst_selected=reference;self.site_png=raw;self.mode='SelfhSt'
            for ref,tile in self.selfhst_buttons.items():tile.setChecked(ref==reference)
            self.selfhst_status.setText('Selected: '+next((entry['Name']for entry in self.selfhst_entries if entry['Reference']==reference),reference));self.refresh()

    def selfhst_icon_failed(self,reference):
        if self.selfhst_request==reference:
            self.selfhst_request=None;self.selfhst_status.setText('That icon could not load. Your previous selection is kept. Try it again or choose another icon.')

    def selfhst_tab_changed(self,index):
        self.selfhst_request=None
        if index==self.selfhst_tab:
            self.selfhst_status.setText('Loading selfh.st icons…');self.selfhst_service.load()

    def choose_selfhst(self,reference):
        self.cancel_site();self.selfhst_request=reference
        for ref,button in self.selfhst_buttons.items():button.setChecked(ref==self.selfhst_selected)
        raw=self.selfhst_service.icon(reference,priority=True)
        if raw:self.selfhst_icon_ready(reference,raw)
        else:self.selfhst_status.setText('Loading icon… Your previous selection is kept until it is ready.')
