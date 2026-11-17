"""Shared translation catalogs, language matching and the Python/C# language list contract."""
import json
from pathlib import Path
import re
import unittest

from little_launcher import i18n

ROOT = Path(__file__).resolve().parents[2]
CATALOGS = ROOT / 'Shared' / 'Localization'
PLACEHOLDER = re.compile(r'\{\{([A-Za-z][A-Za-z0-9]*)\}\}')


def area_files(catalog):
    """Every area file of one catalog, by file name, parsed strictly (duplicate keys rejected)."""
    def no_duplicates(pairs):
        keys = [key for key, _ in pairs]
        duplicates = {key for key in keys if keys.count(key) > 1}
        if duplicates:
            raise ValueError(f'duplicate keys {sorted(duplicates)}')
        return dict(pairs)
    return {path.name: json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=no_duplicates)
            for path in sorted((CATALOGS / catalog).glob('*.json'))}


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.english = area_files('en-US')

    def test_every_catalog_directory_is_a_supported_wording_catalog(self):
        directories = {path.name for path in CATALOGS.iterdir() if path.is_dir()}
        self.assertEqual(directories, set(i18n.CATALOGS.values()))

    def test_keys_live_in_the_file_their_prefix_names_and_are_unique(self):
        seen = {}
        for name, entries in self.english.items():
            area = name.removesuffix('.json')
            for key, value in entries.items():
                self.assertEqual(key.split('.')[0], area, f'{key} is in {name}')
                self.assertNotIn(key, seen, f'{key} in both {name} and {seen.get(key)}')
                seen[key] = name
                self.assertIsInstance(value, str)
                self.assertTrue(value.strip(), f'{key} is empty')
                self.assertNotIn('\u2014', value, f'{key} contains an em dash')

    def test_plural_keys_come_in_pairs(self):
        keys = {key for entries in self.english.values() for key in entries}
        for key in keys:
            if key.endswith('.one'):
                self.assertIn(key[:-4] + '.other', keys)
            if key.endswith('.other'):
                self.assertIn(key[:-6] + '.one', keys)

    def test_translations_match_english_keys_and_placeholders(self):
        for catalog in sorted(set(i18n.CATALOGS.values()) - {'en-US'}):
            translated = area_files(catalog)
            with self.subTest(catalog=catalog):
                self.assertEqual(set(translated), set(self.english), 'area files differ')
                for name, entries in self.english.items():
                    other = translated[name]
                    self.assertEqual(set(other), set(entries), f'{catalog}/{name} keys differ')
                    for key, text in entries.items():
                        value = other[key]
                        self.assertTrue(isinstance(value, str) and value.strip(), f'{catalog} {key} is empty')
                        self.assertEqual(sorted(set(PLACEHOLDER.findall(value))), sorted(set(PLACEHOLDER.findall(text))),
                                         f'{catalog} {key} placeholders differ')


class CodeReferenceTests(unittest.TestCase):
    """Every catalog key the Windows or Linux code names must exist in English.

    Catches a key renamed in one place but not another, which compiles and then shows the raw key.
    Literals are recognised by a known area prefix; file names such as settings.json are skipped.
    """
    SOURCES = ('LittleLauncher/**/*.cs', 'LittleLauncher/**/*.xaml', 'LauncherShortcut/**/*.cs', 'Shared/*.cs',
               'linux/little_launcher/*.py', 'linux/browser/*.cjs', 'linux/browser/*.js', 'linux/browser/*.html')
    LITERAL = re.compile(r"""["'`]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)["'`]|Key=([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""")

    def test_referenced_keys_exist(self):
        english = {key for entries in area_files('en-US').values() for key in entries}
        areas = {key.split('.')[0] for key in english}
        missing = []
        for pattern in self.SOURCES:
            for path in ROOT.glob(pattern):
                if {'obj', 'bin', 'node_modules'} & set(path.parts):
                    continue
                for match in self.LITERAL.finditer(path.read_text(encoding='utf-8', errors='replace')):
                    key = match.group(1) or match.group(2)
                    if key.split('.')[0] not in areas or key.endswith(('.json', '.xml', '.txt')):
                        continue
                    # A prefix used to build a key from a fixed table (icons.color.<name>) is fine.
                    if key in english or key + '.one' in english or any(entry.startswith(key + '.') for entry in english):
                        continue
                    missing.append(f'{path.relative_to(ROOT)}: {key}')
        self.assertEqual(missing, [])


