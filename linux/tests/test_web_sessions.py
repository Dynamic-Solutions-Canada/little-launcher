"""Device-local tab persistence must not affect launcher settings or destroy unreadable data."""
import json
from pathlib import Path
import tempfile
import unittest
from little_launcher.document import DocumentError
from little_launcher.web_sessions import WebSessions, valid_session

class WebSessionTests(unittest.TestCase):
    def test_device_local_sessions_survive_reload_and_do_not_mutate_input(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory=Path(temporary)
            settings=directory/'settings.json';settings.write_text('{"Launchers":[]}')
            store=WebSessions(directory)
            value={'tabs':['https://example.com/one','https://example.com/two'],'active':1}
            store.save('web',value)
            value['tabs'].clear()
            restored=WebSessions(directory)
            self.assertEqual(restored.get('web'),{'tabs':['https://example.com/one','https://example.com/two'],'active':1})
            self.assertEqual(settings.read_text(),'{"Launchers":[]}')
            before=store.path.stat().st_mtime_ns
            restored.save('web',restored.get('web'))
            self.assertEqual(store.path.stat().st_mtime_ns,before)
            restored.save('web',{'tabs':[],'active':0})
            self.assertIsNone(WebSessions(directory).get('web'))

    def test_unreadable_session_file_and_invalid_updates_are_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'web-sessions.json';path.write_text('not json')
            store=WebSessions(Path(temporary))
            self.assertTrue(store.error)
            with self.assertRaises(DocumentError):store.save('web',{'tabs':['https://example.com'],'active':0})
            self.assertEqual(path.read_text(),'not json')
        for value in [{'tabs':['file:///private'],'active':0},{'tabs':['https://example.com'],'active':True},
                      {'tabs':['https://example.com'],'active':1},{'tabs':['https://example.com']*65,'active':0},
                      {'tabs':['http://[invalid'],'active':0}]:
            self.assertFalse(valid_session(value),value)
