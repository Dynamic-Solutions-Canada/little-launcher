"""Sample untouched private desktop frames around one actual tray click.

The normal KDE palette's window color identifies newly painted launcher chrome on a
clean wallpaper background. This is a software-rendered fixture observation, not a
physical GPU, fractional-scale, or multi-monitor acceptance result.
"""
import json
from pathlib import Path
import sys
import time

from PIL import Image, ImageChops
from Xlib import X, display
from Xlib.ext import xtest

name, x, y = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
output = Path('/work') / ('placement-' + name)
output.mkdir(exist_ok=True)
connection = display.Display(':77')
root = connection.screen().root
geometry = root.get_geometry()
size = (geometry.width, geometry.height)


def read():
    return root.get_image(0, 0, *size, X.ZPixmap, 0xffffffff).data


def mask(raw):
    frame = Image.frombytes('RGB', size, raw, 'raw', 'BGRX')
    channels = [channel.point(lambda value, target=target: 255 if value == target else 0)
                for channel, target in zip(frame.split(), (239, 240, 241))]
    return ImageChops.multiply(ImageChops.multiply(channels[0], channels[1]), channels[2]).crop((0, 0, size[0], 752))


baseline = mask(read())
if baseline.histogram()[255] > 1000:
    raise RuntimeError('Close existing launcher windows before measuring from a clean wallpaper background.')
xtest.fake_input(connection, X.MotionNotify, x=x, y=y)
connection.sync()
start = time.monotonic()
xtest.fake_input(connection, X.ButtonPress, detail=1)
xtest.fake_input(connection, X.ButtonRelease, detail=1)
connection.sync()
frames = []
for index in range(90):
    frames.append((time.monotonic() - start, read()))
    time.sleep(0.012)
rows = []
for index, (elapsed, raw) in enumerate(frames):
    changed = ImageChops.subtract(mask(raw), baseline)
    count = changed.histogram()[255]
    bounds = changed.getbbox() if count > 1000 else None
    rows.append({'frame': index, 'elapsedSeconds': round(elapsed, 4), 'bounds': bounds, 'matchingPixels': count})
visible = [row for row in rows if row['bounds']]
positions = sorted({tuple(row['bounds']) for row in visible})
result = {'click': [x, y], 'sampleCount': len(rows), 'firstVisible': visible[0] if visible else None,
          'uniquePaintedBounds': positions}
(output / 'result.json').write_text(json.dumps(result, indent=2))
(output / 'frames.json').write_text(json.dumps(rows, indent=2))
for label, index in [('first', visible[0]['frame'] if visible else 0), ('last', len(frames) - 1)]:
    Image.frombytes('RGB', size, frames[index][1], 'raw', 'BGRX').save(output / (label + '.png'))
print(json.dumps(result, indent=2))
