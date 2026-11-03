"""Append explicit browser selections through one full-launcher transaction."""
from copy import deepcopy
from .document import DocumentError, new_shortcut, normalize_web_address


def append_browser_bookmarks(controller, expected, records):
    if expected.get('Kind', 0) != 0 or not isinstance(records, list) or not 0 < len(records) <= 10_000:
        raise DocumentError('Select between one and 10,000 bookmarks for a shortcut launcher.')
    candidate=deepcopy(expected)
    additions=[]
    for record in records:
        if not isinstance(record,dict) or not isinstance(record.get('Name'),str) or not isinstance(record.get('Url'),str) or len(record['Url'])>8192:
            raise DocumentError('The bookmark selection is invalid.')
        address=normalize_web_address(record.get('Url'))
        additions.append(new_shortcut(record['Name'].strip() or address,address,website=True))
    candidate.setdefault('Items',[]).extend(additions)
    return controller.replace_launcher(candidate,expected)
