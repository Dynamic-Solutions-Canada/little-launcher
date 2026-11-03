"""Immediate full-source editing commands shared by regular and in-place hosts."""
from copy import deepcopy

from PySide6.QtCore import QObject, Signal
from PySide6.QtWidgets import QDialog, QMessageBox

from .forms import NameDialog, ShortcutDialog
from .document import launcher_supported


class ItemEditingSession(QObject):
    """Keep persistence policy outside both window hosts and never save a display projection."""
    changed = Signal()

    def __init__(self, controller, launcher, parent_widget):
        super().__init__(parent_widget)
        self.controller = controller
        self.original = deepcopy(launcher)
        self.parent_widget = parent_widget
        self.saving = False

    def add_external(self, items, destination):
        """Insert references only, without altering existing hidden rows or source files."""
        if not isinstance(items, list) or not items or len(items) > 64:
            return
        candidate = deepcopy(self.original)
        try:
            if 'target' in destination:
                collection, index, _ = self.resolve(candidate, destination['target'])
                index += int(destination.get('after', False))
            elif 'group' in destination:
                _, _, group = self.resolve(candidate, destination['group'])
                if not group.get('IsGroup'):
                    return
                collection = group.setdefault('Children', [])
                index = len(collection)
            else:
                column = destination.get('column')
                breaks = [index for index, row in enumerate(candidate['Items']) if row.get('IsColumnBreak')]
                if type(column) is not int or not 0 <= column <= len(breaks):
                    return
                collection = candidate['Items']
                index = breaks[column] if column < len(breaks) else len(collection)
            collection[index:index] = deepcopy(items)
        except (IndexError, KeyError, TypeError):
            return
        self.commit(candidate)

    @staticmethod
    def resolve(launcher, path):
        collection = launcher['Items']
        for index in path[:-1]:
            collection = collection[index]['Children']
        index = path[-1]
        return collection, index, collection[index]

    def commit(self, candidate, expected=None):
        """Publish only durable saves; native dialogs retain their own earlier source preimage."""
        self.saving = True
        try:
            saved = self.controller.replace_launcher(candidate, self.original if expected is None else expected)
        finally:
            self.saving = False
        if saved:
            self.original = deepcopy(candidate)
            self.changed.emit()
        return saved

    def add_item(self, kind=0):
        expected = deepcopy(self.original)
        if kind == 4:
            expected['Items'].append({'IsColumnBreak': True})
            self.commit(expected)
            return
        if kind == 3:
            dialog = NameDialog('Add Group', parent=self.parent_widget, placeholder='Group name', accept_text='Add')
        else:
            dialog = ShortcutDialog(parent=self.parent_widget,icon_directory=self.controller.directory/'icons')
            dialog.kind.setCurrentIndex(kind)
        if dialog.exec() == QDialog.Accepted:
            candidate = deepcopy(expected)
            value = {'Name': dialog.value(), 'IsGroup': True, 'Children': []} if kind == 3 else dialog.value()
            candidate['Items'].append(value)
            self.commit(candidate, expected)

    def remove_column(self, index):
        """Remove only the separator so joining columns retains every hidden source item."""
        candidate = deepcopy(self.original)
        breaks = [position for position, item in enumerate(candidate['Items']) if item.get('IsColumnBreak')]
        if breaks:
            candidate['Items'].pop(breaks[max(0, min(index - 1, len(breaks) - 1))])
            self.commit(candidate)

    def edit_item(self, path):
        expected = deepcopy(self.original)
        _, _, original = self.resolve(expected, path)
        if original.get('IsGroup'):
            dialog = NameDialog('Rename Group', original.get('Name', ''), self.parent_widget, 'Group name')
        else:
            dialog = ShortcutDialog(original, self.parent_widget,icon_directory=self.controller.directory/'icons')
        if dialog.exec() != QDialog.Accepted:
            return
        if original.get('IsGroup'):
            value = deepcopy(original)
            value['Name'] = dialog.value()
        else:
            value = dialog.value()
        if len(path) > 1 and value.get('IsGroup') and not original.get('IsGroup'):
            QMessageBox.warning(self.parent_widget, 'Group placement', 'Move this item out of its group before changing it into a group.')
            return
        if original.get('Children') and not value.get('IsGroup'):
            QMessageBox.warning(self.parent_widget, 'Keep group contents', 'Move or remove the group contents before changing its type.')
            return
        candidate = deepcopy(expected)
        collection, index, _ = self.resolve(candidate, path)
        collection[index] = value
        self.commit(candidate, expected)

    def remove_item(self, path):
        expected = deepcopy(self.original)
        if QMessageBox.question(self.parent_widget, 'Remove item', 'Remove this item and its children?') != QMessageBox.Yes:
            return
        candidate = deepcopy(expected)
        collection, index, _ = self.resolve(candidate, path)
        collection.pop(index)
        self.commit(candidate, expected)

    def step_item(self, path, delta):
        candidate = deepcopy(self.original)
        collection, index, _ = self.resolve(candidate, path)
        destination = index + delta
        if 0 <= destination < len(collection):
            collection.insert(destination, collection.pop(index))
            self.commit(candidate)

    def move_item(self, source, target, after=False, into=None):
        """Move exact identities; explicit group-edge drops reorder instead of entering it."""
        if source == target or list(target[:len(source)]) == list(source):
            return
        candidate = deepcopy(self.original)
        try:
            source_list, source_index, item = self.resolve(candidate, source)
            target_list, _, receiver = self.resolve(candidate, target)
        except (IndexError, KeyError, TypeError):
            return
        enter_group = receiver.get('IsGroup') and not item.get('IsGroup') and into is not False
        if item.get('IsGroup') and (len(target) > 1 or into):
            return
        source_list.pop(source_index)
        if enter_group:
            receiver.setdefault('Children', []).append(item)
        else:
            target_index = next(index for index, value in enumerate(target_list) if value is receiver)
            target_list.insert(target_index + int(after), item)
        self.commit(candidate)

    def move_to(self, source, destination):
        """Append to a saved group or column without inventing a presentation-only sentinel."""
        candidate = deepcopy(self.original)
        try:
            source_list, source_index, item = self.resolve(candidate, source)
            if 'group' in destination:
                path = destination['group']
                if item.get('IsGroup') or list(path[:len(source)]) == list(source):
                    return
                _, _, group = self.resolve(candidate, path)
                if not group.get('IsGroup'):
                    return
                target_list = group.setdefault('Children', [])
                insertion = len(target_list)
            else:
                column = destination.get('column')
                breaks = [index for index, row in enumerate(candidate['Items']) if row.get('IsColumnBreak')]
                if type(column) is not int or not 0 <= column <= len(breaks):
                    return
                target_list = candidate['Items']
                insertion = breaks[column] if column < len(breaks) else len(target_list)
            source_list.pop(source_index)
            if source_list is target_list and source_index < insertion:
                insertion -= 1
            target_list.insert(insertion, item)
        except (IndexError, KeyError, TypeError):
            return
        self.commit(candidate)

    @staticmethod
    def writable(launcher):
        return launcher.get('Kind', 0) == 0 and launcher_supported(launcher) and not (launcher.get('IsShared') and
            not launcher.get('IsSharedOwner') and not launcher.get('SharedTwoWay'))

    def resize_icons(self, count):
        if not self.writable(self.original) or self.original.get('ViewMode', 0) == 1:
            return
        if type(count) is not int or not 1 <= count <= 12:
            return
        candidate = deepcopy(self.original)
        candidate['IconModeIconsPerRow'] = count
        self.commit(candidate)

    def destinations(self, path):
        """Offer real saved groups and writable shortcut launchers, never web/read-only hosts."""
        _, _, item = self.resolve(self.original, path)
        choices = [('Top Level', {'column': len([row for row in self.original['Items'] if row.get('IsColumnBreak')])})] if len(path) > 1 else []
        if not item.get('IsGroup'):
            for index, group in enumerate(self.original['Items']):
                if group.get('IsGroup') and list(path[:-1]) != [index]:
                    choices.append((group.get('Name', 'Group'), {'group': [index]}))
        for launcher in self.controller.launchers:
            if launcher['Id'] != self.original['Id'] and self.writable(launcher):
                choices.append((launcher.get('Name', 'Launcher') + ' (launcher)', launcher['Id']))
        return choices

    def transfer(self, source, identifier):
        """Move between launchers in one guarded full-document write, preserving all metadata."""
        expected = deepcopy(self.original)
        if identifier == expected['Id']:
            return
        def mutation(document):
            origin = next((row for row in document['Launchers'] if row['Id'] == expected['Id']), None)
            target = next((row for row in document['Launchers'] if row['Id'] == identifier), None)
            from .document import DocumentError
            if origin != expected or not target or not self.writable(target) or not self.writable(origin):
                raise DocumentError('A launcher changed while the move menu was open. Reopen it before moving.')
            collection, index, item = self.resolve(origin, source)
            collection.pop(index)
            target.setdefault('Items', []).append(item)
        self.saving = True
        try:
            saved = self.controller.edit(mutation)
        finally:
            self.saving = False
        if saved:
            self.original = deepcopy(self.controller.launcher(expected['Id']))
            self.changed.emit()

    def wire(self, view):
        """Attach the same commands to either host's fresh renderer instance."""
        expected = deepcopy(view.launcher)
        def guarded(action, *args):
            if self.original != expected:
                self.controller.failed.emit('This item view changed while its menu was open. Reopen the menu before editing.')
                return
            action(*args)
        view.move_destinations = self.destinations
        for signal, action in ((view.transfer_requested, self.transfer), (view.edit_requested, self.edit_item),
                (view.remove_requested, self.remove_item), (view.step_requested, self.step_item),
                (view.move_requested, self.move_item), (view.column_removed, self.remove_column),
                (view.append_requested, self.move_to), (view.external_drop_requested, self.add_external),
                (view.icons_per_row_requested, self.resize_icons)):
            signal.connect(lambda *args, command=action: guarded(command, *args))
