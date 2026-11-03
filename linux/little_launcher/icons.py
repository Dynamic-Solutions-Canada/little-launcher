"""Launcher icon modes and StatusNotifierItem pixmaps in the host's required byte order."""
from pathlib import Path

from PySide6.QtCore import QBuffer, QIODevice, QRect, Qt
from PySide6.QtGui import QIcon, QImage, QPainter, QPixmap

from .desktop import ICON, ROOT, item_icon
from .document import is_heading, visible_items
from .icon_glyphs import PRESETS, canonical_mode, glyph_icon, preset_icon


class LauncherIcons:
    COLORS = ['Blue', 'Green', 'Teal', 'Red', 'Orange', 'Purple']

    @classmethod
    def resolve(cls, launcher):
        """Render supported portable modes, preserving unsupported metadata in the document."""
        mode = canonical_mode(launcher.get('TrayIconMode', 'Composite'))
        if mode.startswith('Glyph:') and launcher.get('TrayIconPreviewMode')==mode and launcher.get('TrayIconPreviewData'):
            try:
                from .portable_icons import preview_bytes
                pixmap=QPixmap();pixmap.loadFromData(preview_bytes(launcher['TrayIconPreviewData']))
                if not pixmap.isNull():return QIcon(pixmap)
            except (ValueError,RuntimeError):pass
        if mode in PRESETS:
            return preset_icon(mode)
        glyph = glyph_icon(mode)
        if glyph is not None:
            return glyph
        if mode in cls.COLORS:
            return QIcon(str(ROOT / 'resources/app-icons' / (mode + '.png')))
        if mode == 'Custom' and Path(launcher.get('CustomTrayIconPath', '')).is_file():
            icon = QIcon(launcher['CustomTrayIconPath'])
            if not icon.isNull():
                return icon
        if mode == 'Composite':
            entries = []
            def collect(items):
                for item in items:
                    if item.get('IsGroup'):
                        collect(item.get('Children', []))
                    elif not item.get('IsColumnBreak') and not is_heading(item):
                        entries.append(item)
            collect(visible_items(launcher.get('Items', [])))
            if entries:
                pixmap = QPixmap(64, 64)
                pixmap.fill(Qt.transparent)
                painter = QPainter(pixmap)
                for index, item in enumerate(entries[:4]):
                    rectangle = QRect((index % 2) * 32, (index // 2) * 32, 32, 32)
                    item_icon(item).paint(painter, rectangle)
                painter.end()
                return QIcon(pixmap)
        return QIcon(str(ICON))

    @staticmethod
    def glyph_png(launcher):
        return LauncherIcons.window_png(launcher) if glyph_icon(launcher.get('TrayIconMode','')) is not None else None

    @staticmethod
    def window_png(launcher):
        """Pass a small rendered icon to the browser, never a synced filesystem path."""
        buffer = QBuffer()
        buffer.open(QIODevice.WriteOnly)
        icon = LauncherIcons.resolve(launcher)
        if not icon.pixmap(64, 64).save(buffer, 'PNG'):
            return ''
        return bytes(buffer.data().toBase64()).decode('ascii')

    @staticmethod
    def pixmaps(icon):
        """Export non-premultiplied ARGB bytes; SNI requires network order, not native BGRA."""
        result = []
        for size in (16, 32, 64):
            image = icon.pixmap(size, size).toImage().convertToFormat(QImage.Format_RGBA8888)
            if image.isNull():
                continue
            raw = bytes(image.constBits())
            pixels = bytearray()
            for y in range(image.height()):
                for x in range(image.width()):
                    offset = y * image.bytesPerLine() + x * 4
                    pixels.append(raw[offset + 3])
                    pixels.extend(raw[offset:offset + 3])
            result.append([image.width(), image.height(), bytes(pixels)])
        return result
