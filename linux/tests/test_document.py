"""Data-integrity checks for cross-platform filtering and lossless persistence."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

from little_launcher.document import (
    DocumentError, launcher_hidden, new_launcher, new_shortcut, require_sync_compatibility,
    normalize_web_address, shows_bookmark_bar, sync_envelope, launcher_supported, validate_document, visible_items, web_address,
)
from little_launcher.storage import ConcurrentEditError, DocumentStore


class CrossPlatformDocumentTests(unittest.TestCase):
    def test_hidden_items_and_unknown_metadata_survive_linux_edits(self):
        original = {"Launchers": [{"Id": "same", "Name": "Apps", "FutureLauncher": 7, "Items": [
            {"Name": "Windows", "Path": "C:\\Apps\\editor.exe", "FutureItem": {"opaque": True}},
            new_shortcut("Linux", "/usr/bin/editor"),
        ]}], "FutureSetting": [1, 2]}
        with tempfile.TemporaryDirectory() as directory:
            store = DocumentStore(Path(directory) / "settings.json")
            _, revision = store.read()
            revision = store.commit(original, revision)
            document, revision = store.read()
            self.assertEqual([i["Name"] for i in visible_items(document["Launchers"][0]["Items"])], ["Windows", "Linux"])
            document["Launchers"][0]["Name"] = "Renamed"
            store.commit(document, revision)
            saved, _ = store.read()
            self.assertEqual(saved["Launchers"][0]["Items"], original["Launchers"][0]["Items"])
            self.assertEqual(saved["FutureSetting"], [1, 2])

    def test_projection_closes_empty_columns_and_groups_without_mutation(self):
        windows = {"Name": "Windows", "Path": "Z:\\offline\\app.exe"}
        linux = new_shortcut("Linux", "/not/installed/yet")
        items = [{"Name": "Empty", "Path": ""}, windows, {"IsColumnBreak": True},
                 {"IsGroup": True, "Name": "Empty group", "Children": [windows]},
                 {"IsColumnBreak": True}, {"Name": "Linux", "Path": ""}, linux,
                 {"IsColumnBreak": True}]
        before = deepcopy(items)
        self.assertEqual([item.get("Name") for item in visible_items(items)], ["Empty", "Windows", None, "Empty group", None, "Linux", "Linux"])
        self.assertEqual(items, before)

    def test_missing_local_application_does_not_change_os_eligibility(self):
        item = new_shortcut("Missing", "/a/nonexistent/app")
        self.assertEqual(visible_items([item]), [item])

    def test_explicit_assignment_overrides_legacy_inference(self):
        item = {"Path": "C:\\portable.exe", "SupportedOperatingSystems": []}
        self.assertEqual(validate_document({"Launchers":[{"Id":"legacy","Items":[item]}]})["Launchers"][0]["Items"][0],item)
        self.assertEqual(visible_items([item]), [item])

    def test_url_is_portable_and_new_application_is_linux_specific(self):
        self.assertNotIn("SupportedOperatingSystems",new_shortcut("Site", "https://example.com", True))
        self.assertNotIn("SupportedOperatingSystems",new_shortcut("App", "/bin/app"))
        self.assertEqual(new_launcher("Linux")["Platform"],"linux")
        self.assertFalse(launcher_supported(new_launcher("Linux"),"windows"))
        self.assertTrue(launcher_supported({"Kind":1,"Platform":"future-os"},"linux"))

    def test_foreign_only_launcher_hides_but_empty_new_launcher_remains_reachable(self):
        self.assertTrue(launcher_hidden({"Items": [{"Path": "C:\\app.exe"}]}))
        self.assertFalse(launcher_hidden(new_launcher("New")))
        self.assertFalse(launcher_hidden({"Kind": 1, "Items": [{"Path": "C:\\app.exe"}]}))

    def test_sync_exports_full_collection_and_rejects_old_writer(self):
        document = {"Launchers": [{"Id": "same", "Items": [{"Path": "C:\\app.exe"}]}]}
        envelope = sync_envelope(document)
        require_sync_compatibility(envelope)
        self.assertEqual(envelope["Launchers"], document["Launchers"])
        envelope.pop("PlatformVisibilityVersion")
        with self.assertRaisesRegex(DocumentError, "Update Little Launcher on Windows"):
            require_sync_compatibility(envelope)

    def test_address_validation_accepts_hosts_and_rejects_non_web_protocols(self):
        self.assertEqual(normalize_web_address('example.com/path'), 'https://example.com/path')
        self.assertEqual(normalize_web_address('localhost:3000/path'), 'https://localhost:3000/path')
        for address in ['file:///etc/passwd', 'javascript:alert(1)', 'https://[bad', 'https://example.com:bad']:
            with self.assertRaises(DocumentError):
                normalize_web_address(address)

    def test_modern_home_and_bar_choice_are_independent_of_bookmark_order(self):
        launcher = {'WebHomeUrl': 'https://home.example', 'WebShowBookmarkBar': False,
                    'WebBookmarks': [{'Name': 'First', 'Url': 'https://first.example'},
                                     {'Name': 'Second', 'Url': 'https://second.example'}]}
        self.assertEqual(web_address(launcher), 'https://home.example')
        self.assertFalse(shows_bookmark_bar(launcher))
        launcher['WebBookmarks'].reverse()
        self.assertEqual(web_address(launcher), 'https://home.example')
        launcher['WebShowBookmarkBar'] = None
        self.assertTrue(shows_bookmark_bar(launcher))

    def test_legacy_home_uses_windows_migration_precedence_without_mutating_bookmarks(self):
        launcher = {'WebDefaultBookmarkUrl': 'https://default.example', 'WebUrl': 'https://legacy.example',
                    'WebBookmarks': [{'IsFolder': True, 'Children': [{'Url': 'https://child.example'}]},
                                     {'Url': 'https://first.example'}]}
        before = deepcopy(launcher)
        self.assertEqual(web_address(launcher), 'https://default.example')
        self.assertEqual(launcher, before)
        launcher.pop('WebDefaultBookmarkUrl')
        self.assertEqual(web_address(launcher), 'https://legacy.example')
        launcher.pop('WebUrl')
        self.assertEqual(web_address(launcher), 'https://first.example')

    def test_malformed_recursive_collection_is_rejected(self):
        with self.assertRaises(DocumentError):
            validate_document({"Launchers": [{"Id": "same", "Items": [{"Children": "bad"}]}]})

    def test_duplicate_launcher_identity_is_rejected(self):
        with self.assertRaises(DocumentError):
            validate_document({"Launchers": [{"Id": "same"}, {"Id": "same"}]})

    def test_stale_editor_cannot_overwrite_another_writer(self):
        with tempfile.TemporaryDirectory() as directory:
            store = DocumentStore(Path(directory) / "settings.json")
            document, revision = store.read()
            store.commit(document, revision)
            with self.assertRaises(ConcurrentEditError):
                store.commit({"Launchers": [new_launcher("Stale")]}, revision)

    def test_corrupt_settings_are_preserved_instead_of_reset(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings.json"
            path.write_bytes(b"{broken")
            with self.assertRaises(DocumentError):
                DocumentStore(path).read()
            self.assertEqual(path.read_bytes(), b"{broken")


if __name__ == "__main__":
    unittest.main()
