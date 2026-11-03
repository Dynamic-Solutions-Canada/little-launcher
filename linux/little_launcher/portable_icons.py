"""Bounded image content that travels with launchers, never another machine's paths."""
import base64
from copy import deepcopy
import hashlib
from pathlib import Path

from .document import DocumentError
from .storage import atomic_write

MAX_ICON_BYTES = 1024 * 1024


def icon_bytes(value):
    if not isinstance(value, str) or len(value) > 4 * ((MAX_ICON_BYTES + 2) // 3):
        raise DocumentError('A synced launcher icon is too large or malformed.')
    try:
        raw = base64.b64decode(value, validate=True)
    except ValueError as error:
        raise DocumentError('A synced launcher icon is malformed.') from error
    if not raw or len(raw) > MAX_ICON_BYTES:
        raise DocumentError('A synced launcher icon is empty or too large.')
    # Raster formats understood on both platforms. No SVG or executable icon containers.
    if not (raw.startswith(b'\x89PNG\r\n\x1a\n') or raw.startswith(b'\x00\x00\x01\x00')
            or raw.startswith(b'\xff\xd8\xff') or raw.startswith((b'GIF87a', b'GIF89a'))
            or raw.startswith(b'BM')):
        raise DocumentError('A synced launcher icon is not a supported image.')
    return raw


def preview_bytes(value):
    raw=icon_bytes(value)
    if len(raw)<24 or not raw.startswith(b'\x89PNG\r\n\x1a\n') or not 0<int.from_bytes(raw[16:20],'big')<=1024 or not 0<int.from_bytes(raw[20:24],'big')<=1024:
        raise DocumentError('A portable glyph preview must be a bounded PNG image.')
    return raw


def pack_icons(document, render_glyph=None):
    """Capture readable local custom files on a detached document; retain unavailable assets."""
    result = deepcopy(document)
    for launcher in result['Launchers']:
        mode=launcher.get('TrayIconMode','')
        if isinstance(mode,str) and mode.startswith('Glyph:'):
            if launcher.get('TrayIconPreviewMode')!=mode or not launcher.get('TrayIconPreviewData'):
                rendered=render_glyph(launcher) if render_glyph else None
                if rendered:
                    preview_bytes(rendered);launcher['TrayIconPreviewData']=rendered;launcher['TrayIconPreviewMode']=mode
        else:
            launcher.pop('TrayIconPreviewData',None);launcher.pop('TrayIconPreviewMode',None)
        if launcher.get('TrayIconMode') != 'Custom':
            launcher.pop('CustomTrayIconData', None)
            continue
        path = Path(launcher.get('CustomTrayIconPath', ''))
        if path.is_file():
            if path.stat().st_size > MAX_ICON_BYTES:
                raise DocumentError('Choose a custom launcher icon smaller than 1 MiB before uploading.')
            encoded = base64.b64encode(path.read_bytes()).decode('ascii')
            icon_bytes(encoded)
            launcher['CustomTrayIconData'] = encoded
        elif launcher.get('CustomTrayIconData'):
            icon_bytes(launcher['CustomTrayIconData'])
    return result


def unpack_icons(document, directory):
    """Materialize content under a hash-derived filename; never write a remote path."""
    result = deepcopy(document)
    for launcher in result['Launchers']:
        if launcher.get('TrayIconPreviewData'):preview_bytes(launcher['TrayIconPreviewData'])
        value = launcher.get('CustomTrayIconData')
        if launcher.get('TrayIconMode') != 'Custom' or not value:
            continue
        raw = icon_bytes(value)
        suffix = '.ico' if raw.startswith(b'\x00\x00\x01\x00') else '.img'
        path = directory / 'icons' / ('sync-' + hashlib.sha256(raw).hexdigest() + suffix)
        if not path.exists() or path.read_bytes() != raw:
            atomic_write(path, raw)
        launcher['CustomTrayIconPath'] = str(path)
    return result


def preserve_icons(existing,incoming):
    """An older peer's unresolved paths must not erase an available local image."""
    result=deepcopy(incoming);mode=result.get('TrayIconMode')
    if mode==existing.get('TrayIconMode'):
        if mode=='Custom' and not result.get('CustomTrayIconData') and not Path(result.get('CustomTrayIconPath','')).is_file():
            old_path=existing.get('CustomTrayIconPath','')
            if Path(old_path).is_file()or existing.get('CustomTrayIconData'):
                result['CustomTrayIconPath']=old_path
                if existing.get('CustomTrayIconData'):result['CustomTrayIconData']=existing['CustomTrayIconData']
        if isinstance(mode,str) and mode.startswith('Glyph:') and not result.get('TrayIconPreviewData') and existing.get('TrayIconPreviewMode')==mode:
            for key in ('TrayIconPreviewMode','TrayIconPreviewData'):
                if key in existing:result[key]=existing[key]
    return result
