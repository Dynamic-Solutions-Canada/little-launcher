"""AppImage release policy and atomic update transactions use synthetic ELF fixtures only."""
from dataclasses import replace
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from little_launcher.updates import Release,UpdateError,check_release,download_release,image_identity,release_from_metadata,valid_image_header,version_tuple


def image(content=b'fixture'):
    header=bytearray(32);header[:4]=b'\x7fELF';header[4]=2;header[5]=1;header[8:11]=b'AI\x02';header[18:20]=(62).to_bytes(2,'little')
    return bytes(header)+content

def release(raw,version='2.0.0'):
    name=f'LittleLauncher-{version}-linux-x64.AppImage'
    return Release(version,name,f'https://github.com/Dynamic-Solutions-Canada/little-launcher/releases/download/v{version}/{name}',hashlib.sha256(raw).hexdigest(),len(raw))

def metadata(raw):
    item=release(raw)
    return {'tag_name':'v2.0.0','assets':[{'name':item.name,'browser_download_url':item.url,'digest':'sha256:'+item.digest,'size':item.size}]}


class UpdateTests(unittest.TestCase):
    def test_selects_only_verified_newer_stable_matching_linux_appimage(self):
        raw=image();data=metadata(raw)
        self.assertEqual(release_from_metadata(data,'1.9.9'),release(raw))
        self.assertIsNone(release_from_metadata(data,'2.0.0'));self.assertIsNone(release_from_metadata({**data,'prerelease':True},'1.0.0'))
        self.assertIsNone(release_from_metadata({'tag_name':'v2.0.0','assets':[{'name':'LittleLauncher-x64-portable.zip'}]},'1.0.0'))
        self.assertIsNone(release_from_metadata(data,'1.0.0','arm64'))
        for field,value in [('digest',None),('browser_download_url','https://other.example/image'),('size',True),('size',2**40)]:
            bad=metadata(raw);bad['assets'][0][field]=value
            with self.assertRaises(UpdateError):release_from_metadata(bad,'1.0.0')

    def test_transferred_repository_uses_canonical_metadata_and_exact_asset_path(self):
        """A repository transfer must not reject real assets or widen the URL allowlist."""
        raw=image();data=metadata(raw);requests=[]
        def opener(url):
            requests.append(url)
            return io.BytesIO(json.dumps(data).encode())
        selected=check_release('1.9.9',opener=opener)
        self.assertEqual(requests,['https://api.github.com/repos/Dynamic-Solutions-Canada/little-launcher/releases/latest'])
        self.assertEqual(selected,release(raw))
        for repository in ['RyanEwen/LittleLauncher','unrelated-owner/little-launcher']:
            changed=metadata(raw)
            changed['assets'][0]['browser_download_url']=selected.url.replace('Dynamic-Solutions-Canada/little-launcher',repository)
            with self.assertRaises(UpdateError):release_from_metadata(changed,'1.9.9')

    def test_metadata_bounds_version_bounds_and_download_header_architecture(self):
        for version in ['1.0','1.0.0-beta','1.0.0/../../a','9'*100+'.0.0']:
            with self.assertRaises(UpdateError):version_tuple(version)
        self.assertTrue(valid_image_header(image()));self.assertFalse(valid_image_header(image(),'arm64'))
        with self.assertRaises(UpdateError):check_release('1.0.0',opener=lambda url:io.BytesIO(b'x'*(2*1024*1024+1)))

    def test_verified_download_does_not_replace_until_explicit_install_and_keeps_backup(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'Launcher.AppImage';old=image(b'old');new=image(b'new');path.write_bytes(old);path.chmod(0o755)
            staged=download_release(release(new),path,opener=lambda url:io.BytesIO(new))
            self.assertEqual(path.read_bytes(),old);self.assertTrue(staged.staged.exists())
            backup=staged.install();self.assertEqual(path.read_bytes(),new);self.assertEqual(backup.read_bytes(),old)
            self.assertFalse(staged.staged.exists());self.assertTrue(os.access(path,os.X_OK))

    def test_failed_digest_truncated_oversized_wrong_arch_and_cancellation_preserve_existing_image(self):
        for raw,cancelled in [(image(b'wrong'),False),(image(b'new')[:-1],False),(image(b'new')+b'bad',False),(b'not an AppImage',False),(image(b'new'),True)]:
            with tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);path=root/'Launcher.AppImage';old=image(b'old');path.write_bytes(old);path.chmod(0o755)
                with self.assertRaises(UpdateError):download_release(release(image(b'new')),path,opener=lambda url:io.BytesIO(raw),cancelled=lambda:cancelled)
                self.assertEqual(path.read_bytes(),old);self.assertEqual(list(root.iterdir()),[path])

    def test_staged_tamper_and_changed_original_block_install(self):
        for change_original in (False,True):
            with tempfile.TemporaryDirectory() as temporary:
                path=Path(temporary)/'Launcher.AppImage';old=image(b'old');new=image(b'new');path.write_bytes(old);path.chmod(0o755)
                staged=download_release(release(new),path,opener=lambda url:io.BytesIO(new))
                changed=image(b'changed')
                if change_original:path.write_bytes(changed)
                else:staged.staged.write_bytes(image(b'changed'))
                with self.assertRaises(UpdateError):staged.install()
                self.assertEqual(path.read_bytes(),changed if change_original else old);staged.discard()

    def test_symlink_current_and_unrelated_backup_are_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'Launcher.AppImage';target=root/'target';target.write_bytes(image());target.chmod(0o755);path.symlink_to(target)
            with self.assertRaises(UpdateError):image_identity(path)
            path.unlink();path.write_bytes(image());path.chmod(0o755)
            staged=download_release(release(image(b'new')),path,opener=lambda url:io.BytesIO(image(b'new')))
            backup=root/'.Launcher.AppImage.previous';backup.write_text('unrelated document')
            with self.assertRaises(UpdateError):staged.install()
            self.assertEqual(backup.read_text(),'unrelated document');staged.discard()

    def test_directory_sync_failure_after_replace_restores_previous_image(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'Launcher.AppImage';old=image(b'old');new=image(b'new');path.write_bytes(old);path.chmod(0o755)
            staged=download_release(release(new),path,opener=lambda url:io.BytesIO(new))
            with patch('little_launcher.updates.sync_directory',side_effect=[None,OSError('fixture disk error'),None]):
                with self.assertRaises(UpdateError):staged.install()
            self.assertEqual(path.read_bytes(),old)
