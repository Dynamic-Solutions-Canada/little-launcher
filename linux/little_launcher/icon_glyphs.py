"""Native semantic presets and portable Unicode icon text, without bundling Windows fonts."""
import math
import re
import unicodedata
from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QFont, QFontDatabase, QFontMetricsF, QIcon, QPainter, QPainterPath, QPalette, QPen, QPixmap, QPolygonF
from PySide6.QtWidgets import QApplication

PRESETS={'Pin':('\ue840','pin'),'Star':('\ue734','starred'),'Heart':('\ueb51','emblem-favorite'),
         'Lightning':('\ue945','flash'),'Search':('\ue721','edit-find'),'Globe':('\ue774','globe')}
LEGACY=['Blue','Green','Teal','Red','Orange','Purple',*PRESETS,'Custom','Composite']

def canonical_mode(mode):
    if type(mode) is int:
        return LEGACY[mode] if 0<=mode<len(LEGACY) else 'Blue'
    return mode if isinstance(mode,str) else 'Blue'

def glyph_parts(mode):
    if not isinstance(mode,str) or not mode.startswith('Glyph:'):
        return None
    payload=mode[6:];color=None
    if payload.startswith('#'):
        candidate,separator,text=payload.partition(':')
        if not separator or not re.fullmatch(r'#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?',candidate):return None
        color=candidate;payload=text
    if not 0 < len(payload) <= 32 or any(ord(char)<32 or ord(char)==127 or 0xd800<=ord(char)<=0xdfff for char in payload):
        return None
    visible=any(unicodedata.category(char) not in ('Cf','Mn','Me','Zs','Zl','Zp') for char in payload)
    return (payload,color) if visible and payload.strip() else None

def preset_icon(name,color=None):
    if color is None:
        themed=QIcon.fromTheme(PRESETS[name][1])
        if not themed.isNull():return themed
    image=QPixmap(64,64);image.fill(Qt.transparent)
    painter=QPainter(image);painter.setRenderHint(QPainter.Antialiasing);painter.scale(4,4)
    foreground=QColor(color) if color else QApplication.palette().color(QPalette.WindowText)
    painter.setPen(QPen(foreground,1.3));painter.setBrush(Qt.NoBrush)
    if name=='Star':
        polygon=QPolygonF([QPointF(8+math.cos(-math.pi/2+i*math.pi/5)*(6.5 if i%2==0 else 3),8+math.sin(-math.pi/2+i*math.pi/5)*(6.5 if i%2==0 else 3)) for i in range(10)])
        painter.drawPolygon(polygon)
    elif name=='Heart':
        path=QPainterPath(QPointF(8,14));path.cubicTo(0,9,0,3,4,3);path.cubicTo(6,3,8,4,8,6);path.cubicTo(8,4,10,3,12,3);path.cubicTo(16,3,16,9,8,14);painter.drawPath(path)
    elif name=='Pin':
        painter.drawPolygon(QPolygonF([QPointF(x,y) for x,y in [(5,2),(11,2),(10,7),(12,9),(9,9),(8,14),(7,9),(4,9),(6,7)]]))
    elif name=='Lightning':
        painter.drawPolygon(QPolygonF([QPointF(x,y) for x,y in [(9,1),(3,9),(7,9),(6,15),(13,6),(9,6)]]))
    elif name=='Search':
        painter.drawEllipse(QRectF(2,2,8,8));painter.drawLine(QPointF(9,9),QPointF(14,14))
    else:
        painter.drawEllipse(QRectF(1.5,1.5,13,13));painter.drawEllipse(QRectF(5,1.5,6,13));painter.drawLine(QPointF(1.5,8),QPointF(14.5,8))
    painter.end();return QIcon(image)

def glyph_icon(mode, color_override=None):
    parts=glyph_parts(mode)
    if parts is None:return None
    glyph,color=parts
    if isinstance(color_override,str) and re.fullmatch(r'#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?',color_override):color=color_override
    for name,(character,_) in PRESETS.items():
        if glyph==character:return preset_icon(name,color)
    private=any(0xe000<=ord(char)<=0xf8ff or 0xf0000<=ord(char)<=0xffffd or 0x100000<=ord(char)<=0x10fffd for char in glyph)
    family=next((name for name in QFontDatabase.families() if name.casefold()=='segoe fluent icons'),None) if private else None
    if private and family is None:return None
    font=QFont(family) if private else QFont(QApplication.font());font.setPixelSize(48)
    bounds=QFontMetricsF(font).tightBoundingRect(glyph)
    maximum=max(bounds.width(),bounds.height(),1)
    if maximum>54:font.setPixelSize(max(8,int(48*54/maximum)))
    image=QPixmap(64,64);image.fill(Qt.transparent)
    painter=QPainter(image);painter.setRenderHint(QPainter.Antialiasing);painter.setFont(font)
    painter.setPen(QColor(color) if color else QApplication.palette().color(QPalette.WindowText))
    painter.drawText(QRectF(0,0,64,64),Qt.AlignCenter,glyph)
    painter.end();return QIcon(image)
