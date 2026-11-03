"""Use a complete distro Qt/KDE stack when available; never mix host and bundled Qt libraries."""
import faulthandler
faulthandler.enable()
from pathlib import Path
import sys


def probe():
    if sys.version_info < (3, 11):
        return False
    try:
        from PySide6 import QtWidgets, QtNetwork, QtDBus
        from PySide6.QtCore import QLibraryInfo, qVersion
        import dbus_next
        version = tuple(int(part) for part in qVersion().split('.')[:3])
        plugins = Path(QLibraryInfo.path(QLibraryInfo.PluginsPath))
        return (6, 8, 2) <= version < (7, 0, 0) and any(
            (plugins / 'platformthemes' / name).is_file()
            for name in ('KDEPlasmaPlatformTheme6.so', 'KDEPlasmaPlatformTheme.so'))
    except (ImportError, OSError, ValueError):
        return False


if __name__ == '__main__':
    if sys.argv[1:] == ['--probe']:
        raise SystemExit(0 if probe() else 1)
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from little_launcher.app import main
    raise SystemExit(main())
