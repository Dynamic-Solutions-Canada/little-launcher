"""Native screen-anchor grid with pending selection and desktop button ordering."""
from PySide6.QtCore import Qt
from PySide6.QtWidgets import QButtonGroup, QDialog, QDialogButtonBox, QGridLayout, QLabel, QPushButton, QVBoxLayout

from . import i18n

# Saved WebAnchor values with the label keys the Windows launcher settings use.
ANCHOR_KEYS = [('launcherSettings.opensAt.tray', 0), ('launcherSettings.opensAt.topLeft', 1),
               ('launcherSettings.opensAt.topCentre', 2), ('launcherSettings.opensAt.topRight', 3),
               ('launcherSettings.opensAt.left', 4), ('launcherSettings.opensAt.centre', 5),
               ('launcherSettings.opensAt.right', 6), ('launcherSettings.opensAt.bottomLeft', 7),
               ('launcherSettings.opensAt.bottomCentre', 8), ('launcherSettings.opensAt.bottomRight', 9),
               ('launcherSettings.opensAt.lastPosition', 10)]


class AnchorPicker(QDialog):
    @staticmethod
    def choices():
        """(label, WebAnchor value) pairs in the current interface language, resolved on each call."""
        return [(i18n.t(key), value) for key, value in ANCHOR_KEYS]

    def __init__(self, selected, parent=None, position_supported=False):
        super().__init__(parent)
        self.selected = selected
        self.setWindowTitle(i18n.t('launcherSettings.opensAt.title'))
        layout = QVBoxLayout(self)
        label = QLabel(i18n.t('webPosition.pickerIntro'))
        label.setWordWrap(True)
        layout.addWidget(label)
        grid = QGridLayout()
        self.buttons = QButtonGroup(self)
        for title, value in self.choices():
            button = QPushButton(title)
            button.setCheckable(True)
            button.setChecked(value == selected)
            button.setEnabled(value != 10 or position_supported)
            button.clicked.connect(lambda checked=False, choice=value: self.choose(choice))
            self.buttons.addButton(button, value)
            if value == 0:
                grid.addWidget(button, 0, 0, 1, 3)
            elif value == 10:
                grid.addWidget(button, 4, 0, 1, 3)
            else:
                grid.addWidget(button, 1 + (value - 1) // 3, (value - 1) % 3)
        layout.addLayout(grid)
        notice = QLabel(i18n.t('webPosition.pickerNotice'))
        notice.setWordWrap(True)
        layout.addWidget(notice)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        self.resize(480, 300)

    def choose(self, value):
        self.selected = value
