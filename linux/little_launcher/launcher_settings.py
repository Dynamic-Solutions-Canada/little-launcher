"""Launcher settings following Windows section/row geometry with desktop-native controls."""
from copy import deepcopy
from pathlib import Path

from PySide6.QtCore import QEvent, QRect, QSize, QSignalBlocker, Qt, QTimer, Signal
from PySide6.QtGui import QFont, QIcon, QPainter, QPalette
from PySide6.QtWidgets import (
    QCheckBox, QComboBox, QDialog, QFileDialog, QFrame, QHBoxLayout, QLabel, QLineEdit,
    QMessageBox, QPushButton, QScrollArea, QSizePolicy, QSpinBox, QStyle,
    QStyleOption, QStyleOptionToolButton, QToolButton, QVBoxLayout, QWidget,
)

from .document import DocumentError, normalize_web_address, shows_bookmark_bar, web_address
from .icons import LauncherIcons
from .icon_glyphs import PRESETS
from .launcher_card import LauncherCard
from .bookmark_picker import BookmarkPicker
from .icon_picker import IconPicker
from .anchor_picker import AnchorPicker, ANCHORS


def text_label(text, pixels=14, muted=False):
    label = QLabel(text)
    label.setTextFormat(Qt.PlainText)
    label.setWordWrap(True)
    font = QFont(label.font())
    font.setPixelSize(pixels)
    label.setFont(font)
    if muted:
        label.setForegroundRole(QPalette.PlaceholderText)
    return label


class SettingsRow(QWidget):
    """Label/subtitle at left, natural-height control at right; long addresses are stacked."""
    def __init__(self, title, subtitle, control, stacked=False, parent=None):
        super().__init__(parent)
        self.title = title
        self.control = control
        layout = QVBoxLayout(self) if stacked else QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8 if stacked else 12)
        labels = QWidget()
        labels.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        labels.setMinimumWidth(0)
        label_layout = QVBoxLayout(labels)
        label_layout.setContentsMargins(0, 0, 0, 0)
        label_layout.setSpacing(2 if stacked else 0)
        label_layout.addWidget(text_label(title))
        self.subtitle = text_label(subtitle, 12, True)
        self.subtitle.setMinimumWidth(0)
        self.subtitle.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        label_layout.addWidget(self.subtitle)
        if stacked:
            layout.addWidget(labels)
            layout.addWidget(control)
        else:
            layout.addWidget(labels, 1)
            layout.addWidget(control, 0, Qt.AlignVCenter | Qt.AlignRight)
        control.setAccessibleName(title)
        control.setToolTip(subtitle)


