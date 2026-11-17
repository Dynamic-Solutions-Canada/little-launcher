"""Interface language: the shared translation catalogs and Persistent's language rules.

The catalogs are the same JSON files the Windows app embeds (``Shared/Localization``); see
docs/localization.md. This module has no Qt dependency so the portable model and its tests can
use it; the application supplies the desktop's ordered language list at startup.
"""
from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Callable, Iterable

DEFAULT = 'en-US'

# Picker order. Must match Shared/SupportedLanguages.cs; test_localization checks it.
ORDERED = ('en-US', 'en-CA', 'es-ES', 'fr-FR', 'fr-CA', 'de-DE', 'zh-CN', 'pt-BR', 'ja-JP')

# Canadian identities deliberately reuse wording; their identity still matters for selection.
CATALOGS = {
    'en-US': 'en-US', 'en-CA': 'en-US', 'es-ES': 'es-ES', 'fr-FR': 'fr-FR', 'fr-CA': 'fr-FR',
    'de-DE': 'de-DE', 'zh-CN': 'zh-CN', 'pt-BR': 'pt-BR', 'ja-JP': 'ja-JP',
}

# Each language named in itself, so it can be found from any interface language. Never translated.
NATIVE_NAMES = {
    'en-US': 'English (United States)', 'en-CA': 'English (Canada)', 'es-ES': 'Español (España)',
    'fr-FR': 'Français (France)', 'fr-CA': 'Français (Canada)', 'de-DE': 'Deutsch (Deutschland)',
    'zh-CN': '简体中文（中国）', 'pt-BR': 'Português (Brasil)', 'ja-JP': '日本語（日本）',
}

_LATIN_DEFAULT = ('en', 'es', 'fr', 'de', 'pt')
_PLACEHOLDER = re.compile(r'\{\{([A-Za-z][A-Za-z0-9]*)\}\}')

_catalogs: dict[str, dict[str, str]] = {}
_override: Callable[[], str | None] = lambda: None
_device_preferences: Callable[[], Iterable[str]] = lambda: ()
_device_locale: str | None = None


def catalog_directory() -> Path:
    """Where the catalogs are: bundled beside the package in the AppImage, else the source tree."""
    package_root = Path(__file__).resolve().parents[1]
    bundled = package_root / 'localization'
    if bundled.is_dir():
        return bundled
    return package_root.parent / 'Shared' / 'Localization'


def configure(language_override: Callable[[], str | None], device_preferences: Callable[[], Iterable[str]] | None = None) -> None:
    """Connect the saved choice and the desktop's ordered display languages.

    ``language_override`` is read on every lookup; empty or unknown means Match device. The device
    list is resolved once and cached, so a desktop language change applies on the next start.
    """
    global _override, _device_preferences, _device_locale
    _override = language_override
    if device_preferences is not None:
        _device_preferences = device_preferences
    _device_locale = None


def normalize_override(value) -> str | None:
    """Return the canonical supported identity for an explicit choice, or None for Match device."""
    if not isinstance(value, str):
        return None
    tag = value.strip().lower()
    return next((locale for locale in ORDERED if locale.lower() == tag), None)


def _strip_default_script(tag: str) -> str:
    """Drop an explicit Latin script that the regional identity already implies (en-Latn-US)."""
    parts = tag.split('-')
    if len(parts) > 1 and parts[1].lower() == 'latn' and parts[0].lower() in _LATIN_DEFAULT:
        return '-'.join(part for index, part in enumerate(parts) if index != 1)
    return tag


def _family(tag: str) -> str:
    """Group regional wording without crossing an explicit Chinese script boundary."""
    parts = tag.split('-')
    if any(not part or len(part) > 8 or not part.isascii() or not part.isalnum() for part in parts):
        return ''
    language = parts[0].lower()
    script = next((part for part in parts[1:] if len(part) == 4 and part.isalpha()), None)
    if language == 'zh':
        if script:
            return 'zh-' + script.title()
        if any(part.upper() in ('TW', 'HK', 'MO') for part in parts[1:]):
            return 'zh-Hant'
        return 'zh-Hans'
    if script and script.lower() == 'latn' and language in _LATIN_DEFAULT:
        script = None
    return language if script is None else language + '-' + script.title()


def resolve(preferences: Iterable[str]) -> str:
    """Pick the first supported preference, exact identity before regional relative.

    Accepts BCP 47 (``fr-CA``) and POSIX (``fr_CA.UTF-8``) forms. Unsupported languages fall back
    to English; Traditional Chinese never selects Simplified Chinese.
    """
    for preference in preferences:
        if not isinstance(preference, str) or not preference.strip():
            continue
        # POSIX locale: drop the encoding and modifier, use hyphens.
        tag = preference.strip().split('.')[0].split('@')[0].replace('_', '-')
        if tag in ('C', 'POSIX'):
            continue
        # Unicode extensions belong to regional formatting, not wording identity.
        parts = tag.split('-')
        extension = next((index for index, part in enumerate(parts) if index > 0 and len(part) == 1), None)
        if extension is not None:
            tag = '-'.join(parts[:extension])
        tag = _strip_default_script(tag)
        exact = normalize_override(tag)
        if exact:
            return exact
        family = _family(tag)
        if family:
            relative = next((locale for locale in ORDERED if _family(locale) == family), None)
            if relative:
                return relative
    return DEFAULT


def device_locale() -> str:
    """The identity Match device currently resolves to."""
    global _device_locale
    if _device_locale is None:
        try:
            preferences = list(_device_preferences())
        except Exception:  # A broken desktop query must not stop the app; English is safe.
            preferences = []
        _device_locale = resolve(preferences)
    return _device_locale


def locale() -> str:
    """The supported identity currently in effect."""
    return normalize_override(_override()) or device_locale()


def read_catalog(catalog: str) -> dict[str, str]:
    """Merge every area file of one catalog. A broken file is skipped; lookups fall back to English."""
    if catalog in _catalogs:
        return _catalogs[catalog]
    merged: dict[str, str] = {}
    directory = catalog_directory() / catalog
    for path in sorted(directory.glob('*.json')) if directory.is_dir() else ():
        try:
            entries = json.loads(path.read_text(encoding='utf-8'))
        except (OSError, UnicodeError, ValueError):
            continue
        if isinstance(entries, dict):
            merged.update({key: value for key, value in entries.items() if isinstance(value, str)})
    _catalogs[catalog] = merged
    return merged


def _fill(template: str, values: dict) -> str:
    """Replace each known ``{{name}}`` once; inserted values are never expanded again."""
    if not values:
        return template
    return _PLACEHOLDER.sub(lambda match: str(values[match.group(1)]) if match.group(1) in values else match.group(0), template)


def t(key: str, /, **values) -> str:
    """The text for ``key`` in the current language, then English, then the key itself."""
    text = read_catalog(CATALOGS[locale()]).get(key)
    if text is None:
        text = read_catalog(DEFAULT).get(key, key)
    return _fill(text, values)


def _uses_one(identity: str, count: int) -> bool:
    language = identity.split('-')[0]
    if language in ('fr', 'pt'):
        return count in (0, 1)
    if language in ('zh', 'ja'):
        return False
    return count == 1


def plural(key: str, count: int, /, **values) -> str:
    """Count-dependent text from ``key.one`` / ``key.other``, with ``{{count}}`` supplied."""
    form = '.one' if _uses_one(locale(), count) else '.other'
    return t(key + form, count=count, **values)