class LanguageListTests(unittest.TestCase):
    def test_python_list_matches_supported_languages_cs(self):
        source = (ROOT / 'Shared' / 'SupportedLanguages.cs').read_text(encoding='utf-8')
        ordered = re.search(r'Ordered \{ get; \} =\s*\[([^\]]*)\]', source).group(1)
        self.assertEqual(tuple(re.findall(r'"([^"]+)"', ordered)), i18n.ORDERED)
        catalogs = dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', source.split('NativeNames')[0]))
        self.assertEqual(catalogs, i18n.CATALOGS)
        names = dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', source.split('NativeNames')[1]))
        self.assertEqual(names, i18n.NATIVE_NAMES)


class ResolveTests(unittest.TestCase):
    def test_exact_regional_and_family_matching(self):
        self.assertEqual(i18n.resolve(['fr-CA']), 'fr-CA')
        self.assertEqual(i18n.resolve(['fr-BE']), 'fr-FR')
        self.assertEqual(i18n.resolve(['en-GB']), 'en-US')
        self.assertEqual(i18n.resolve(['pt-PT']), 'pt-BR')
        self.assertEqual(i18n.resolve(['de_AT.UTF-8']), 'de-DE')
        self.assertEqual(i18n.resolve(['en-Latn-CA']), 'en-CA')
        self.assertEqual(i18n.resolve(['ja-JP-u-ca-japanese']), 'ja-JP')

    def test_order_unsupported_and_chinese_scripts(self):
        self.assertEqual(i18n.resolve(['it-IT', 'es-MX', 'en-US']), 'es-ES')
        self.assertEqual(i18n.resolve(['it-IT']), 'en-US')
        self.assertEqual(i18n.resolve(['C', '']), 'en-US')
        self.assertEqual(i18n.resolve(['zh-TW']), 'en-US')
        self.assertEqual(i18n.resolve(['zh-Hant-CN', 'ja']), 'ja-JP')
        self.assertEqual(i18n.resolve(['zh-SG']), 'zh-CN')
        self.assertEqual(i18n.resolve(['zh-Hans-HK']), 'zh-CN')

    def test_override_wins_and_unknown_override_means_match_device(self):
        try:
            i18n.configure(lambda: 'ja-jp', lambda: ['fr-FR'])
            self.assertEqual(i18n.locale(), 'ja-JP')
            i18n.configure(lambda: 'it-IT', lambda: ['fr-FR'])
            self.assertEqual(i18n.locale(), 'fr-FR')
            i18n.configure(lambda: '', lambda: ['fr-CA'])
            self.assertEqual(i18n.locale(), 'fr-CA')
        finally:
            i18n.configure(lambda: None, lambda: [])


class LookupTests(unittest.TestCase):
    def tearDown(self):
        i18n.configure(lambda: None, lambda: [])

    def test_fallback_and_single_pass_placeholders(self):
        i18n.configure(lambda: 'en-US', lambda: [])
        self.assertEqual(i18n.t('common.ok'), 'OK')
        self.assertEqual(i18n.t('no.such.key'), 'no.such.key')
        text = i18n.t('system.language.matchDevice', language='{{language}}')
        self.assertEqual(text, 'Match device ({{language}})')

    def test_plural_rules(self):
        self.assertTrue(i18n._uses_one('en-US', 1))
        self.assertFalse(i18n._uses_one('en-US', 0))
        self.assertTrue(i18n._uses_one('fr-CA', 0))
        self.assertTrue(i18n._uses_one('pt-BR', 1))
        self.assertFalse(i18n._uses_one('ja-JP', 1))


if __name__ == '__main__':
    unittest.main()