class SectionHeader(QToolButton):
    """Native expander hit target with left title and right disclosure arrow."""
    def __init__(self, title, parent=None):
        super().__init__(parent)
        self.setText(title)
        self.setAccessibleName(title)
        self.setCheckable(True)
        self.setAutoRaise(True)
        self.setFixedHeight(48)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)

    def paintEvent(self, event):
        option = QStyleOptionToolButton()
        self.initStyleOption(option)
        option.text = ''
        option.state &= ~QStyle.State_On
        option.icon = QIcon()
        painter = QPainter(self)
        self.style().drawComplexControl(QStyle.CC_ToolButton, option, painter, self)
        font = QFont(self.font())
        font.setPixelSize(14)
        painter.setFont(font)
        painter.setPen(self.palette().buttonText().color())
        painter.drawText(self.rect().adjusted(16, 0, -44, 0), Qt.AlignLeft | Qt.AlignVCenter, self.text())
        arrow = QStyleOption()
        arrow.initFrom(self)
        arrow.rect = QRect(self.width() - 28, (self.height() - 12) // 2, 12, 12)
        self.style().drawPrimitive(QStyle.PE_IndicatorArrowDown if self.isChecked() else QStyle.PE_IndicatorArrowRight,
                                   arrow, painter, self)


class LauncherDialog(QDialog):
    """A 560px launcher-specific form; item/bookmark dialogs keep their separate contracts."""
    profile_action_requested = Signal(str)

    def __init__(self, launcher, parent=None, controller=None, position_supported=False, page_icon=None):
        super().__init__(parent)
        self.position_supported = position_supported
        self.page_icon = page_icon
        self.page_icon_address = web_address(launcher)
        self.original = deepcopy(launcher)
        self.setWindowTitle('Launcher Settings')
        self.setWindowIcon(LauncherIcons.resolve(launcher))
        self.resize(560, 560)
        self.setMinimumWidth(400)
        self.section_widgets = {}
        self.rows = {}
        outer = QVBoxLayout(self)
        outer.setContentsMargins(24, 8, 24, 24)
        outer.setSpacing(0)
        self.form_scroll = QScrollArea()
        self.form_scroll.setFrameShape(QFrame.NoFrame)
        self.form_scroll.setWidgetResizable(True)
        self.form_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        body = QWidget()
        self.sections = QVBoxLayout(body)
        self.sections.setContentsMargins(0, 0, 0, 0)
        self.sections.setSpacing(12)
        self.sections.setAlignment(Qt.AlignTop)
        self.form_scroll.setWidget(body)
        outer.addWidget(self.form_scroll, 1)
        outer.addSpacing(16)
        footer = QHBoxLayout()
        footer.addStretch()
        self.done_button = QPushButton('Done')
        self.done_button.setAutoDefault(False)
        self.done_button.setDefault(True)
        self.done_button.setMinimumWidth(90)
        self.done_button.clicked.connect(self.accept)
        footer.addWidget(self.done_button)
        outer.addLayout(footer)

        self.controller = controller
        general = self.section('General')
        self.name = QLineEdit(launcher.get('Name', 'Launcher'))
        self.name.setPlaceholderText('Launcher name')
        self.name.setFixedWidth(160)
        self.name.installEventFilter(self)
        self.row(general, 'Name', 'Display name in tray icon tooltip', self.name)
        self.kind = QComboBox()
        self.kind.addItem('Shortcuts', 0)
        self.kind.addItem('Web page', 1)
        kind = launcher.get('Kind', 0)
        if self.kind.findData(kind) < 0:
            self.kind.addItem('Current type (preserved)', kind)
        self.kind.setCurrentIndex(self.kind.findData(kind))
        self.kind.setFixedWidth(160)
        self.row(general, 'Type', 'What the tray icon opens', self.kind)
        self.url = QLineEdit(web_address(launcher))
        self.url.setPlaceholderText('https://example.com')
        self.url.installEventFilter(self)
        address_controls=QWidget();address_layout=QHBoxLayout(address_controls);address_layout.setContentsMargins(0,0,0,0)
        address_layout.addWidget(self.url,1)
        from_browser=QPushButton('From browser…');from_browser.clicked.connect(self._pick_address);address_layout.addWidget(from_browser)
        self.address_row = self.row(general, 'Web Address', 'The page this launcher opens', address_controls, stacked=True)

        appearance = self.section('Appearance')
        self.icon_mode = QComboBox(self)
        self.icon_mode.hide()
        for mode in ['Composite', *LauncherIcons.COLORS, *PRESETS, 'Custom']:
            self.icon_mode.addItem(LauncherIcons.resolve({'TrayIconMode': mode}), mode, mode)
        mode = launcher.get('TrayIconMode', 'Composite')
        if self.icon_mode.findData(mode) < 0:
            self.icon_mode.addItem(LauncherIcons.resolve(launcher), 'Saved icon (preserved)', mode)
        self.icon_mode.setCurrentIndex(self.icon_mode.findData(mode))
        self.icon_mode.setFixedWidth(160)
        icon_controls = QWidget()
        icon_layout = QHBoxLayout(icon_controls)
        icon_layout.setContentsMargins(0, 0, 0, 0)
        choose_icon = QPushButton('Choose icon…')
        self.choose_icon = choose_icon
        choose_icon.setIconSize(QSize(28, 28))
        choose_icon.clicked.connect(self._pick_icon)
        icon_layout.addWidget(choose_icon)
        self.row(appearance, 'Icon', 'Choose this launcher’s tray icon', icon_controls)
        self.custom_icon = QLineEdit(launcher.get('CustomTrayIconPath', ''), self)
        self.custom_icon.hide()
        browse = QPushButton('Browse…')
        browse.clicked.connect(self._browse_icon)
        self.custom_row = self.row(appearance, 'Custom Icon', self._custom_name(), browse)
        self.custom_row.subtitle.setToolTip(self.custom_icon.text())
        self.show_tray = self.toggle(not launcher.get('NIconHide', False))
        self.row(appearance, 'Show in Tray', "Show this launcher's icon in the system tray", self.show_tray)
        self.view = QComboBox()
        for label, value in [('Icons', 0), ('Small Icons', 2), ('List', 1)]:
            self.view.addItem(label, value)
        view = launcher.get('ViewMode', 0)
        if self.view.findData(view) < 0:
            self.view.addItem('Current view (preserved)', view)
        self.view.setCurrentIndex(self.view.findData(view))
        self.view.setFixedWidth(160)
        self.view_row = self.row(appearance, 'View Mode', 'How items appear in the flyout popup', self.view)
        self.columns = QComboBox()
        for value in range(1, 13):
            self.columns.addItem(str(value), value)
        self.columns.setCurrentIndex(max(0, self.columns.findData(launcher.get('IconModeIconsPerRow', 3))))
        self.columns.setFixedWidth(100)
        self.columns_row = self.row(appearance, 'Icons Per Row', 'How many icons fit across each icon-mode column', self.columns)
        self.show_title = self.toggle(launcher.get('ShowTitle', False))
        self.title_row = self.row(appearance, 'Show Title', 'Show the launcher name at the top of the flyout', self.show_title)
        self.always_tabs = self.toggle(launcher.get('WebAlwaysShowTabs', False))
        self.tabs_row = self.row(appearance, 'Tab Bar', 'Keep the tab strip on screen with one page open. Off, it appears as soon as there is a second', self.always_tabs)
        self.address_bar = self.toggle(launcher.get('WebShowAddressBar', False))
        self.address_bar_row = self.row(appearance, 'Address Bar', 'Show the address of the current page and a bookmark button', self.address_bar)
        self.bookmark_bar = self.toggle(shows_bookmark_bar(launcher))
        self.bookmark_row = self.row(appearance, 'Bookmarks Bar', 'Show a bar of saved pages along the bottom of the flyout', self.bookmark_bar)

        self.bookmark_icons = self.toggle(launcher.get('WebBookmarkIconsOnly', False))
        self.bookmark_icons_row = self.row(appearance, 'Bookmark Icons Only', "Hides every bookmark's label, leaving just its icon.", self.bookmark_icons)

        size = self.section('Size and position')
        self.width_box = QSpinBox()
        self.width_box.setRange(320, 4000)
        self.width_box.setValue(launcher.get('WebFlyoutWidth') or 480)
        self.height_box = QSpinBox()
        self.height_box.setRange(240, 4000)
        self.height_box.setValue(launcher.get('WebFlyoutHeight') or 720)
        size_control = QWidget()
        size_layout = QHBoxLayout(size_control)
        size_layout.setContentsMargins(0, 0, 0, 0)
        size_layout.setSpacing(8)
        for control in (self.width_box, self.height_box):
            control.setFixedWidth(120)
        size_layout.addWidget(self.width_box)
        size_layout.addWidget(text_label('×', 12, True))
        size_layout.addWidget(self.height_box)
        self.row(size, 'Flyout Size', 'Width × height of the web flyout, in pixels', size_control)

        self.zoom = QComboBox()
        zoom = launcher.get('WebZoomPercent') or 100
        for value in sorted(set([50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, zoom])):
            self.zoom.addItem(str(value) + '%', value)
        self.zoom.setCurrentIndex(self.zoom.findData(zoom))
        self.zoom.setFixedWidth(160)
        self.row(size, 'Zoom', 'Page zoom inside the flyout, also on the flyout’s “…” menu', self.zoom)
        self.anchor = QComboBox()
        for label, value in ANCHORS:
            if value != 10 or position_supported:
                self.anchor.addItem(label, value)
        anchor = launcher.get('WebAnchor', 0)
        if self.anchor.findData(anchor) < 0:
            self.anchor.addItem('Saved position (preserved; not supported here yet)', anchor)
        self.anchor.setCurrentIndex(self.anchor.findData(anchor))
        self.anchor.setMinimumWidth(120);self.anchor.setMaximumWidth(200)
        self.anchor.setSizePolicy(QSizePolicy.Ignored,QSizePolicy.Fixed)
        anchor_control=QWidget();anchor_layout=QHBoxLayout(anchor_control);anchor_layout.setContentsMargins(0,0,0,0);anchor_layout.addWidget(self.anchor,1)
        self.anchor_picker_button=QPushButton('Choose…');self.anchor_picker_button.clicked.connect(self._pick_anchor);anchor_layout.addWidget(self.anchor_picker_button)
        self.row(size, 'Opens At', 'Where it opens on the screen holding its tray icon', anchor_control)
        self.remember_size = self.toggle(not launcher.get('WebLockSize', False))
        self.row(size, 'Remember Size Changes', 'Keep this flyout at the size you drag it to; otherwise it reopens at the size above', self.remember_size)

        behavior = self.section('Behavior')
        self.policy = QComboBox()
        for label, value in [('Unload when idle', 0), ('Stay loaded (suspended)', 1), ('Keep running', 2)]:
            self.policy.addItem(label, value)
        policy = launcher.get('WebHiddenPolicy', 0)
        if self.policy.findData(policy) < 0:
            self.policy.addItem('Current policy (preserved)', policy)
        self.policy.setCurrentIndex(self.policy.findData(policy))
        self.policy.setFixedWidth(200)
        self.row(behavior, 'When Hidden', 'Unloading frees memory; staying loaded reopens instantly', self.policy)
        self.idle = QSpinBox()
        self.idle.setRange(1, 720)
        self.idle.setValue(launcher.get('WebIdleUnloadMinutes') or 5)
        self.idle.setFixedWidth(120)
        self.idle_row = self.row(behavior, 'Unload After', 'Minutes the flyout may sit closed before the page is dropped', self.idle)

        self.reload_open = self.toggle(launcher.get('WebReloadOnShow', False))
        self.row(behavior, 'Reload On Open', 'Fetch the page again each time, instead of showing it as you left it', self.reload_open)
        self.links_external = self.toggle(launcher.get('WebLinksInBrowser', False))
        self.row(behavior, 'Open Links In Browser', 'Hand a link that opens a new window to your default browser, instead of opening it in a tab here', self.links_external)
        self.pin = self.toggle(launcher.get('WebPinFlyout', False))
        self.row(behavior, 'Pin Open', 'Stay on screen when you click elsewhere, instead of dismissing like a flyout', self.pin)
        self.regular = self.toggle(launcher.get('WebRegularWindow', False))
        self.row(behavior, 'Regular Window', 'Behave like an ordinary app window: taskbar button, Alt-Tab entry, and stays open until closed', self.regular)
        self.auto_hide = self.toggle(launcher.get('WebWindowAutoHide', False))
        self.auto_hide_row = self.row(behavior, 'Close On Focus Loss', 'Hide the regular window when you switch to another app', self.auto_hide)
        self.regular.toggled.connect(self.refresh_visibility)

        for title in ('Regular Window', 'Close On Focus Loss', 'Pin Open', 'Open Links In Browser', 'Reload On Open', 'When Hidden', 'Unload After'):
            row = self.rows[title]
            behavior.removeWidget(row)
            behavior.addWidget(row)

        self.fullscreen_window=self.toggle(launcher.get('WebFullScreenInWindow',False))
        self.row(behavior,'Fullscreen Fills Launcher','Keep page fullscreen inside this launcher instead of filling the display',self.fullscreen_window)
        self.fullscreen_video=self.toggle(launcher.get('WebFitFullscreenToVideo',False))
        self.fullscreen_video.setEnabled(self.fullscreen_window.isChecked())
        self.row(behavior,'Fit Fullscreen To Video','Fit the launcher to the video’s proportions while fullscreen stays inside it',self.fullscreen_video)
        self.fullscreen_window.toggled.connect(self.fullscreen_video.setEnabled)

        privacy = self.section('Privacy and sign-ins')
        self.profile = QComboBox()
        self.profile.addItem('Shared with other launchers', True)
        self.profile.addItem('Private to this launcher', False)
        self.profile.setCurrentIndex(self.profile.findData(launcher.get('WebSharedProfile', False)))
        self.profile.setFixedWidth(200)
        self.row(privacy, 'Sign-ins', 'Shared pools cookies with other launchers. Private keeps a separate account on this machine.', self.profile)

        clear = self.clear_data_button = QPushButton('Clear…')
        clear.clicked.connect(lambda: self._profile_action('clear-data'))
        self.row(privacy, 'Browsing Data', 'Clear cookies and site data on this machine. Shared sign-ins affect all launchers using that profile.', clear)
        reset = self.reset_permissions_button = QPushButton('Reset')
        reset.clicked.connect(lambda: self._profile_action('reset-permissions'))
        self.row(privacy, 'Site Permissions', 'Forget what you have allowed and blocked, so this profile’s pages ask again', reset)

        self.profile.currentIndexChanged.connect(self._refresh_profile_actions)
        self._refresh_profile_actions()
        self.kind.currentIndexChanged.connect(self.refresh_visibility)
        self.view.currentIndexChanged.connect(self.refresh_visibility)
        self.policy.currentIndexChanged.connect(self.refresh_visibility)
        self.icon_mode.currentIndexChanged.connect(self.refresh_visibility)
        self.refresh_visibility()
        self._fit_initial(body)
        self.live_conflict = False
        try:
            self._last_form = self.value()
        except DocumentError:
            self._last_form = None
        if self.controller is not None:
            for control in self.findChildren(QComboBox):
                control.currentIndexChanged.connect(self._commit_live)
            for control in self.findChildren(QSpinBox):
                control.valueChanged.connect(self._commit_live)
            for control in self.findChildren(QCheckBox):
                control.toggled.connect(self._commit_live)
            self.name.editingFinished.connect(self._commit_live)
            self.url.editingFinished.connect(self._commit_live)

    def _commit_live(self, *_):
        """Apply one completed control edit with the full-source preimage, as Windows does."""
        if self.controller is None:
            return True
        if self.live_conflict:
            return False
        try:
            value = self.value()
        except DocumentError:
            return False
        if value == self._last_form:
            return True
        if not self.controller.replace_launcher(value, self.original):
            self.live_conflict = True
            return False
        self.original = deepcopy(value)
        self._last_form = deepcopy(value)
        self.setWindowIcon(LauncherIcons.resolve(value))
        self._refresh_profile_actions()
        return True

    @staticmethod
    def toggle(checked):
        control = QCheckBox()
        control.setChecked(checked)
        control.setFixedWidth(40)
        control.setLayoutDirection(Qt.RightToLeft)
        return control

    def section(self, title):
        frame = LauncherCard()
        layout = QVBoxLayout(frame)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)
        header = SectionHeader(title)
        layout.addWidget(header)
        body = QWidget()
        rows = QVBoxLayout(body)
        rows.setContentsMargins(16, 12, 16, 16)
        rows.setSpacing(12)
        layout.addWidget(body)
        header.toggled.connect(body.setVisible)
        expanded = title not in ('Behavior', 'Privacy and sign-ins')
        header.setChecked(expanded)
        body.setVisible(expanded)
        self.sections.addWidget(frame)
        self.section_widgets[title] = (frame, header, body)
        return rows

    def _pick_address(self):
        dialog=BookmarkPicker(self)
        if dialog.exec()==QDialog.Accepted:
            self.url.setText(dialog.value()['Url'])
            self._commit_live()

    def _pick_icon(self):
        candidate = {**self.original, 'Kind':self.kind.currentData(), 'WebHomeUrl':self.url.text(), 'TrayIconMode': self.icon_mode.currentData(),
                     'CustomTrayIconPath': self.custom_icon.text()}
        dialog = IconPicker(candidate, self,cache_directory=self.controller.directory/'icons' if self.controller is not None else None,
                            page_icon=self.page_icon if self.url.text().strip()==self.page_icon_address else None)
        if dialog.exec() != QDialog.Accepted:
            return
        choice = dialog.value()
        mode = choice['TrayIconMode']
        with QSignalBlocker(self.icon_mode):
            index = self.icon_mode.findData(mode)
            if index < 0:
                self.icon_mode.addItem(LauncherIcons.resolve(choice), 'Emoji / text icon', mode)
                index = self.icon_mode.count() - 1
            self.icon_mode.setCurrentIndex(index)
        self.custom_icon.setText(choice['CustomTrayIconPath'])
        self.custom_row.subtitle.setText(self._custom_name())
        self.refresh_visibility()
        self._commit_live()

    def row(self, section, title, subtitle, control, stacked=False):
        row = SettingsRow(title, subtitle, control, stacked)
        section.addWidget(row)
        self.rows[title] = row
        return row

    def refresh_visibility(self):
        self.web = self.kind.currentData() == 1
        self.address_row.setVisible(self.web)
        for row in (self.view_row, self.title_row):
            row.setVisible(not self.web)
        self.columns_row.setVisible(not self.web and self.view.currentData() != 1)
        for row in (self.tabs_row, self.address_bar_row, self.bookmark_row, self.bookmark_icons_row):
            row.setVisible(self.web)
        for title in ('Size and position', 'Behavior', 'Privacy and sign-ins'):
            self.section_widgets[title][0].setVisible(self.web)
        self.auto_hide_row.setVisible(self.web and self.regular.isChecked())
        self.rows['Pin Open'].setEnabled(not self.regular.isChecked())
        self.rows['Pin Open'].setToolTip('Use the KDE window menu to keep a regular Wayland window above others.' if self.regular.isChecked() else '')
        self.custom_row.hide()
        self.choose_icon.setIcon(LauncherIcons.resolve({**self.original, 'TrayIconMode':self.icon_mode.currentData(), 'CustomTrayIconPath':self.custom_icon.text()}))
        self.idle_row.setVisible(self.policy.currentData() == 0)

    def _custom_name(self):
        path = self.custom_icon.text()
        return path.replace('\\', '/').rsplit('/', 1)[-1] if path else 'Choose an icon file'

    def _pick_anchor(self):
        picker=AnchorPicker(self.anchor.currentData(),self,position_supported=self.position_supported)
        if picker.exec()==QDialog.Accepted:
            index=self.anchor.findData(picker.selected)
            if index>=0:self.anchor.setCurrentIndex(index)

    def _browse_icon(self):
        path, _ = QFileDialog.getOpenFileName(self, 'Choose icon', filter='Images (*.png *.svg *.ico *.webp);;All files (*)')
        if path:
            self.custom_icon.setText(path)
            self.custom_row.subtitle.setText(self._custom_name())
            self._commit_live()

    def _fit_initial(self, body):
        self.ensurePolished()
        width = 560 - 48
        height = body.layout().totalHeightForWidth(width)
        if height < 0:
            height = body.sizeHint().height()
        self.resize(560, min(max(360, height + 90), self.screen().availableGeometry().height() - 40))

    def show_section(self, title):
        section = self.section_widgets.get(title) or self.section_widgets['General']
        frame, toggle, _ = section
        if frame.isHidden():
            frame, toggle, _ = self.section_widgets['General']
        toggle.setChecked(True)
        QTimer.singleShot(0, lambda: self.form_scroll.ensureWidgetVisible(frame))

    def _refresh_profile_actions(self):
        unchanged = self.profile.currentData() == self.original.get('WebSharedProfile', False)
        for button in (self.clear_data_button, self.reset_permissions_button):
            button.setEnabled(unchanged)
            button.setToolTip('Finish changing sign-ins with Done before changing this profile’s data.' if not unchanged else '')

    def _profile_action(self, action):
        if self.profile.currentData() != self.original.get('WebSharedProfile', False):
            return
        scope = 'all launchers with shared sign-ins' if self.original.get('WebSharedProfile') else 'this launcher'
        if action == 'clear-data' and QMessageBox.question(self, 'Clear browsing data',
                'Clear cookies and site data for ' + scope + '? This signs you out of websites on this machine.',
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No) != QMessageBox.Yes:
            return
        self.profile_action_requested.emit(action)

    def value(self):
        launcher = deepcopy(self.original)
        launcher.update(Name=self.name.text().strip() or 'Launcher', Kind=self.kind.currentData(),
                        NIconHide=not self.show_tray.isChecked(), TrayIconMode=self.icon_mode.currentData(),
                        CustomTrayIconPath=self.custom_icon.text())
        if launcher['Kind']==0 and self.original.get('Kind',0)==1:launcher['Platform']='linux'
        if launcher['CustomTrayIconPath'] != self.original.get('CustomTrayIconPath', ''):
            launcher.pop('CustomTrayIconData', None)
        if self.web:
            launcher.update(WebHomeUrl=normalize_web_address(self.url.text(), allow_empty=True),
                WebShowAddressBar=self.address_bar.isChecked(), WebAlwaysShowTabs=self.always_tabs.isChecked(),
                WebBookmarkIconsOnly=self.bookmark_icons.isChecked(), WebShowBookmarkBar=self.bookmark_bar.isChecked(), WebFlyoutWidth=self.width_box.value(),
                WebFlyoutHeight=self.height_box.value(), WebHiddenPolicy=self.policy.currentData(),
                WebIdleUnloadMinutes=self.idle.value(), WebPinFlyout=self.pin.isChecked(),
                WebSharedProfile=self.profile.currentData(), WebRegularWindow=self.regular.isChecked(),
                WebZoomPercent=self.zoom.currentData(), WebAnchor=self.anchor.currentData(),
                WebLockSize=not self.remember_size.isChecked(), WebReloadOnShow=self.reload_open.isChecked(),
                WebLinksInBrowser=self.links_external.isChecked(), WebWindowAutoHide=self.auto_hide.isChecked(),
                WebFullScreenInWindow=self.fullscreen_window.isChecked(), WebFitFullscreenToVideo=self.fullscreen_video.isChecked())
        else:
            launcher.update(ViewMode=self.view.currentData(), IconModeIconsPerRow=self.columns.currentData(),
                            ShowTitle=self.show_title.isChecked())
        return launcher

    def eventFilter(self, watched, event):
        if event.type() == QEvent.KeyPress and watched in (getattr(self, 'name', None), getattr(self, 'url', None)) and event.key() in (Qt.Key_Return, Qt.Key_Enter):
            watched.clearFocus()
            return True
        return super().eventFilter(watched, event)

    def accept(self):
        if self.controller is not None and self.live_conflict:
            QDialog.reject(self)
            return
        try:
            self.value()
        except DocumentError as error:
            QMessageBox.warning(self, 'Check these details', str(error))
            return
        if not self._commit_live():
            return
        super().accept()

    def reject(self):
        # Windows closes its settings form with Done, Escape or the title-bar close action.
        self.accept()
