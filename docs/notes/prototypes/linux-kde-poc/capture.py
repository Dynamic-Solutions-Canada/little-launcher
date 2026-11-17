"""Capture only the container-owned Xvfb framebuffer displaying the nested Wayland compositor."""
import sys
from Xlib import display, X
from PIL import Image
connection = display.Display(':77')
root = connection.screen().root
geometry = root.get_geometry()
raw = root.get_image(0, 0, geometry.width, geometry.height, X.ZPixmap, 0xffffffff)
image = Image.frombytes('RGB', (geometry.width, geometry.height), raw.data, 'raw', 'BGRX')
image.save(sys.argv[1])
