// Only Little Launcher's own mapped flyouts carry this protocol caption.
// Anchor coordinates are captured by StatusNotifierItem.Activate, never read from the later cursor.
(function () {
    function protocol(window) {
        var application = String(window.resourceClass);
        if (application !== 'io.github.RyanEwen.LittleLauncher' && application !== 'io.github.RyanEwen.LittleLauncher.Web') return;
        return /^LittleLauncher(?:Flyout|Window):([a-zA-Z0-9-]+):(-?\d+):(-?\d+)(:native)?(:pinned)?(:owned)?(:focus)?(?::at([1-9]))?(?::last:(-?\d+):(-?\d+))?$/.exec(window.caption);
    }

    function place(window) {
        var match = protocol(window);
        if (!match) return;
        var regular = window.caption.indexOf('LittleLauncherWindow:') === 0;
        if (!regular) {
            window.skipTaskbar = true;
            window.skipPager = true;
            window.skipSwitcher = true;
            window.keepAbove = true;
        }
        var point = {x: Number(match[2]), y: Number(match[3])};
        // Plasma SNI popupPosition uses native pixels when XwaylandClientsScale is enabled.
        // Protocol callers that already hold logical coordinates never carry :native.
        var output;
        if (match[4]) {
            var screens = workspace.screens;
            for (var index = 0; index < screens.length; index++) {
                var candidate = screens[index];
                var bounds = candidate.geometry;
                var scale = candidate.devicePixelRatio || 1;
                var local = {x: bounds.x + (point.x - bounds.x) / scale,
                             y: bounds.y + (point.y - bounds.y) / scale};
                if (local.x >= bounds.x && local.x < bounds.x + bounds.width &&
                    local.y >= bounds.y && local.y < bounds.y + bounds.height) {
                    point = local;
                    output = candidate;
                    break;
                }
            }
        } else {
            output = workspace.screenAt(point);
        }
        if (!output) return;
        if (match[9]) output = workspace.screenAt({x:Number(match[9]),y:Number(match[10])}) || output;
        var area = workspace.clientArea(KWin.PlacementArea, output, workspace.currentDesktop);
        var screen = workspace.clientArea(KWin.ScreenArea, output, workspace.currentDesktop);
        var geometry = window.frameGeometry;
        var width = Math.min(geometry.width, area.width - 16);
        var height = Math.min(geometry.height, area.height - 16);
        // Match Windows tray placement: a horizontal-panel corner is not a vertical panel.
        // Overflow icons lie inside the work area and must open above the clicked icon.
        var gap = 8;
        var threshold = 16;
        var nearLeft = area.x > screen.x && point.x <= area.x + threshold;
        var nearRight = area.x + area.width < screen.x + screen.width &&
                        point.x >= area.x + area.width - threshold;
        var nearBottom = point.y >= area.y + area.height - threshold;
        var nearTop = point.y <= area.y + threshold;
        var x = point.x - width / 2;
        var y = point.y - height - gap;
        if (nearLeft || nearRight) {
            x = nearLeft ? area.x + gap : area.x + area.width - width - gap;
            y = point.y - height / 2;
        } else if (nearBottom) {
            y = area.y + area.height - height - gap;
        } else if (nearTop) {
            y = area.y + gap;
        }
        if (match[8]) {
            var anchor = Number(match[8]) - 1;
            var column = anchor % 3;
            var row = Math.floor(anchor / 3);
            x = area.x + gap + column * (area.width - width - 2 * gap) / 2;
            y = area.y + gap + row * (area.height - height - 2 * gap) / 2;
        }
        if (match[9]) {x=Number(match[9]);y=Number(match[10]);}
        x = Math.max(area.x + gap, Math.min(area.x + area.width - width - gap, x));
        y = Math.max(area.y + gap, Math.min(area.y + area.height - height - gap, y));
        window.frameGeometry = {x:Math.round(x), y:Math.round(y), width:width, height:height};
    }
    function focusShortcut(window) {
        // A Wayland Qt tool window may map without focus after a D-Bus tray activation.
        // It must become active once so its ordinary WindowDeactivate path can dismiss it.
        if (protocol(window) && String(window.resourceClass) === 'io.github.RyanEwen.LittleLauncher') {
            workspace.activeWindow = window;
        }
    }

    function watchFlyoutSize(window) {
        if (!protocol(window) || String(window.resourceClass) === 'io.github.RyanEwen.LittleLauncher.Web') return;
        var width = window.frameGeometry.width;
        var height = window.frameGeometry.height;
        function resized() {
            var geometry = window.frameGeometry;
            if (geometry.width === width && geometry.height === height) return;
            width = geometry.width;
            height = geometry.height;
            // An explicit edit resize retains the original activation edge. Focus never moves it.
            place(window);
        }
        window.frameGeometryChanged.connect(resized);
        function removed(candidate) {
            if (candidate !== window) return;
            window.frameGeometryChanged.disconnect(resized);
            workspace.windowRemoved.disconnect(removed);
        }
        workspace.windowRemoved.connect(removed);
    }

    // A toolbar follows its owner; moving it must never move or refocus the launcher.
    function placeToolbar(toolbar) {
        if (String(toolbar.resourceClass) !== 'io.github.RyanEwen.LittleLauncher') return;
        var match = /^LittleLauncherToolbar:([a-zA-Z0-9-]+)$/.exec(toolbar.caption);
        if (!match) return;
        toolbar.skipTaskbar = true;
        toolbar.skipPager = true;
        toolbar.skipSwitcher = true;
        var owner = toolbar.transientFor;
        if (!owner) {
            owner = workspace.windowList().find(function (candidate) {
                var target = protocol(candidate);
                return target && target[1] === match[1] &&
                    String(candidate.resourceClass) === 'io.github.RyanEwen.LittleLauncher';
            });
        }
        if (!owner) return;
        function position() {
            if (workspace.windowList().indexOf(toolbar) < 0 || workspace.windowList().indexOf(owner) < 0) return;
            var frame = owner.frameGeometry;
            var output = workspace.screenAt({x:frame.x+frame.width/2, y:frame.y+frame.height/2});
            if (!output) return;
            var area = workspace.clientArea(KWin.PlacementArea, output, workspace.currentDesktop);
            var size = toolbar.frameGeometry;
            var x = Math.max(area.x, Math.min(frame.x+frame.width-size.width, area.x+area.width-size.width));
            var y = frame.y-size.height-6;
            if (y < area.y) y = frame.y+frame.height+6;
            y = Math.max(area.y, Math.min(y, area.y+area.height-size.height));
            toolbar.frameGeometry = {x:Math.round(x), y:Math.round(y), width:size.width, height:size.height};
        }
        position();
        owner.frameGeometryChanged.connect(position);
        function removed(window) {
            if (window !== toolbar && window !== owner) return;
            owner.frameGeometryChanged.disconnect(position);
            workspace.windowRemoved.disconnect(removed);
        }
        workspace.windowRemoved.connect(removed);
    }

    function restoreToolbarOwner(active) {
        if (!active || String(active.resourceClass) !== 'io.github.RyanEwen.LittleLauncher') return;
        if (!/^LittleLauncherToolbar:([a-zA-Z0-9-]+)$/.test(active.caption)) return;
        var owner = active.transientFor;
        if (owner && String(owner.resourceClass) === 'io.github.RyanEwen.LittleLauncher') {
            workspace.activeWindow = owner;
        }
    }

    function belongsTo(active, owner) {
        // Native portal dialogs and sign-in popups retain their transient ownership chain.
        var current = active;
        while (current) {
            if (current === owner) return true;
            current = current.transientFor;
        }
        return false;
    }

    function dismissOnFocus(active) {
        // KWin emits a null activation during first mapping and between real focus targets.
        if (!active) return;
        workspace.windowList().forEach(function (window) {
            var match = protocol(window);
            if (!match || window.caption.indexOf('LittleLauncherWindow:') === 0 || match[5] || match[6]) return;
            if (belongsTo(active, window)) return;
            // Permission prompts are native Qt application-modal dialogs in the parent process.
            if (active && active.modal && String(active.resourceClass) === 'io.github.RyanEwen.LittleLauncher') return;
            window.closeWindow();
        });
    }

    function watchShortcutState(window) {
        if (!protocol(window) || String(window.resourceClass) !== 'io.github.RyanEwen.LittleLauncher') return;
        function changed() {
            var state = protocol(window);
            if (state && state[7]) workspace.activeWindow = window;
            else dismissOnFocus(workspace.activeWindow);
        }
        window.captionChanged.connect(changed);
        function removed(candidate) {
            if (candidate !== window) return;
            window.captionChanged.disconnect(changed);
            workspace.windowRemoved.disconnect(removed);
        }
        workspace.windowRemoved.connect(removed);
    }

    function watchDraggedPosition(window) {
        var match = protocol(window);
        if (!match || String(window.resourceClass) !== 'io.github.RyanEwen.LittleLauncher.Web') return;
        var identifier = match[1];
        function finished() {
            if (window.fullScreen) return;
            var frame = window.frameGeometry;
            callDBus('org.littlelauncher.Placement', '/Placement', 'org.littlelauncher.Placement', 'Record', identifier, Math.round(frame.x), Math.round(frame.y));
        }
        window.interactiveMoveResizeFinished.connect(finished);
        function removed(candidate) {
            if (candidate !== window) return;
            window.interactiveMoveResizeFinished.disconnect(finished);
            workspace.windowRemoved.disconnect(removed);
        }
        workspace.windowRemoved.connect(removed);
    }

    workspace.windowAdded.connect(place);
    workspace.windowAdded.connect(watchDraggedPosition);
    workspace.windowAdded.connect(placeToolbar);
    workspace.windowAdded.connect(watchFlyoutSize);
    workspace.windowAdded.connect(focusShortcut);
    workspace.windowAdded.connect(watchShortcutState);
    // Focus changes dismiss only. Never reposition an existing window after an owned dialog.
    workspace.windowActivated.connect(restoreToolbarOwner);
    workspace.windowActivated.connect(dismissOnFocus);
})();
