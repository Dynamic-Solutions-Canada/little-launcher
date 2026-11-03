"""Synthetic browser bookmarks only; never read an actual browser profile in tests."""
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from little_launcher.browser_bookmarks import BookmarkSource,read_bookmarks,sources
from little_launcher.document import DocumentError

class BrowserBookmarkTests(unittest.TestCase):
    def test_chromium_profiles_and_folder_search_metadata_are_read_without_mutation(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);path=home/'.config/microsoft-edge/Default/Bookmarks';path.parent.mkdir(parents=True)
            data={'roots':{'bookmark_bar':{'type':'folder','name':'Bar','children':[
                {'type':'url','name':'Saved','url':'https://example.com'},
                {'type':'url','name':'Not a website','url':'javascript:alert(1)'}]}}}
            path.write_text(json.dumps(data));before=path.read_bytes()
            found=sources(home)
            self.assertEqual([(item.browser,item.profile) for item in found],[('Edge','Default')])
            self.assertEqual(read_bookmarks(path),[{'Name':'Saved','Url':'https://example.com','Folder':'Bar'}])
            self.assertEqual(path.read_bytes(),before)

    def test_firefox_and_html_sources_import_only_ordinary_bookmarked_addresses(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);path=root/'places.sqlite'
            with closing(sqlite3.connect(path)) as db:
                db.executescript('CREATE TABLE moz_places(id INTEGER,url TEXT);CREATE TABLE moz_bookmarks(title TEXT,fk INTEGER,type INTEGER,parent INTEGER,position INTEGER);')
                db.execute('INSERT INTO moz_places VALUES(1,?)',('https://example.com/firefox',))
                db.execute('INSERT INTO moz_bookmarks VALUES(?,1,1,1,0)',('Firefox bookmark',));db.commit()
            before=path.read_bytes();self.assertEqual(read_bookmarks(path)[0]['Name'],'Firefox bookmark');self.assertEqual(path.read_bytes(),before)
            html=root/'export.html';html.write_text('<DL><DT><A HREF="https://example.com/html?a=1&amp;b=2">A &amp; B</A></DL>')
            self.assertEqual(read_bookmarks(html)[0],{'Name':'A & B','Url':'https://example.com/html?a=1&b=2','Folder':''})
            broken=root/'invalid.json';broken.write_text('bad source')
            with self.assertRaises(DocumentError):read_bookmarks(broken)

    def test_html_folder_names_and_firefox_folder_ancestry_are_read_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);html=root/'bookmarks.html'
            html.write_text('<DL><DT><H3>Bar</H3><DL><DT><H3>Tools</H3><DL><DT><A HREF="https://example.com">Tool</A></DL></DL></DL>')
            self.assertEqual(read_bookmarks(html)[0]['Folder'],'Bar/Tools')
            path=root/'places.sqlite'
            with closing(sqlite3.connect(path)) as db:
                db.executescript('CREATE TABLE moz_places(id INTEGER,url TEXT);CREATE TABLE moz_bookmarks(id INTEGER,title TEXT,fk INTEGER,type INTEGER,parent INTEGER,position INTEGER);INSERT INTO moz_places VALUES(1,"https://example.com");INSERT INTO moz_bookmarks VALUES(1,"Bar",NULL,2,0,0),(2,"Tools",NULL,2,1,0),(3,"Tool",1,1,2,0);');db.commit()
            before=path.read_bytes();self.assertEqual(read_bookmarks(path)[0]['Folder'],'Bar/Tools');self.assertEqual(path.read_bytes(),before)

    def test_gecko_variants_are_discovered_without_loading_unrelated_profile_data(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary)
            for folder in ['.zen','.waterfox','.librewolf']:
                path=home/folder/'test-profile'/'places.sqlite';path.parent.mkdir(parents=True);path.write_bytes(b'not-opened-by-discovery')
            self.assertEqual({source.browser for source in sources(home)},{'Zen','Waterfox','LibreWolf'})
