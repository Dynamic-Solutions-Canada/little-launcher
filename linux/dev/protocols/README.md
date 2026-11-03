# KDE test-input protocol

`fake-input.xml` is the KDE Plasma Wayland test-input protocol, interface version 6, retrieved from [KDE plasma-wayland-protocols](https://github.com/KDE/plasma-wayland-protocols/blob/master/src/protocols/fake-input.xml) on October 29, 2026. Its original LGPL-2.1-or-later notice is retained. Generate Python bindings inside the disposable fixture with the distro pywayland scanner. This protocol is a KDE implementation detail used only by the private QA input driver; production Little Launcher does not depend on it.

Protocol access is explicitly granted to the private test interpreter through the fixture desktop entry. Capture and input drivers additionally require its fixed private runtime and socket names. The production browser and native application receive no such grants.
