"""Lossless bookmark mutations and native forms for browser-bar actions."""
from copy import deepcopy

from PySide6.QtWidgets import QDialog, QMessageBox

from .document import DocumentError
from .forms import BookmarkDialog, BookmarkFolderDialog, NameDialog, BookmarkAddressDialog
from .bookmark_tree import BookmarkTree
from .bookmark_picker import BookmarkPicker


class BookmarkActions:
    """Apply bar-menu commands through the same stale-save boundary as native item edits."""

    def __init__(self, controller, parent):
        self.controller = controller
        self.parent = parent

    def handle(self, message):
        """Keep a detached launcher preimage across native dialogs; cancellation writes nothing."""
        launcher = self.controller.launcher(message.get('id'))
        if launcher is None or launcher.get('Kind') != 1:
            return
        original = deepcopy(launcher)
        if message.get('expected') != original.get('WebBookmarks', []):
            self.controller.failed.emit('Bookmarks changed. Reopen the bookmark menu before editing.')
            return
        if original.get('IsShared') and not original.get('IsSharedOwner') and not original.get('SharedTwoWay'):
            self.controller.failed.emit('This subscribed launcher is read-only.')
            return
        if message.get('action') == 'set-home' and message.get('expectedHome') != original.get('WebHomeUrl'):
            self.controller.failed.emit('The home page changed. Reopen the bookmark menu before editing.')
            return
        tree = BookmarkTree(original.get('WebBookmarks', []))
        candidate = deepcopy(original)
        try:
            if message.get('action') == 'set-home':
                _, _, item = tree.resolve(message.get('path'))
                if item.get('IsFolder'):
                    raise DocumentError('Choose a bookmark address as the home page.')
                from .document import normalize_web_address
                candidate['WebHomeUrl'] = normalize_web_address(item.get('Url', ''))
            elif not self._apply(tree, message):
                return
        except DocumentError as error:
            self.controller.failed.emit(str(error))
            return
        candidate['WebBookmarks'] = tree.items
        self.controller.replace_launcher(candidate, original)

    def _apply(self, tree, message):
        """Run one explicit action, preserving the original home address and browser settings."""
        action = message.get('action')
        path = message.get('path', [])
        if action in ('add', 'add-folder', 'import'):
            collection = tree.collection(path)
            dialog = (BookmarkPicker(self.parent) if action == 'import' else
                      BookmarkDialog('', '', self.parent) if action == 'add' else BookmarkFolderDialog('', self.parent))
            if dialog.exec() != QDialog.Accepted:
                return False
            collection.append(dialog.value())
        elif action == 'edit':
            _, _, item = tree.resolve(path)
            dialog = (BookmarkFolderDialog(item.get('Name', ''), self.parent) if item.get('IsFolder')
                      else BookmarkDialog(item.get('Name', ''), item.get('Url', ''), self.parent, editing=True))
            if dialog.exec() != QDialog.Accepted:
                return False
            tree.replace(path, dialog.value())
        elif action in ('rename', 'address'):
            _, _, item = tree.resolve(path)
            if item.get('IsFolder'):
                raise DocumentError('Use Rename for a folder.')
            dialog = (NameDialog('Rename bookmark', item.get('Name') or item.get('Url', ''), self.parent)
                      if action == 'rename' else BookmarkAddressDialog(item.get('Url', ''), self.parent))
            if dialog.exec() != QDialog.Accepted:
                return False
            tree.replace(path, {'Name' if action == 'rename' else 'Url': dialog.value()})
        elif action == 'icons-only':
            if type(message.get('value')) is not bool:
                raise DocumentError('Icon only requires an explicit on/off value.')
            tree.replace(path, {'IconsOnly': message['value']})
        elif action == 'remove':
            collection, index, item = tree.resolve(path)
            text = 'Remove this folder and all its bookmarks?' if item.get('IsFolder') else 'Remove this bookmark?'
            if QMessageBox.question(self.parent, 'Remove bookmark', text) != QMessageBox.Yes:
                return False
            collection.pop(index)
        elif action == 'step':
            tree.step(path, message.get('delta'))
        elif action == 'reorder':
            tree.reorder(path, message.get('parent'), message.get('before'))
        elif action == 'move':
            tree.move(path, message.get('parent'))
        else:
            raise DocumentError('This bookmark action is unavailable.')
        return True
