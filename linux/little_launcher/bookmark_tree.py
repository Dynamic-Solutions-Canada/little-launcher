"""Qt-independent, lossless bookmark mutations against saved source paths."""
from copy import deepcopy

from . import i18n
from .document import DocumentError


class BookmarkTree:
    """Edit a detached saved tree, retaining unknown fields and stable source paths."""

    def __init__(self, items):
        self.items = deepcopy(items)

    def collection(self, parent):
        """Resolve a folder path; malformed or stale paths never select a nearby row."""
        items = self.items
        if not isinstance(parent, list):
            raise DocumentError(i18n.t('webBookmarks.error.changed'))
        for index in parent:
            if type(index) is not int or not 0 <= index < len(items):
                raise DocumentError(i18n.t('webBookmarks.error.changed'))
            folder = items[index]
            if not folder.get('IsFolder'):
                raise DocumentError(i18n.t('webBookmarks.error.notFolder'))
            items = folder.setdefault('Children', [])
        return items

    def resolve(self, path):
        """Return the exact source collection and row, including undisplayed entries."""
        if not isinstance(path, list) or not path:
            raise DocumentError(i18n.t('webBookmarks.error.chooseBookmark'))
        items = self.collection(path[:-1])
        index = path[-1]
        if type(index) is not int or not 0 <= index < len(items):
            raise DocumentError(i18n.t('webBookmarks.error.changed'))
        return items, index, items[index]

    def replace(self, path, fields):
        """Change only form-owned fields, preserving future metadata and folder children."""
        _, _, item = self.resolve(path)
        item.update(deepcopy(fields))

    def step(self, path, delta):
        """Move among saved siblings, including entries omitted by the browser projection."""
        items, index, _ = self.resolve(path)
        if type(delta) is not int or delta not in (-1, 1):
            raise DocumentError(i18n.t('webBookmarks.error.chooseStep'))
        destination = index + delta
        if not 0 <= destination < len(items):
            raise DocumentError(i18n.t('webBookmarks.error.atEnd'))
        items.insert(destination, items.pop(index))

    def reorder(self, path, parent, before):
        """Resolve both collections before removal so hidden rows and folder identities survive."""
        items, index, item = self.resolve(path)
        if not isinstance(parent, list):
            raise DocumentError(i18n.t('webBookmarks.error.chooseDestination'))
        if item.get('IsFolder') and parent[:len(path)] == path:
            raise DocumentError(i18n.t('webBookmarks.error.folderIntoItself'))
        destination = self.collection(parent)
        if before is None:
            before = len(destination)
        if type(before) is not int or not 0 <= before <= len(destination):
            raise DocumentError(i18n.t('webBookmarks.error.destinationChanged'))
        if items is destination and before > index:
            before -= 1
        items.pop(index)
        destination.insert(before, item)

    def move(self, path, parent):
        """Move into a folder or onto the bar, rejecting self and descendant targets."""
        items, index, item = self.resolve(path)
        if not isinstance(parent, list):
            raise DocumentError(i18n.t('webBookmarks.error.chooseDestination'))
        if item.get('IsFolder') and parent[:len(path)] == path:
            raise DocumentError(i18n.t('webBookmarks.error.folderIntoItself'))
        destination = self.collection(parent)
        destination.append(items.pop(index))
