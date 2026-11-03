"""Native screen-anchor grid with pending selection and desktop button ordering."""
from PySide6.QtCore import Qt
from PySide6.QtWidgets import QButtonGroup, QDialog, QDialogButtonBox, QGridLayout, QLabel, QPushButton, QVBoxLayout

ANCHORS = [('Near its tray icon', 0), ('Top left', 1), ('Top centre', 2), ('Top right', 3),
           ('Left', 4), ('Centre', 5), ('Right', 6), ('Bottom left', 7), ('Bottom centre', 8), ('Bottom right', 9),
           ('Where you last dragged it', 10)]

class AnchorPicker(QDialog):
    def __init__(self, selected, parent=None, position_supported=False):
        super().__init__(parent)
        self.selected = selected
        self.setWindowTitle('Opens At')
        layout = QVBoxLayout(self)
        label = QLabel('Choose where this launcher opens on the screen holding its tray icon.')
        label.setWordWrap(True)
        layout.addWidget(label)
        grid = QGridLayout()
        self.buttons = QButtonGroup(self)
        for title, value in ANCHORS:
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
        notice = QLabel('Dragged positions stay on this device. KDE desktop integration is required.')
        notice.setWordWrap(True)
        layout.addWidget(notice)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        self.resize(480, 300)

    def choose(self, value):
        self.selected = value
