> The item-level shortcut decision below is superseded by [whole-launcher ownership](0004-whole-launcher-platform-ownership.md). Local browser-session isolation remains current.

# Keep OS-specific shortcuts together and website sessions local

Windows application paths can be unusable on Linux even when both machines share the same launcher collection. Keep separate OS-specific entries in that collection and hide incompatible entries from each OS's launcher view. This supersedes the proposed machine-local replacement mapping: users can place a Linux shortcut beside the Windows shortcut without rewriting the latter or managing replacement identities. Narrow Windows compatibility changes are approved for symmetric visibility. Application shortcuts receive an automatic, user-editable OS assignment; ordinary website URLs are cross-platform. Missing local files do not by themselves establish OS incompatibility. An editor toggle reveals entries belonging to other operating systems.

Web launchers keep persistent sign-in sessions locally, isolated per launcher by default, with an explicit shared local profile option. Configuration compatibility does not imply copying website cookies or credentials between machines. Desktop integration preferences are also machine-local; portable launcher configuration continues to sync.
