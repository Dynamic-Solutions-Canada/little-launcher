"""User-scoped desktop setup, native icons, and launch execution without a shell."""
import os
from pathlib import Path
import shlex
import shutil
import subprocess

from PySide6.QtCore import QEvent, QFileInfo, QLibraryInfo, QObject, QTimer, QUrl, Signal
from PySide6.QtGui import QDesktopServices, QIcon
from PySide6.QtWidgets import QApplication, QFileIconProvider

from .storage import atomic_write
from .catalog import DesktopCatalog
from .icon_glyphs import glyph_icon

ROOT = Path(__file__).resolve().parents[1]
ICON = ROOT / 'resources' / 'little-launcher.png'


def item_icon(item):
    """Use native theme/application icons, falling back safely from foreign cached paths."""
    custom = Path(item.get('IconPath') or '/nonexistent')
    if custom.is_file():
        icon=QIcon(str(custom))
        if not icon.isNull():return icon
    glyph=item.get('IconGlyph','')
    glyph={'Globe24':'\ue774','Open24':'\ue8e5'}.get(glyph,glyph) if isinstance(glyph,str) else ''
    if glyph:
        icon=glyph_icon('Glyph:'+glyph,item.get('IconColor'))
        if icon is not None:return icon
    if item.get('IsWebsite'):
        return QIcon.fromTheme('internet-web-browser', QIcon(str(ICON)))
    target = item.get('Path', '')
    if target.endswith('.desktop'):
        entry = desktop_entry(target)
        if entry:
            return QIcon.fromTheme(entry.get('Icon', ''), QIcon(str(ICON)))
    if target and Path(target).exists():
        icon = QFileIconProvider().icon(QFileInfo(target))
        if not icon.isNull():
            return icon
    return QIcon.fromTheme('application-x-executable', QIcon(str(ICON)))


def desktop_entry(identifier):
    """Resolve an installed desktop ID from standard XDG application directories."""
    return DesktopCatalog().resolve(identifier)


def launch_item(item):
    """Launch a URL, desktop ID, executable, or file without shell interpolation."""
    target = str(item.get('Path', ''))
    if item.get('IsWebsite'):
        url = QUrl(target)
        if url.scheme() not in ('http', 'https') or not url.host():
            raise ValueError('Website shortcuts need a valid HTTP or HTTPS address.')
        if not QDesktopServices.openUrl(url):
            raise OSError('The default browser could not open this address.')
        return
    if target.endswith('.desktop'):
        if item.get('Arguments'):
            raise ValueError('Choose the executable instead of a desktop entry to supply extra arguments.')
        entry = desktop_entry(target)
        if not entry:
            raise FileNotFoundError('This application is not installed on this machine.')
        launcher = shutil.which('gio')
        if launcher:
            command = [launcher, 'launch', entry['_path']]
        else:
            launcher = shutil.which('gtk-launch')
            if not launcher or Path(target).is_absolute():
                raise OSError('Desktop application launching requires gio.')
            command = [launcher, target.removesuffix('.desktop')]

    else:
        executable = shutil.which(target) if not Path(target).is_absolute() else target
        if not executable or not Path(executable).exists():
            raise FileNotFoundError('This shortcut target is unavailable. Edit it or reconnect its drive.')
        if Path(executable).is_dir() or not os.access(executable, os.X_OK):
            if not QDesktopServices.openUrl(QUrl.fromLocalFile(executable)):
                raise OSError('No application could open this target.')
            return
        command = [executable, *shlex.split(item.get('Arguments', ''))]
    subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL, start_new_session=True)


def desktop_executable():
    """AppImage runtime mounts are temporary; user entries must address the stable image file."""
    value=os.environ.get('APPIMAGE')
    if value:
        path=Path(value)
        if not path.is_absolute() or not path.is_file():raise OSError('The AppImage file is unavailable. Run it from its permanent location.')
        return path
    return ROOT/'bin/little-launcher'


def desktop_argument(path):
    value=str(path)
    if any(ord(character)<32 for character in value):raise ValueError('The application path contains unsupported control characters.')
    value=value.replace('\\','\\\\')
    for character in (chr(34),chr(96),chr(36)):value=value.replace(character,'\\'+character)
    return chr(34)+value.replace('\\','\\\\').replace('%','%%')+chr(34)


def register_appimage():
    """Register only this user's app entry/icon; no OS package or KWin modification."""
    if not os.environ.get('APPIMAGE'):return
    data=Path(os.environ.get('XDG_DATA_HOME',Path.home()/'.local/share'))
    icon=data/'icons/hicolor/256x256/apps/io.github.RyanEwen.LittleLauncher.png'
    atomic_write(icon,ICON.read_bytes())
    content=('[Desktop Entry]\nType=Application\nName=Little Launcher\n'
             f'Exec={desktop_argument(desktop_executable())}\n'
             'Icon=io.github.RyanEwen.LittleLauncher\nTerminal=false\nCategories=Utility;\n'
             'StartupWMClass=io.github.RyanEwen.LittleLauncher\n')
    atomic_write(data/'applications/io.github.RyanEwen.LittleLauncher.desktop',content.encode())
    web=content.replace('Name=Little Launcher\n','Name=Little Launcher Web\n').replace('StartupWMClass=io.github.RyanEwen.LittleLauncher\n','StartupWMClass=io.github.RyanEwen.LittleLauncher.Web\n')+'NoDisplay=true\n'
    atomic_write(data/'applications/io.github.RyanEwen.LittleLauncher.Web.desktop',web.encode())


