"""Windows-reference launcher cards, painted with the desktop's native Qt style."""
from PySide6.QtCore import QRect, QSize, Qt
from PySide6.QtGui import QFont, QFontMetrics, QIcon, QPainter, QPalette
from PySide6.QtWidgets import QGroupBox, QSizePolicy, QStyle, QStyleOption, QStyleOptionGroupBox, QStyleOptionToolButton, QToolButton, QWidget


class LauncherCard(QGroupBox):
    """A passive native card frame; its header is content, not a group-box caption."""
    def paintEvent(self, event):
        option = QStyleOptionGroupBox()
        self.initStyleOption(option)
        option.state &= ~(QStyle.State_MouseOver | QStyle.State_HasFocus)
        painter = QPainter(self)
        self.style().drawComplexControl(QStyle.CC_GroupBox, option, painter, self)


class LauncherCardsColumn(QWidget):
    """Prefer the Windows 450-pixel column, shrinking with the viewport on narrow windows."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setMaximumWidth(450)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Preferred)

    def sizeHint(self):
        return QSize(450, super().sizeHint().height())


class LauncherActionRow(QToolButton):
    """Two-line action with trailing chevron and 12/10 reference content padding."""
    def __init__(self, title, subtitle, parent=None):
        super().__init__(parent)
        self.title = title
        self.subtitle = subtitle
        self.setAutoRaise(True)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.setToolTip(title)
        self.setAccessibleName(title)
        self.setAccessibleDescription(subtitle)
        self.setMinimumWidth(80)
        self.setFixedHeight(self.sizeHint().height())

    def _font(self, pixels):
        font = QFont(self.font())
        font.setPixelSize(pixels)
        return font

    def sizeHint(self):
        return QSize(200, QFontMetrics(self._font(14)).height() + QFontMetrics(self._font(12)).height() + 20)

    def paintEvent(self, event):
        painter = QPainter(self)
        option = QStyleOptionToolButton()
        self.initStyleOption(option)
        option.text = ''
        option.icon = QIcon()
        self.style().drawComplexControl(QStyle.CC_ToolButton, option, painter, self)
        group = QPalette.Active if self.isEnabled() else QPalette.Disabled
        color = self.palette().color(group, QPalette.ButtonText)
        painter.setFont(self._font(14))
        painter.setPen(color)
        height = painter.fontMetrics().height()
        title = painter.fontMetrics().elidedText(self.title, Qt.ElideRight, max(1, self.width() - 48))
        painter.drawText(QRect(12, 10, max(1, self.width() - 48), height), Qt.AlignLeft | Qt.AlignVCenter, title)
        painter.setFont(self._font(12))
        color.setAlphaF(.5)
        painter.setPen(color)
        subtitle = painter.fontMetrics().elidedText(self.subtitle, Qt.ElideRight, max(1, self.width() - 48))
        painter.drawText(QRect(12, 10 + height, max(1, self.width() - 48), painter.fontMetrics().height()),
                         Qt.AlignLeft | Qt.AlignVCenter, subtitle)
        arrow = QStyleOption()
        arrow.initFrom(self)
        arrow.rect = QRect(self.width() - 24, (self.height() - 12) // 2, 12, 12)
        self.style().drawPrimitive(QStyle.PE_IndicatorArrowRight, arrow, painter, self)
