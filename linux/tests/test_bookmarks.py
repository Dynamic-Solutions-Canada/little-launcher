"""Bookmark mutations preserve full-source identity, nesting and unknown metadata."""
from copy import deepcopy
import unittest

from little_launcher.bookmark_tree import BookmarkTree
from little_launcher.document import DocumentError


class BookmarkTreeTests(unittest.TestCase):
    def setUp(self):
        self.original = [
            {'Name': 'Unrendered', 'Url': 'custom:legacy', 'Future': 1},
            {'Name': 'Work', 'IsFolder': True, 'FutureFolder': 2, 'Children': [
                {'Name': 'Wiki', 'Url': 'https://wiki.example', 'FutureChild': {'keep': True}},
                {'Name': 'Nested', 'IsFolder': True, 'Children': []},
            ]},
            {'Name': 'Home', 'Url': 'https://example.com'},
        ]
        self.tree = BookmarkTree(self.original)

    def test_edit_preserves_unknown_fields_and_the_original_tree(self):
        self.tree.replace([1, 0], {'Name': 'Renamed', 'Url': 'https://new.example'})
        self.assertEqual(self.tree.items[1]['Children'][0]['FutureChild'], {'keep': True})
        self.assertEqual(self.tree.items[1]['FutureFolder'], 2)
        self.assertEqual(self.original[1]['Children'][0]['Name'], 'Wiki')
        self.assertEqual(self.tree.items[0], self.original[0])

    def test_reorder_and_cross_folder_move_use_saved_not_projected_indexes(self):
        self.tree.step([2], -1)
        self.assertEqual([row['Name'] for row in self.tree.items], ['Unrendered', 'Home', 'Work'])
        self.tree.move([1], [2, 1])
        self.assertEqual(self.tree.items[1]['Children'][1]['Children'][0]['Name'], 'Home')
        self.tree.move([1, 0], [])
        self.assertEqual(self.tree.items[-1]['FutureChild'], {'keep': True})

    def test_stale_paths_and_folder_cycles_are_rejected_without_changing_source(self):
        for path, parent in [([1], [1, 1]), ([1, 1], [1, 1]), ([9], []), ([True], []), ([1], None)]:
            with self.subTest(path=path, parent=parent), self.assertRaises(DocumentError):
                BookmarkTree(self.original).move(path, parent)
        self.assertEqual(self.tree.items, self.original)
        with self.assertRaises(DocumentError):
            self.tree.step([0], -1)


class BookmarkStripReorderTests(unittest.TestCase):
    def test_drop_resolves_destination_before_source_removal_and_keeps_hidden_members(self):
        original=[{'Name':'Move','Url':'https://move.example','Future':9},
                  {'Name':'Folder','IsFolder':True,'Children':[{'Url':'legacy:hidden','Unknown':7}]}]
        tree=BookmarkTree(original)
        tree.reorder([0],[1],None)
        self.assertEqual(tree.items[0]['Children'],[original[1]['Children'][0],original[0]])
        self.assertEqual(original[1]['Children'],[{'Url':'legacy:hidden','Unknown':7}])
        tree.reorder([0,1],[],0)
        self.assertEqual(tree.items,original)
        with self.assertRaises(DocumentError):tree.reorder([1],[1],None)