def set_autostart(enabled):
    """Manage only this app's user autostart entry, preserving an explicit off choice."""
    directory = Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'autostart'
    path = directory / 'little-launcher.desktop'
    if not enabled:
        path.unlink(missing_ok=True)
        return
    content = ('[Desktop Entry]\nType=Application\nName=Little Launcher\n'
               f'Exec={desktop_argument(desktop_executable())} --background\nIcon=io.github.RyanEwen.LittleLauncher\nTerminal=false\n'
               'X-GNOME-Autostart-enabled=true\n')
    atomic_write(path, content.encode())


def prepare_desktop_environment():
    """Desktop launch tools can omit the version xdg-open needs for current KDE routing."""
    if 'KDE' in os.environ.get('XDG_CURRENT_DESKTOP','').upper().split(':'):
        os.environ.setdefault('KDE_SESSION_VERSION','6')


def native_tray_coordinates():
    """Plasma's default Wayland SNI coordinates are native pixels for Xwayland compatibility."""
    if 'KDE' not in os.environ.get('XDG_CURRENT_DESKTOP', '') or os.environ.get('XDG_SESSION_TYPE') != 'wayland':
        return False
    import configparser
    config = configparser.ConfigParser(interpolation=None, strict=False)
    config.read(Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'kdeglobals')
    return config.get('KScreen', 'XwaylandClientsScale', fallback='true').lower() != 'false'


class DesktopIntegration:
    """Install only the app-owned KWin helper after an explicit setup-button action."""

    SCRIPT_ID = 'little-launcher'

    def __init__(self):
        self.qdbus = self._find_qdbus()
        self.enabled = False
        self.refresh()

    @staticmethod
    def _find_qdbus():
        """Use the distro Qt tools directory when KDE's command is outside PATH."""
        for name in ('qdbus6', 'qdbus-qt6', 'qdbus'):
            executable = shutil.which(name)
            if executable:
                return executable
        qt_bin = Path(QLibraryInfo.path(QLibraryInfo.BinariesPath))
        for name in ('qdbus', 'qdbus-qt6'):
            executable = qt_bin / name
            if executable.is_file() and os.access(executable, os.X_OK):
                return str(executable)
        bundled=Path(os.environ.get('APPDIR','/nonexistent'))/'usr/bin/qdbus6'
        return str(bundled) if bundled.is_file() and os.access(bundled,os.X_OK) else None

    def _call(self, *arguments):
        if not self.qdbus:
            raise OSError('KDE D-Bus tooling is not installed.')
        return subprocess.run([self.qdbus, *arguments], check=True, capture_output=True,
                              text=True, timeout=5).stdout.strip()

    def refresh(self):
        try:
            self.enabled = self._call('org.kde.KWin', '/Scripting',
                                      'org.kde.kwin.Scripting.isScriptLoaded', self.SCRIPT_ID) == 'true'
        except (OSError, subprocess.SubprocessError):
            self.enabled = False

    def supports_launcher(self, identifier):
        """Report this integration's current per-launcher placement capability."""
        return self.enabled

    def enable(self):
        """Persist and load the packaged helper; regular-window mode survives setup failure."""
        if 'KDE' not in os.environ.get('XDG_CURRENT_DESKTOP', ''):
            raise OSError('Anchored GNOME integration is not available yet. Regular-window mode remains available.')
        directory = Path(os.environ.get('XDG_DATA_HOME', Path.home() / '.local/share')) / 'kwin/scripts' / self.SCRIPT_ID
        for relative in ['metadata.json', 'contents/code/main.js']:
            atomic_write(directory / relative, (ROOT / 'desktop/kwin' / relative).read_bytes())
        self.refresh()
        if self.enabled:
            self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.unloadScript', self.SCRIPT_ID)
        writer = shutil.which('kwriteconfig6')
        if not writer:
            raise OSError('KDE configuration tooling is not installed.')
        subprocess.run([writer, '--file', 'kwinrc', '--group', 'Plugins', '--key',
                        self.SCRIPT_ID + 'Enabled', 'true'], check=True, timeout=5)
        self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.loadScript',
                   str(directory / 'contents/code/main.js'), self.SCRIPT_ID)
        self._call('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting.start')
        self.refresh()
        if not self.enabled:
            raise OSError('Desktop integration could not be loaded. Regular-window mode is still active.')


def native_theme():
    """Send the actual desktop palette/font to browser chrome, not merely light/dark state."""
    app = QApplication.instance()
    palette = app.palette()
    return {'window': palette.window().color().name(), 'text': palette.windowText().color().name(),
            'base': palette.base().color().name(), 'button': palette.button().color().name(),
            'highlight': palette.highlight().color().name(), 'font': app.font().family(),
            'fontSize': app.font().pointSizeF(), 'style': app.style().objectName(),
            'iconTheme': QIcon.themeName(),
            'dark': palette.window().color().lightnessF() < 0.5}


class NativeThemeMonitor(QObject):
    """Notify browser chrome after Qt has applied a desktop font, palette, or style change."""
    changed = Signal()

    def __init__(self):
        super().__init__()
        self.timer = QTimer(self)
        self.timer.setSingleShot(True)
        self.last_theme = native_theme()
        self.timer.timeout.connect(self._publish)

    def _publish(self):
        theme = native_theme()
        if theme != self.last_theme:
            self.last_theme = theme
            self.changed.emit()

    def eventFilter(self, watched, event):
        if event.type() in (QEvent.ApplicationPaletteChange, QEvent.ApplicationFontChange, QEvent.StyleChange, QEvent.ThemeChange):
            self.timer.start(0)
        return False


def application_version():
    """Read the packaged value or repository's single version source, without a second pin."""
    version_file = ROOT / 'VERSION'
    if version_file.is_file():
        return version_file.read_text().strip()
    import xml.etree.ElementTree as ET
    try:
        return ET.parse(ROOT.parent / 'Directory.Build.props').findtext('.//Version') or 'development'
    except (OSError, ET.ParseError):
        return 'development'
