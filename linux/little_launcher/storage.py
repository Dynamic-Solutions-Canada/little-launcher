"""Atomic, lossless local persistence with optimistic concurrency checks."""
from __future__ import annotations

from contextlib import contextmanager
from copy import deepcopy
import fcntl
import hashlib
import json
import os
from pathlib import Path
import tempfile

from .document import DocumentError, JsonObject, validate_document


class ConcurrentEditError(DocumentError):
    """Another writer changed the file after the editor loaded it."""


class DocumentStore:
    """Own a single document; callers edit copies and commit against a revision token."""

    def __init__(self, path: Path):
        self.path = path

    def read(self) -> tuple[JsonObject, str]:
        """Return a detached document and byte revision, failing closed on corrupt input."""
        raw = self.path.read_bytes() if self.path.exists() else b""
        try:
            document = json.loads(raw) if raw else {"Launchers": []}
        except (UnicodeError, json.JSONDecodeError) as error:
            raise DocumentError("Settings are unreadable. The existing file has been preserved.") from error
        return validate_document(document), self._revision(raw)

    def commit(self, document: JsonObject, expected_revision: str) -> str:
        """Atomically replace valid settings only if the editor's source bytes still match."""
        validated = validate_document(document)
        self.path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        with self._lock():
            previous = self.path.read_bytes() if self.path.exists() else b""
            if self._revision(previous) != expected_revision:
                raise ConcurrentEditError("Settings changed elsewhere. Reload before saving this edit.")
            raw = (json.dumps(validated, indent=2, ensure_ascii=False) + "\n").encode()
            atomic_write(self.path, raw)
        return self._revision(raw)

    @staticmethod
    def _revision(raw: bytes) -> str:
        return hashlib.sha256(raw).hexdigest()

    @contextmanager
    def _lock(self):
        """Serialize cooperating writers without treating the lock as stale state."""
        descriptor = os.open(str(self.path) + ".lock", os.O_CREAT | os.O_RDWR, 0o600)
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX)
            yield
        finally:
            os.close(descriptor)


def atomic_write(path: Path, raw: bytes) -> None:
    """Write private bytes and fsync both file and directory before reporting success."""
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary = tempfile.mkstemp(prefix="." + path.name, dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class LocalPreferences:
    """Keep machine choices outside the portable Windows-format launcher document."""

    def __init__(self, path: Path):
        self.path = path
        try:
            self.data = json.loads(path.read_text()) if path.exists() else {"StartAtLogin": True}
        except (ValueError, UnicodeError) as error:
            raise DocumentError('Machine preferences are unreadable and have been preserved.') from error
        if (not isinstance(self.data, dict)
                or type(self.data.get('StartAtLogin', True)) is not bool
                or not isinstance(self.data.get('OneDriveSync', {}), dict)
                or not isinstance(self.data.get('WebPositions', {}), dict)
                or type(self.data.get('CheckForUpdatesAutomatically',True)) is not bool
                or not isinstance(self.data.get('DisabledLauncherIds', []), list)
                or any(not isinstance(item, str) for item in self.data.get('DisabledLauncherIds', []))
                or not isinstance(self.data.get('HiddenLauncherNotices', []), list)
                or any(not isinstance(item, str) for item in self.data.get('HiddenLauncherNotices', []))):
            raise DocumentError("Machine preferences are unreadable and have been preserved.")

    def set(self, key: str, value) -> None:
        """Persist a local choice without copying it into global sync."""
        candidate = deepcopy(self.data)
        candidate[key] = value
        atomic_write(self.path, (json.dumps(candidate, indent=2) + "\n").encode())
        self.data = candidate
