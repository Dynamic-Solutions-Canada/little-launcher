"""Allowlisted trusted-chrome mutations; page content never supplies portable settings."""
BOOLEAN_PREFERENCES = frozenset(('WebShowAddressBar', 'WebAlwaysShowTabs', 'WebShowBookmarkBar',
    'WebBookmarkIconsOnly', 'WebPinFlyout', 'WebRegularWindow', 'WebReloadOnShow', 'WebLinksInBrowser', 'WebWindowAutoHide', 'WebLockSize', 'WebFullScreenInWindow', 'WebFitFullscreenToVideo'))
INTEGER_PREFERENCES = {'WebZoomPercent': (25, 400), 'WebHiddenPolicy': (0, 2),
    'WebIdleUnloadMinutes': (1, 720), 'WebAnchor': (0, 10), 'WebFlyoutWidth': (320, 4000), 'WebFlyoutHeight': (240, 4000)}


def valid_preference(key, value):
    if key in BOOLEAN_PREFERENCES:
        return type(value) is bool
    limits = INTEGER_PREFERENCES.get(key)
    return bool(limits and type(value) is int and limits[0] <= value <= limits[1])
