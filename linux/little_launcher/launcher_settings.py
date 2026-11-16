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

from . import i18n
from .document import DocumentError, normalize_web_address, shows_bookmark_bar, web_address
from .icons import LauncherIcons
from .icon_glyphs import PRESETS
from .launcher_card import LauncherCard
from .bookmark_picker import BookmarkPicker
from .icon_picker import IconPicker
from .anchor_picker import AnchorPicker


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
        self.setWindowTitle(i18n.t('launcherSettings.title'))
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
        self.done_button = QPushButton(i18n.t('common.done'))
        self.done_button.setAutoDefault(False)
        self.done_button.setDefault(True)
        self.done_button.setMinimumWidth(90)
        self.done_button.clicked.connect(self.accept)
        footer.addWidget(self.done_button)
        outer.addLayout(footer)

        self.controller = controller
        general = self.section('General', 'launcherSettings.section.general')
        self.name = QLineEdit(launcher.get('Name', 'Launcher'))
        self.name.setPlaceholderText(i18n.t('launcherSettings.name.placeholder'))
        self.name.setFixedWidth(160)
        self.name.installEventFilter(self)
        self.row(general, i18n.t('common.name'), i18n.t('launcherSettings.name.description'), self.name)
        self.kind = QComboBox()
        self.kind.addItem(i18n.t('launcherSettings.type.shortcuts'), 0)
        self.kind.addItem(i18n.t('launcherSettings.type.web'), 1)
        kind = launcher.get('Kind', 0)
        if self.kind.findData(kind) < 0:
            self.kind.addItem(i18n.t('launcherSettings.type.preserved'), kind)
        self.kind.setCurrentIndex(self.kind.findData(kind))
        self.kind.setFixedWidth(160)
        self.row(general, i18n.t('launcherSettings.type.title'), i18n.t('launcherSettings.type.description'), self.kind)
        self.url = QLineEdit(web_address(launcher))
        self.url.setPlaceholderText('https://example.com')
        self.url.installEventFilter(self)
        address_controls=QWidget();address_layout=QHBoxLayout(address_controls);address_layout.setContentsMargins(0,0,0,0)
        address_layout.addWidget(self.url,1)
        from_browser=QPushButton(i18n.t('launcherSettings.webAddress.fromBrowser'));from_browser.clicked.connect(self._pick_address);address_layout.addWidget(from_browser)
        self.address_row = self.row(general, i18n.t('launcherSettings.webAddress.title'), i18n.t('launcherSettings.webAddress.description'), address_controls, stacked=True)

        appearance = self.section('Appearance', 'launcherSettings.section.appearance')
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
        choose_icon = QPushButton(i18n.t('launcherSettings.icon.choose'))
        self.choose_icon = choose_icon
        choose_icon.setIconSize(QSize(28, 28))
        choose_icon.clicked.connect(self._pick_icon)
        icon_layout.addWidget(choose_icon)
        self.row(appearance, i18n.t('common.icon'), i18n.t('launcherSettings.icon.description'), icon_controls)
        self.custom_icon = QLineEdit(launcher.get('CustomTrayIconPath', ''), self)
        self.custom_icon.hide()
        browse = QPushButton(i18n.t('common.browse'))
        browse.clicked.connect(self._browse_icon)
        self.custom_row = self.row(appearance, i18n.t('launcherSettings.customIcon.title'), self._custom_name(), browse)
        self.custom_row.subtitle.setToolTip(self.custom_icon.text())
        self.show_tray = self.toggle(not launcher.get('NIconHide', False))
        self.row(appearance, i18n.t('launcherSettings.showInTray.title'), i18n.t('launcherSettings.showInTray.description'), self.show_tray)
        self.view = QComboBox()
        for label, value in [('launcherSettings.viewMode.icons', 0), ('launcherSettings.viewMode.smallIcons', 2), ('launcherSettings.viewMode.list', 1)]:
            self.view.addItem(i18n.t(label), value)
        view = launcher.get('ViewMode', 0)
        if self.view.findData(view) < 0:
            self.view.addItem(i18n.t('launcherSettings.viewMode.preserved'), view)
        self.view.setCurrentIndex(self.view.findData(view))
        self.view.setFixedWidth(160)
        self.view_row = self.row(appearance, i18n.t('launcherSettings.viewMode.title'), i18n.t('launcherSettings.viewMode.description'), self.view)
        self.columns = QComboBox()
        for value in range(1, 13):
            self.columns.addItem(str(value), value)
        self.columns.setCurrentIndex(max(0, self.columns.findData(launcher.get('IconModeIconsPerRow', 3))))
        self.columns.setFixedWidth(100)
        self.columns_row = self.row(appearance, i18n.t('launcherSettings.iconsPerRow.title'), i18n.t('launcherSettings.iconsPerRow.description'), self.columns)
        self.show_title = self.toggle(launcher.get('ShowTitle', False))
        self.title_row = self.row(appearance, i18n.t('launcherSettings.showTitle.title'), i18n.t('launcherSettings.showTitle.description'), self.show_title)
        self.always_tabs = self.toggle(launcher.get('WebAlwaysShowTabs', False))
        self.tabs_row = self.row(appearance, i18n.t('launcherSettings.tabBar.title'), i18n.t('launcherSettings.tabBar.description'), self.always_tabs)
        self.address_bar = self.toggle(launcher.get('WebShowAddressBar', False))
        self.address_bar_row = self.row(appearance, i18n.t('launcherSettings.addressBar.title'), i18n.t('launcherSettings.addressBar.description'), self.address_bar)
        self.bookmark_bar = self.toggle(shows_bookmark_bar(launcher))
        self.bookmark_row = self.row(appearance, i18n.t('launcherSettings.bookmarksBar.title'), i18n.t('launcherSettings.bookmarksBar.description'), self.bookmark_bar)

        self.bookmark_icons = self.toggle(launcher.get('WebBookmarkIconsOnly', False))
        self.bookmark_icons_row = self.row(appearance, i18n.t('launcherSettings.bookmarkIconsOnly.title'), i18n.t('launcherSettings.bookmarkIconsOnly.description'), self.bookmark_icons)

        size = self.section('Size and position', 'launcherSettings.section.sizeAndPosition')
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
        self.row(size, i18n.t('launcherSettings.flyoutSize.title'), i18n.t('launcherSettings.flyoutSize.description'), size_control)

        self.zoom = QComboBox()
        zoom = launcher.get('WebZoomPercent') or 100
        for value in sorted(set([50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, zoom])):
            self.zoom.addItem(i18n.t('launcherSettings.zoom.percent', percent=value), value)
        self.zoom.setCurrentIndex(self.zoom.findData(zoom))
        self.zoom.setFixedWidth(160)
        self.row(size, i18n.t('launcherSettings.zoom.title'), i18n.t('launcherSettings.zoom.description'), self.zoom)
        self.anchor = QComboBox()
        for label, value in AnchorPicker.choices():
            if value != 10 or position_supported:
                self.anchor.addItem(label, value)
        anchor = launcher.get('WebAnchor', 0)
        if self.anchor.findData(anchor) < 0:
            self.anchor.addItem(i18n.t('launcherSettings.opensAt.preserved'), anchor)
        self.anchor.setCurrentIndex(self.anchor.findData(anchor))
        self.anchor.setMinimumWidth(120);self.anchor.setMaximumWidth(200)
        self.anchor.setSizePolicy(QSizePolicy.Ignored,QSizePolicy.Fixed)
        anchor_control=QWidget();anchor_layout=QHBoxLayout(anchor_control);anchor_layout.setContentsMargins(0,0,0,0);anchor_layout.addWidget(self.anchor,1)
        self.anchor_picker_button=QPushButton(i18n.t('launcherSettings.opensAt.choose'));self.anchor_picker_button.clicked.connect(self._pick_anchor);anchor_layout.addWidget(self.anchor_picker_button)
        self.row(size, i18n.t('launcherSettings.opensAt.title'), i18n.t('launcherSettings.opensAt.screenDescription'), anchor_control)
        self.remember_size = self.toggle(not launcher.get('WebLockSize', False))
        self.row(size, i18n.t('launcherSettings.rememberSize.title'), i18n.t('launcherSettings.rememberSize.description'), self.remember_size)

        behavior = self.section('Behavior', 'launcherSettings.section.behavior')
        self.policy = QComboBox()
        for label, value in [('launcherSettings.hiddenPolicy.unloadWhenIdle', 0), ('launcherSettings.hiddenPolicy.suspend', 1), ('launcherSettings.hiddenPolicy.keepRunning', 2)]:
            self.policy.addItem(i18n.t(label), value)
        policy = launcher.get('WebHiddenPolicy', 0)
        if self.policy.findData(policy) < 0:
            self.policy.addItem(i18n.t('launcherSettings.hiddenPolicy.preserved'), policy)
        self.policy.setCurrentIndex(self.policy.findData(policy))
        self.policy.setFixedWidth(200)
        self.row(behavior, i18n.t('launcherSettings.hiddenPolicy.title'), i18n.t('launcherSettings.hiddenPolicy.description'), self.policy, key='When Hidden')
        self.idle = QSpinBox()
        self.idle.setRange(1, 720)
        self.idle.setValue(launcher.get('WebIdleUnloadMinutes') or 5)
        self.idle.setFixedWidth(120)
        self.idle_row = self.row(behavior, i18n.t('launcherSettings.unloadAfter.title'), i18n.t('launcherSettings.unloadAfter.description'), self.idle, key='Unload After')

        self.reload_open = self.toggle(launcher.get('WebReloadOnShow', False))
        self.row(behavior, i18n.t('launcherSettings.reloadOnOpen.title'), i18n.t('launcherSettings.reloadOnOpen.description'), self.reload_open, key='Reload On Open')
        self.links_external = self.toggle(launcher.get('WebLinksInBrowser', False))
        self.row(behavior, i18n.t('launcherSettings.openLinksInBrowser.title'), i18n.t('launcherSettings.openLinksInBrowser.description'), self.links_external, key='Open Links In Browser')
        self.pin = self.toggle(launcher.get('WebPinFlyout', False))
        self.row(behavior, i18n.t('launcherSettings.pinOpen.title'), i18n.t('launcherSettings.pinOpen.description'), self.pin, key='Pin Open')
        self.regular = self.toggle(launcher.get('WebRegularWindow', False))
        self.row(behavior, i18n.t('launcherSettings.regularWindow.title'), i18n.t('launcherSettings.regularWindow.description'), self.regular, key='Regular Window')
        self.auto_hide = self.toggle(launcher.get('WebWindowAutoHide', False))
        self.auto_hide_row = self.row(behavior, i18n.t('launcherSettings.closeOnFocusLoss.title'), i18n.t('launcherSettings.closeOnFocusLoss.description'), self.auto_hide, key='Close On Focus Loss')
        self.regular.toggled.connect(self.refresh_visibility)

        for title in ('Regular Window', 'Close On Focus Loss', 'Pin Open', 'Open Links In Browser', 'Reload On Open', 'When Hidden', 'Unload After'):
            row = self.rows[title]
            behavior.removeWidget(row)
            behavior.addWidget(row)

        self.fullscreen_window=self.toggle(launcher.get('WebFullScreenInWindow',False))
        self.row(behavior,i18n.t('launcherSettings.fullscreenFillsLauncher.title'),i18n.t('launcherSettings.fullscreenFillsLauncher.description'),self.fullscreen_window)
        self.fullscreen_video=self.toggle(launcher.get('WebFitFullscreenToVideo',False))
        self.fullscreen_video.setEnabled(self.fullscreen_window.isChecked())
        self.row(behavior,i18n.t('launcherSettings.fitFullscreenToVideo.title'),i18n.t('launcherSettings.fitFullscreenToVideo.description'),self.fullscreen_video)
        self.fullscreen_window.toggled.connect(self.fullscreen_video.setEnabled)

        privacy = self.section('Privacy and sign-ins', 'launcherSettings.section.privacyAndSignIns')
        self.profile = QComboBox()
        self.profile.addItem(i18n.t('launcherSettings.signIns.shared'), True)
        self.profile.addItem(i18n.t('launcherSettings.signIns.private'), False)
        self.profile.setCurrentIndex(self.profile.findData(launcher.get('WebSharedProfile', False)))
        self.profile.setFixedWidth(200)
        self.row(privacy, i18n.t('launcherSettings.signIns.title'), i18n.t('launcherSettings.signIns.description'), self.profile)

        clear = self.clear_data_button = QPushButton(i18n.t('launcherSettings.browsingData.clear'))
        clear.clicked.connect(lambda: self._profile_action('clear-data'))
        self.row(privacy, i18n.t('launcherSettings.browsingData.title'), i18n.t('launcherSettings.browsingData.description'), clear)
        reset = self.reset_permissions_button = QPushButton(i18n.t('common.reset'))
        reset.clicked.connect(lambda: self._profile_action('reset-permissions'))
        self.row(privacy, i18n.t('launcherSettings.sitePermissions.title'), i18n.t('launcherSettings.sitePermissions.profileDescription'), reset)

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

    def section(self, key, title):
        """Add a collapsible section; ``key`` is the stable English id show_section() receives."""
        frame = LauncherCard()
        layout = QVBoxLayout(frame)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)
        header = SectionHeader(i18n.t(title))
        layout.addWidget(header)
        body = QWidget()
        rows = QVBoxLayout(body)
        rows.setContentsMargins(16, 12, 16, 16)
        rows.setSpacing(12)
        layout.addWidget(body)
        header.toggled.connect(body.setVisible)
        expanded = key not in ('Behavior', 'Privacy and sign-ins')
        header.setChecked(expanded)
        body.setVisible(expanded)
        self.sections.addWidget(frame)
        self.section_widgets[key] = (frame, header, body)
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

    def row(self, section, title, subtitle, control, stacked=False, key=None):
        """Add a settings row; ``key`` is a stable id for rows the form looks up again."""
        row = SettingsRow(title, subtitle, control, stacked)
        section.addWidget(row)
        self.rows[key or title] = row
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
        self.rows['Pin Open'].setToolTip(i18n.t('launcherSettings.pinOpen.regularWindowHint') if self.regular.isChecked() else '')
        self.custom_row.hide()
        self.choose_icon.setIcon(LauncherIcons.resolve({**self.original, 'TrayIconMode':self.icon_mode.currentData(), 'CustomTrayIconPath':self.custom_icon.text()}))
        self.idle_row.setVisible(self.policy.currentData() == 0)

    def _custom_name(self):
        path = self.custom_icon.text()
        return path.replace('\\', '/').rsplit('/', 1)[-1] if path else i18n.t('launcherSettings.customIcon.noFile')

    def _pick_anchor(self):
        picker=AnchorPicker(self.anchor.currentData(),self,position_supported=self.position_supported)
        if picker.exec()==QDialog.Accepted:
            index=self.anchor.findData(picker.selected)
            if index>=0:self.anchor.setCurrentIndex(index)

    def _browse_icon(self):
        path, _ = QFileDialog.getOpenFileName(self, i18n.t('icons.chooserTitle'),
                                              filter=i18n.t('icons.imageFiles') + ' (*.png *.svg *.ico *.webp);;' + i18n.t('prompts.fileFilter.all') + ' (*)')
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
            button.setToolTip(i18n.t('launcherSettings.profileChangePending') if not unchanged else '')

    def _profile_action(self, action):
        if self.profile.currentData() != self.original.get('WebSharedProfile', False):
            return
        message = 'launcherSettings.browsingData.confirmShared' if self.original.get('WebSharedProfile') else 'launcherSettings.browsingData.confirmPrivate'
        if action == 'clear-data' and QMessageBox.question(self, i18n.t('launcherSettings.browsingData.confirmTitle'), i18n.t(message),
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
            QMessageBox.warning(self, i18n.t('prompts.checkDetails'), str(error))
            return
        if not self._commit_live():
            return
        super().accept()

    def reject(self):
        # Windows closes its settings form with Done, Escape or the title-bar close action.
        self.accept()
