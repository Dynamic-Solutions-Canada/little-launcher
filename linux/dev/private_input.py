"""Inject input only into the container-owned wayland-poc compositor.

The private fixture generates bindings from protocols/fake-input.xml and grants
only its disposable test interpreter access. Production does not use this API.
"""
import os
import sys
import time

assert os.environ.get('XDG_RUNTIME_DIR') == '/tmp/ll-runtime'
assert os.environ.get('WAYLAND_DISPLAY') == 'wayland-poc'
sys.path.insert(0, '/work/protocols')
from pywayland.client import Display
from fake_input import OrgKdeKwinFakeInput

with Display() as display:
    registry = display.get_registry()
    devices = []

    def global_added(registry, name, interface, version):
        """Bind only the test-input interface advertised by this private compositor."""
        if interface == OrgKdeKwinFakeInput.name:
            devices.append(registry.bind(name, OrgKdeKwinFakeInput, min(version, 6)))

    registry.dispatcher['global'] = global_added
    display.roundtrip()
    assert devices, 'Private compositor has no test-input interface'
    device = devices[0]
    device.authenticate('Little Launcher private fixture', 'Disposable KDE portal verification')
    display.roundtrip()

    if sys.argv[1] in ('click', 'right-click'):
        x, y = map(int, sys.argv[2:4])
        assert 0 <= x < 1280 and 0 <= y < 800
        device.pointer_motion_absolute(x, y)
        display.roundtrip()
        # Let the native event loop deliver pointer focus before the button press.
        time.sleep(0.2)
        button = 273 if sys.argv[1] == 'right-click' else 272
        device.button(button, 1)
        display.roundtrip()
        time.sleep(0.1)
        device.button(button, 0)
    elif sys.argv[1] == 'drag':
        start_x, start_y, end_x, end_y = map(int, sys.argv[2:6])
        assert all(0 <= x < 1280 and 0 <= y < 800 for x, y in ((start_x, start_y), (end_x, end_y)))
        device.pointer_motion_absolute(start_x, start_y)
        display.roundtrip()
        time.sleep(0.2)
        device.button(272, 1)
        display.roundtrip()
        for step in range(1, 21):
            device.pointer_motion_absolute(start_x + (end_x - start_x) * step / 20,
                                           start_y + (end_y - start_y) * step / 20)
            display.roundtrip()
            time.sleep(0.02)
        if len(sys.argv) > 6:
            time.sleep(max(0, min(2, float(sys.argv[6]))))
        device.button(272, 0)
    elif sys.argv[1] == 'wheel':
        x, y, amount = map(int, sys.argv[2:5])
        assert 0 <= x < 1280 and 0 <= y < 800
        device.pointer_motion_absolute(x, y)
        display.roundtrip()
        time.sleep(0.2)
        control = len(sys.argv) > 5 and sys.argv[5] == 'control'
        if control:
            device.keyboard_key(29, 1)
            display.roundtrip()
            time.sleep(0.1)
        device.axis(0, amount)
        display.roundtrip()
        time.sleep(0.1)
        if control:
            device.keyboard_key(29, 0)
    elif sys.argv[1] == 'key':
        key = int(sys.argv[2])
        device.keyboard_keysym(key, 1)
        device.keyboard_keysym(key, 0)
    elif sys.argv[1] in ('text', 'replace-text'):
        if sys.argv[1] == 'replace-text':
            device.keyboard_keysym(0xffe3, 1)  # Control_L
            device.keyboard_keysym(ord('a'), 1)
            device.keyboard_keysym(ord('a'), 0)
            device.keyboard_keysym(0xffe3, 0)
            display.roundtrip()
        for character in sys.argv[2]:
            code = ord(character)
            keysym = code if code < 128 else 0x01000000 | code
            device.keyboard_keysym(keysym, 1)
            device.keyboard_keysym(keysym, 0)
            display.roundtrip()
    else:
        raise ValueError('Use click X Y, key KEYSYM, text TEXT or replace-text TEXT')

    display.roundtrip()
    # Keep the device alive until queued input events have reached the native UI.
    time.sleep(0.2)
