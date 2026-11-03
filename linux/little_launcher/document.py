"""Lossless launcher documents and OS-specific display projections.

Projection never mutates persisted collections. Editors operate on the full document,
so hiding another OS's rows cannot erase them during save, reorder, or sync.
"""
from __future__ import annotations

from copy import deepcopy
from datetime import datetime, timezone
import re
from typing import Any
from uuid import uuid4

JsonObject = dict[str, Any]
VISIBILITY_VERSION = 2


class DocumentError(ValueError):
    """The input cannot safely be interpreted as a launcher document."""


def validate_document(value: Any) -> JsonObject:
    """Validate known collection shapes while retaining all unrecognized fields."""
    if not isinstance(value, dict) or not isinstance(value.get("Launchers"), list):
        raise DocumentError("Choose a settings or launchers file containing a Launchers list.")
    identifiers: set[str] = set()
    for launcher in value["Launchers"]:
        if not isinstance(launcher, dict):
            raise DocumentError("A launcher entry is not an object.")
        identifier = launcher.get("Id")
        if not isinstance(identifier, str) or not identifier or identifier in identifiers:
            raise DocumentError("Every launcher must have a unique, nonempty Id.")
        identifiers.add(identifier)
        for field in ('Platform', 'TrayIconPreviewData', 'TrayIconPreviewMode', 'Name', 'WebUrl', 'WebHomeUrl', 'WebDefaultBookmarkUrl', 'CustomTrayIconPath'):
            if field in launcher and not isinstance(launcher[field], str):
                raise DocumentError(f'{field} must be text.')
        for field in ('NIconHide', 'ShowTitle', 'WebSharedProfile', 'WebRegularWindow', 'WebPinFlyout', 'WebLockSize', 'WebReloadOnShow', 'WebLinksInBrowser', 'WebWindowAutoHide', 'WebShowAddressBar', 'WebAlwaysShowTabs', 'WebShowBookmarkBar', 'IsShared', 'IsSharedOwner', 'SharedTwoWay'):
            if field in launcher and type(launcher[field]) is not bool:
                raise DocumentError(f'{field} must be true or false.')
        for field in ('Kind', 'ViewMode', 'WebHiddenPolicy', 'WebFlyoutWidth', 'WebFlyoutHeight', 'IconModeIconsPerRow', 'WebIdleUnloadMinutes', 'WebZoomPercent', 'WebAnchor'):
            if field in launcher and (type(launcher[field]) is not int or launcher[field] < 0):
                raise DocumentError(f'{field} must be a nonnegative whole number.')
        for collection in ("Items", "WebBookmarks"):
            _validate_items(launcher.get(collection, []))
    return deepcopy(value)


def _validate_items(items: Any) -> None:
    """Reject malformed recursive lists before they reach editing or persistence."""
    if not isinstance(items, list):
        raise DocumentError("A launcher item collection is not a list.")
    for item in items:
        if not isinstance(item, dict):
            raise DocumentError("A launcher item is not an object.")
        for field in ('Name', 'Path', 'Url', 'Arguments', 'IconPath'):
            if field in item and not isinstance(item[field], str):
                raise DocumentError(f'{field} must be text.')
        for field in ('IsWebsite', 'IsPwa', 'IsGroup', 'IsColumnBreak'):
            if field in item and type(item[field]) is not bool:
                raise DocumentError(f'{field} must be true or false.')
        targets = item.get("SupportedOperatingSystems")
        if targets is not None and (
            not isinstance(targets, list) or any(not isinstance(target, str) for target in targets)
        ):
            raise DocumentError("Supported operating systems must be a list of names.")
        if "Children" in item:
            _validate_items(item["Children"])


def is_heading(item: JsonObject) -> bool:
    """Windows represents a heading by an empty target, not an extra persisted flag."""
    return not item.get("Path") and not item.get("IsGroup") and not item.get("IsColumnBreak")


