"""Private relocatable Python/Qt paths, without exporting them to desktop/browser subprocesses."""
import faulthandler
faulthandler.enable()
from pathlib import Path
import sys
root=Path(__file__).resolve().parent
site=root.parent/f'python{sys.version_info.major}.{sys.version_info.minor}'/'site-packages'
sys.path[:0]=[str(root),str(site)]
from PySide6.QtCore import QCoreApplication
QCoreApplication.setLibraryPaths([str(site/'PySide6/Qt/plugins')])
from little_launcher.app import main
raise SystemExit(main())
