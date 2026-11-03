"""Capture the private KDE compositor using its explicitly granted screenshot API.

The fixture desktop entry grants only its disposable test interpreter access.
This never connects to the host compositor or host portal.
"""
import asyncio
import os
import sys

from dbus_next import Message, MessageType, Variant
from dbus_next.aio import MessageBus
from PySide6.QtGui import QImage

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
assert os.environ.get('WAYLAND_DISPLAY') == 'wayland-poc'


def read_all(descriptor):
    """Drain concurrently so a full frame cannot block KWin before its D-Bus reply."""
    chunks = []
    with os.fdopen(descriptor, 'rb') as stream:
        while chunk := stream.read(65536):
            chunks.append(chunk)
    return b''.join(chunks)


async def main():
    """Save the compositor-provided pixel format without altering its colors."""
    bus = await MessageBus(negotiate_unix_fd=True).connect()
    read_descriptor, write_descriptor = os.pipe()
    reader = asyncio.create_task(asyncio.to_thread(read_all, read_descriptor))
    try:
        reply = await bus.call(Message(
            destination='org.kde.KWin.ScreenShot2',
            path='/org/kde/KWin/ScreenShot2',
            interface='org.kde.KWin.ScreenShot2',
            member='CaptureWorkspace',
            signature='a{sv}h',
            body=[{'include-cursor': Variant('b', True)}, 0],
            unix_fds=[write_descriptor],
        ))
    finally:
        os.close(write_descriptor)

    try:
        assert reply.message_type != MessageType.ERROR, reply.body
        raw = await asyncio.wait_for(reader, 10)
        metadata = {key: value.value for key, value in reply.body[0].items()}
        image = QImage(raw, metadata['width'], metadata['height'], metadata['stride'],
                       QImage.Format(metadata['format']))
        assert image.save(sys.argv[1]), metadata
        print(metadata)
    finally:
        bus.disconnect()


asyncio.run(main())
