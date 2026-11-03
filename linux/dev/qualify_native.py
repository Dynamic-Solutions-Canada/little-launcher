"""Qualify the distro-native palette/style and actual embedded browser before UI adoption."""
import json
import os
import sys
from pathlib import Path
from PySide6.QtCore import QTimer, QUrl
from PySide6.QtGui import QPalette
from PySide6.QtWidgets import QApplication, QDialog, QVBoxLayout, QPushButton, QLabel
from PySide6.QtWebEngineCore import QWebEngineProfile, QWebEnginePage
from PySide6.QtWebEngineWidgets import QWebEngineView
app = QApplication(sys.argv)
app.setApplicationName('Little Launcher Native Qualification')
window = QDialog()
window.setWindowTitle('Little Launcher native qualification')
window.resize(640, 600)
layout = QVBoxLayout(window)
layout.addWidget(QLabel('Native desktop controls and embedded Chromium'))
layout.addWidget(QPushButton('Native button'))
profile = QWebEngineProfile('qualification', app)
profile.setPersistentStoragePath('/work/qt-qualification-profile')
view = QWebEngineView(window)
view.setPage(QWebEnginePage(profile, view))
layout.addWidget(view)
window.show()

def loaded(success):
    """Record native theme identity and painted output only after a real page load."""
    palette = app.palette()
    result = {'loaded': success, 'style': app.style().objectName(), 'font': app.font().toString(),
              'window': palette.color(QPalette.Window).name(), 'text': palette.color(QPalette.WindowText).name(),
              'url': view.url().toString(), 'qt': profile.httpUserAgent()}
    Path('/work/native-qualification.json').write_text(json.dumps(result, indent=2))
    QTimer.singleShot(500, lambda: window.grab().save('/work/native-qualification.png'))
    QTimer.singleShot(1200, app.quit)
view.loadFinished.connect(loaded)
view.load(QUrl('https://example.com'))
QTimer.singleShot(20000, app.quit)
sys.exit(app.exec())
