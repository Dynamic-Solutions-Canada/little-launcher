import json,stat,tempfile,unittest,zipfile
from pathlib import Path
from little_launcher.document import DocumentError
from little_launcher.extension_packages import extract_package,import_folder
class ExtensionPackageTests(unittest.TestCase):
    def test_valid_manifest_is_copied_to_stable_managed_package_and_source_kept(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);source=root/'source';source.mkdir();raw=json.dumps({'manifest_version':3,'name':'Fixture','version':'1.0','background':{'service_worker':'worker.js'}});(source/'manifest.json').write_text(raw);(source/'worker.js').write_text('console.log("fixture")')
            first=import_folder(source,root/'managed');second=import_folder(source,root/'managed')
            self.assertEqual(first,second);self.assertEqual((source/'manifest.json').read_text(),raw);self.assertEqual((first/'worker.js').read_text(),'console.log("fixture")')
    def test_archive_rejects_traversal_symlinks_and_duplicate_names(self):
        for bad in ['../outside','/absolute','folder\\outside']:
            with self.subTest(bad=bad),tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);archive=root/'bad.zip'
                with zipfile.ZipFile(archive,'w') as package:package.writestr(bad,'bad')
                with self.assertRaises(DocumentError):extract_package(archive,root/'packages')
                self.assertFalse((root/'outside').exists())
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);archive=root/'bad.zip';entry=zipfile.ZipInfo('link');entry.external_attr=(stat.S_IFLNK|0o777)<<16
            with zipfile.ZipFile(archive,'w') as package:package.writestr(entry,'/etc/passwd')
            with self.assertRaises(DocumentError):extract_package(archive,root/'packages')
    def test_local_zip_is_stable_and_oversized_archive_does_not_publish(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);archive=root/'fixture.zip'
            with zipfile.ZipFile(archive,'w') as package:package.writestr('manifest.json',json.dumps({'manifest_version':3,'name':'ZIP fixture','version':'1.0'}))
            first=extract_package(archive,root/'packages');self.assertEqual(extract_package(archive,root/'packages'),first)
            self.assertTrue(archive.is_file())
            with patch('little_launcher.extension_packages.MAX_PACKAGE',1):
                with self.assertRaises(DocumentError):extract_package(archive,root/'rejected')
            self.assertEqual(list((root/'rejected').iterdir()),[])
