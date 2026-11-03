"""Public favicon preview uses only synthetic localhost responses and temporary image files."""
import http.server
from pathlib import Path
import tempfile
import threading
import time
import unittest
try:
    from PySide6.QtCore import QBuffer, QIODevice
    from PySide6.QtGui import QColor, QImage
    from PySide6.QtWidgets import QApplication
    from little_launcher.document import DocumentError
    from little_launcher.icon_picker import IconPicker
    from little_launcher.site_icon import SiteIconPreview, favicon_url, normalized_png, MAX_BYTES
    QT=True
except ImportError:
    QT=False

@unittest.skipUnless(QT,'Native website icon checks require QtWidgets/QtNetwork')
class SiteIconTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app=QApplication.instance() or QApplication([])
        image=QImage(32,32,QImage.Format_ARGB32);image.fill(QColor('#119944'))
        buffer=QBuffer();buffer.open(QIODevice.WriteOnly);image.save(buffer,'PNG');cls.png=bytes(buffer.data())
        cls.requests=[];cls.mode='image'
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self,*_):pass
            def do_GET(self):
                cls.requests.append((self.path,self.headers.get('Cookie'),self.headers.get('Authorization'),self.headers.get('Referer')))
                mode=cls.mode
                if mode=='slow':time.sleep(.2)
                if mode=='redirect':
                    self.send_response(302);self.send_header('Location','/redirected');self.end_headers();return
                self.send_response(200);self.send_header('Content-Type','image/png');self.send_header('Set-Cookie','private=not-reused; Path=/');self.end_headers()
                try:self.wfile.write(b'x'*(MAX_BYTES+1) if mode=='oversized' else cls.png)
                except (BrokenPipeError,ConnectionResetError):pass
        cls.server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
        threading.Thread(target=cls.server.serve_forever,daemon=True).start()
        cls.url=f'http://127.0.0.1:{cls.server.server_port}/private/page?secret=excluded'

    @classmethod
    def tearDownClass(cls):cls.server.shutdown();cls.server.server_close()

    def setUp(self):type(self).mode='image';self.requests.clear()

    def until(self,check,seconds=2):
        end=time.monotonic()+seconds
        while not check() and time.monotonic()<end:self.app.processEvents();time.sleep(.002)
        self.assertTrue(check())

    def test_origin_only_url_rejects_credentials_and_nonweb_targets(self):
        self.assertTrue(favicon_url(self.url).toString().endswith('/favicon.ico'))
        for value in ['file:///tmp/a','javascript:bad','https://name:password@example.com','https://bad host/']:
            with self.assertRaises(DocumentError):favicon_url(value)
        self.assertNotIn('secret',favicon_url(self.url).toString())

    def test_raster_normalization_bounds_and_rejects_svg_or_malformed_data(self):
        self.assertIsNotNone(normalized_png(self.png))
        self.assertIsNone(normalized_png(b'<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"/>'))
        self.assertIsNone(normalized_png(b'x'*(MAX_BYTES+1)))
        image=QImage(2048,2,QImage.Format_ARGB32);image.fill(0);buffer=QBuffer();buffer.open(QIODevice.WriteOnly);image.save(buffer,'PNG')
        self.assertIsNone(normalized_png(bytes(buffer.data())))

    def test_network_preview_omits_cookies_credentials_query_and_referrer(self):
        fetch=SiteIconPreview();images=[];fetch.ready.connect(images.append)
        fetch.fetch(self.url);self.until(lambda:len(images)==1)
        fetch.fetch(self.url);self.until(lambda:len(images)==2)
        self.assertEqual(self.requests,[('/favicon.ico',None,None,None)]*2)
        fetch.cancel()

    def test_redirect_oversized_response_and_timeout_fail_without_preview(self):
        for mode in ['redirect','oversized','slow']:
            type(self).mode=mode;self.requests.clear();fetch=SiteIconPreview();images=[];errors=[];fetch.ready.connect(images.append);fetch.failed.connect(errors.append)
            fetch.fetch(self.url)
            if mode=='slow':fetch.timer.start(20)
            self.until(lambda:bool(errors));self.assertFalse(images)
            self.assertTrue(all(request[0]=='/favicon.ico' for request in self.requests));fetch.cancel()

    def test_loaded_page_choice_is_pending_web_only_has_no_composite(self):
        from little_launcher.launcher_settings import LauncherDialog
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            launcher={'Id':'web','Kind':1,'WebHomeUrl':self.url,'TrayIconMode':'Composite'}
            picker=IconPicker(launcher,cache_directory=root,page_icon=self.png)
            self.assertFalse(any(mode=='Composite' for _,mode in picker.tiles))
            self.assertTrue(picker.page_button.isEnabled())
            self.assertEqual(picker.tabs.currentIndex(),picker.site_tab)
            picker.page_button.click()
            self.assertEqual(picker.mode,'Site')
            self.assertEqual(list(root.iterdir()),[])
            picker.reject();self.assertEqual(launcher['TrayIconMode'],'Composite')
            form=LauncherDialog(launcher,page_icon=self.png)
            form.show();self.app.processEvents()
            self.assertTrue(form.icon_mode.isHidden())
            self.assertTrue(form.custom_row.isHidden())
            form.close()

    def test_browser_icon_cache_checks_home_identity_and_bounds(self):
        import base64
        from little_launcher.browser import BrowserHost
        with tempfile.TemporaryDirectory() as temporary:
            host=BrowserHost(Path(temporary));host.launchers={'web':{'Id':'web','Kind':1,'WebHomeUrl':self.url}}
            event={'type':'page-icon','id':'web','url':self.url,'png':'data:image/png;base64,'+base64.b64encode(self.png).decode()}
            host._message(event);self.assertIsNotNone(host.page_icon('web'))
            webp_image=QImage(24,24,QImage.Format_ARGB32);webp_image.fill(QColor('#00aa55'))
            webp_buffer=QBuffer();webp_buffer.open(QIODevice.WriteOnly)
            self.assertTrue(webp_image.save(webp_buffer,'WEBP'))
            host.page_icons.clear()
            host._message({'type':'page-icon','id':'web','url':self.url,'imageBase64':base64.b64encode(bytes(webp_buffer.data())).decode()})
            self.assertTrue(host.page_icon('web').startswith(b'\x89PNG'))
            host.page_icons.clear();host._message({**event,'url':'https://foreign.example/'})
            self.assertIsNone(host.page_icon('web'))
            host._message({**event,'png':'data:image/png;base64,invalid!'})
            self.assertIsNone(host.page_icon('web'))

    def test_bookmark_decode_reply_is_scoped_bounded_and_normalized(self):
        import base64
        from little_launcher.browser import BrowserHost
        with tempfile.TemporaryDirectory() as temporary:
            host=BrowserHost(Path(temporary));host.launchers={'web':{'Id':'web','Kind':1}}
            sent=[];host.send=sent.append
            image=QImage(24,24,QImage.Format_ARGB32);image.fill(QColor('#00aa55'))
            buffer=QBuffer();buffer.open(QIODevice.WriteOnly);self.assertTrue(image.save(buffer,'WEBP'))
            event={'type':'bookmark-icon','id':'web','token':'request-token','imageBase64':base64.b64encode(bytes(buffer.data())).decode()}
            host._message(event);self.assertEqual(len(sent),1)
            self.assertEqual(sent[0]['token'],'request-token')
            self.assertTrue(base64.b64decode(sent[0]['png']).startswith(b'\x89PNG'))
            for invalid in [{**event,'id':'removed'},{**event,'token':'x'*65},{**event,'imageBase64':'!'},{**event,'imageBase64':'x'*350001}]:host._message(invalid)
            self.assertEqual(len(sent),1)

    def test_fetch_cancel_and_explicit_confirm_do_not_replace_original_file_or_settings(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);old=root/'original.png';old.write_bytes(self.png)
            original={'Id':'web','Kind':1,'WebHomeUrl':self.url,'TrayIconMode':'Custom','CustomTrayIconPath':str(old)}
            picker=IconPicker(original,cache_directory=root/'icons');picker.fetch_site();self.until(lambda:picker.site_png is not None)
            self.assertFalse((root/'icons').exists());picker.reject();self.assertEqual(old.read_bytes(),self.png)
            picker=IconPicker(original,cache_directory=root/'icons');picker.fetch_site();self.until(lambda:picker.site_png is not None)
            picker.accept();selected=picker.value()
            self.assertEqual(selected['TrayIconMode'],'Custom');self.assertTrue(Path(selected['CustomTrayIconPath']).is_file());self.assertEqual(old.read_bytes(),self.png)
            self.assertEqual(original['CustomTrayIconPath'],str(old))
            type(self).mode='slow';late=IconPicker(original,cache_directory=root/'other');late.fetch_site();late.choose('Glyph:A')
            self.until(lambda:late.site_fetch.reply is None);end=time.monotonic()+.3
            while time.monotonic()<end:self.app.processEvents();time.sleep(.002)
            self.assertEqual(late.mode,'Glyph:A');self.assertIsNone(late.site_png);self.assertFalse((root/'other').exists());late.reject()
