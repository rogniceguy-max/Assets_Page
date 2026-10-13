"""Read-only access to first-install packages offered for manual download.

The update channel delivers the client to machines that already run it. This is the
other direction: a colleague with nothing installed needs one file they can run, and
the simplest way to hand it over is a URL on the NAS.

Deliberately separate from :mod:`release_store`. An update package is opaque and is
trusted only because a signed manifest vouches for it, so the store refuses to serve
anything the manifest has not named. A first-install package is a plain executable an
operator drops into a directory, and its integrity is published as a checksum beside
it. Folding the two together would blur a distinction the update path depends on.
"""

from __future__ import annotations

import hashlib
import os
import re
import threading
from pathlib import Path

from .release_store import (
    OpenRelease,
    ReleaseNotFound,
    ReleaseUnavailable,
    _open_child_file,
    _open_directory,
)

# The operator's filename carries the version, which is what "newest" is decided by.
# A name that does not match is left alone rather than guessed at.
SETUP_NAME_RE = re.compile(
    r"^MuYingDrive\.Setup-((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))\.exe$"
)

# A first-install package is a self-contained executable, so it is large; this only
# rejects something so far outside the expected shape that serving it is a mistake.
MAX_INSTALLER_BYTES = 1024 * 1024 * 1024
HASH_CHUNK_BYTES = 1024 * 1024


def newest_setup_name(names) -> str | None:
    """The highest-versioned setup executable among ``names``, or None.

    Ordered by the version in the filename rather than by modification time: a copy or
    a rebuild moves mtimes around, while the version is what the name already promises.
    Names that do not match the convention are ignored rather than guessed at — an
    operator's stray file must never become the download.

    Separate from the directory walk so the choice can be tested where the directory
    primitives do not exist (they are POSIX-only; see _open_directory).
    """
    newest: tuple[tuple[int, int, int], str] | None = None
    for name in names:
        match = SETUP_NAME_RE.fullmatch(name)
        if match is None:
            continue
        version = tuple(int(part) for part in match.group(1).split("."))
        if newest is None or version > newest[0]:
            newest = (version, name)
    return None if newest is None else newest[1]


class InstallerStore:
    """Serves the newest setup executable from one directory."""

    def __init__(self, directory: Path | None) -> None:
        self._directory = directory
        self._lock = threading.Lock()
        # Keyed by (name, size, mtime) so a replaced file is never served a stale digest.
        self._digests: dict[tuple[str, int, int], str] = {}

    @property
    def configured(self) -> bool:
        return self._directory is not None

    def latest_name(self) -> str:
        """The name of the highest-versioned setup executable present."""
        if self._directory is None:
            raise ReleaseUnavailable("installer directory is not configured")

        directory_fd = _open_directory(self._directory)
        try:
            newest = newest_setup_name(os.listdir(directory_fd))
        finally:
            os.close(directory_fd)

        if newest is None:
            raise ReleaseNotFound("no installer has been published")
        return newest

    def open(self, name: str) -> OpenRelease:
        """Opens a published installer for streaming.

        The name must be one this store would advertise. Opening is by name relative to
        a directory descriptor with ``O_NOFOLLOW``, so a symlink swapped in behind a
        valid name is refused rather than followed.
        """
        if self._directory is None:
            raise ReleaseUnavailable("installer directory is not configured")
        if SETUP_NAME_RE.fullmatch(name) is None:
            raise ReleaseNotFound("unknown installer")

        directory_fd = _open_directory(self._directory)
        try:
            opened = _open_child_file(directory_fd, name)
        finally:
            os.close(directory_fd)

        if opened.size <= 0 or opened.size > MAX_INSTALLER_BYTES:
            opened.close()
            raise ReleaseUnavailable("installer size is out of range")
        return opened

    def sha256(self, name: str) -> str:
        """The published installer's digest, computed once per file version.

        Hashing 150 MB is not free, so the answer is cached; the cache key includes the
        size and mtime so publishing a replacement invalidates it rather than serving the
        previous file's checksum to someone about to verify a download with it.
        """
        opened = self.open(name)
        try:
            key = (name, opened.size, opened.modified_ns)
            with self._lock:
                cached = self._digests.get(key)
            if cached is not None:
                return cached

            os.lseek(opened.file_descriptor, 0, os.SEEK_SET)
            hasher = hashlib.sha256()
            while True:
                chunk = os.read(opened.file_descriptor, HASH_CHUNK_BYTES)
                if not chunk:
                    break
                hasher.update(chunk)
            digest = hasher.hexdigest()

            with self._lock:
                self._digests.clear()
                self._digests[key] = digest
            return digest
        finally:
            opened.close()