def visible_items(items: list[JsonObject], operating_system: str = "linux") -> list[JsonObject]:
    """Build a detached presentation list, removing empty groups, headings, and columns."""
    result: list[JsonObject] = []
    pending_heading: JsonObject | None = None
    pending_break: JsonObject | None = None
    for original in items:
        item = deepcopy(original)
        if item.get("IsColumnBreak"):
            pending_break = item
            pending_heading = None
            continue
        if is_heading(item):
            pending_heading = item
            continue
        if item.get("IsGroup"):
            item["Children"] = visible_items(item.get("Children", []), operating_system)
            if not item["Children"]:
                continue
        if pending_break is not None and result:
            result.append(pending_break)
        if pending_heading is not None:
            result.append(pending_heading)
        result.append(item)
        pending_break = None
        pending_heading = None
    return result


def launcher_supported(launcher: JsonObject, operating_system: str = 'linux') -> bool:
    """Shortcut launchers belong to one OS. Untagged legacy launchers came from Windows."""
    return launcher.get('Kind',0)==1 or launcher.get('Platform','windows')==operating_system


def launcher_hidden(launcher: JsonObject, operating_system: str = 'linux') -> bool:
    return not launcher_supported(launcher, operating_system)


def new_launcher(name: str, web: bool = False) -> JsonObject:
    """Create Linux defaults without changing defaults of imported Windows launchers."""
    return {
        "Id": str(uuid4()), "Name": name.strip() or "Launcher", "Kind": int(web),
        "Items": [], "WebBookmarks": [], "WebSharedProfile": False, "Platform": "linux",
        "WebShowBookmarkBar": bool(web),
    }


def new_shortcut(name: str, target: str, website: bool = False) -> JsonObject:
    """Create an explicit OS assignment at entry time, leaving ordinary URLs portable."""
    return {
        "Name": name.strip(), "Path": target.strip(), "IsWebsite": website,
    }


def sync_envelope(document: JsonObject) -> JsonObject:
    """Export all launchers, including hidden rows, with a Windows-compatibility marker."""
    envelope = {
        "LastModified": datetime.now(timezone.utc).isoformat(),
        "PlatformVisibilityVersion": VISIBILITY_VERSION,
        "Launchers": deepcopy(document["Launchers"]),
    }
    for launcher in envelope['Launchers']:
        for field in ('WebFlyoutPosition', 'WebSessionTabs', 'WebSessionActiveTab', 'PinAumid'):
            launcher.pop(field, None)
    if 'Extensions' in document:
        envelope['Extensions'] = deepcopy(document['Extensions'])
    return envelope


def require_sync_compatibility(envelope: JsonObject) -> None:
    """Refuse automated cross-platform sync through an older, unqualified writer."""
    validate_document(envelope)
    version = envelope.get("PlatformVisibilityVersion", 0)
    if type(version) is not int or version < VISIBILITY_VERSION:
        raise DocumentError(
            "Update Little Launcher on Windows before cross-platform sync. "
            "This file was written by a version that cannot preserve whole-launcher operating-system ownership."
        )


def web_address(launcher: JsonObject) -> str:
    """Resolve modern home independently of bookmarks, retaining legacy migration semantics."""
    for field in ('WebHomeUrl', 'WebDefaultBookmarkUrl', 'WebUrl'):
        value = launcher.get(field, '')
        if isinstance(value, str) and value.strip():
            return value.strip()
    first = next((item for item in launcher.get('WebBookmarks', []) if not item.get('IsFolder')), None)
    return first.get('Url', '') if first else ''


def shows_bookmark_bar(launcher: JsonObject) -> bool:
    """An explicit choice wins; older files derive visibility from top-level bookmark count."""
    choice = launcher.get('WebShowBookmarkBar')
    return bool(choice) if choice is not None else len(launcher.get('WebBookmarks', [])) > 1


def normalize_web_address(value: str, allow_empty: bool = False) -> str:
    """Accept HTTP(S) addresses and bare hosts; reject malformed ports and other protocols."""
    from urllib.parse import urlsplit
    target = value.strip()
    if not target and allow_empty:
        return ''
    try:
        parsed = urlsplit(target)
        if not parsed.scheme or re.match(r'^[^/\s:]+:\d+(/|$)', target):
            target = 'https://' + target
            parsed = urlsplit(target)
        if parsed.scheme not in ('http', 'https') or not parsed.hostname:
            raise ValueError()
        parsed.port
    except ValueError as error:
        raise DocumentError('Enter a valid HTTP or HTTPS website address.') from error
    return target
