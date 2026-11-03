# Own shortcut launchers by operating system

Ryan replaced item-level OS handling with completely separate Windows and Linux shortcut launchers. Each shortcut launcher has a portable Platform string. Legacy untagged launchers are Windows; new launchers belong to their creating OS. Web launchers remain shared. A foreign shortcut launcher is visible as disabled in Settings and owns no tray or running resources on that system. Users create a separate launcher for the other OS.

All item-level inference, filtering and assignment controls are removed. Existing source items, groups, columns and legacy metadata round-trip without modification. This supersedes ADR0002's item visibility decision while retaining its local browser-session boundaries.

Sync writer marker2 and local verified compatibility version2 prevent older clients from silently losing the new ownership and icon fields. Selected image bytes and mode-bound glyph previews travel with launchers; OS paths and browser credentials do not define the selected icon.
