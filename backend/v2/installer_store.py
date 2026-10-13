"""Read-only access to first-install packages offered for manual download.

The update channel delivers the client to machines that already run it. This is the
other direction: a colleague with nothing installed needs one file they can run, and
the simplest way to hand it over is a URL on the NAS.

Deliberately separate from :mod:`release_store`. An update package is opaque and is
trusted only because a signed manifest vouches for it, so that store refuses to serve
anything the manifest has not named. A first-install package is a plain executable an
operator drops into a directory, and its integrity is published as a checksum beside
it. Folding the two together would blur a distinction the update path depends on.

What it does share with that store is the part that is not optional:

* A published file must be immutable. Anything with a write bit is mid-copy or left
  open for editing, and serving it would hand out a truncated executable together with
  a checksum that vouches for the truncated bytes — the recipient's verification would
  pass and the file would not run.
* The digest is computed from the same descriptor that is streamed, and the file's
  identity is re-checked either side of the read, so the advertised checksum cannot
  describe a different file from the one the bytes came from.
"""

from __future__ import annotations

import hashlib
import os
import re
import stat
import threading
from collections import OrderedDict
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
HASH_CACHE_ENTRIES = 32

WRITE_BITS = stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH


def newest_setup_name(names) -> str | None:
    """The highest-versioned setup executable among ``names``, or None.

    Ordered by the version in the filename rather than by modification time: a copy or
    a rebuild moves mtimes around, while the version is what the name already promises.
    Names that do not match the convention are ignored rather than guessed at.

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
        # Held across the hash, not just around the cache. A cold cache with several
        # requests arriving together would otherwise have every one of them read and
        # hash the whole file at once — 150 MB each, on a NAS volume, while each holds
        # a worker slot.
        self._hash_lock = threading.Lock()
        self._digests: OrderedDict[tuple[int, int, int, int, int], str] = OrderedDict()

    @property
    def configured(self) -> bool:
        return self._directory is not None

    def latest_name(self) -> str:
        """The newest *publishable* setup executable present.

        Publishable, not merely present: a file still being copied in is writable, and
        it would otherwise win the version sort the moment its first byte landed.
        """
        if self._directory is None:
            raise ReleaseUnavailable("installer directory is not configured")

        directory_fd = _open_directory(self._directory)
        try:
            publishable = []
            for name in os.listdir(directory_fd):
                if SETUP_NAME_RE.fullmatch(name) is None:
                    continue
                try:
                    info = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
                except OSError:
                    continue
                if not stat.S_ISREG(info.st_mode):
                    continue
                if _is_writable(info):
                    continue
                if not 0 < info.st_size <= MAX_INSTALLER_BYTES:
                    continue
                publishable.append(name)
            newest = newest_setup_name(publishable)
        finally:
            os.close(directory_fd)

        if newest is None:
            raise ReleaseNotFound("no installer has been published")
        return newest

    def open_verified(self, name: str) -> tuple[OpenRelease, str]:
        """Opens a published installer and returns it with the digest of its bytes.

        One descriptor serves both, so the checksum a recipient is told to verify
        against is provably the checksum of what they are about to receive.
        """
        opened = self._open(name)
        try:
            if not 0 < opened.size <= MAX_INSTALLER_BYTES:
                raise ReleaseUnavailable("installer size is out of range")
            digest = self._digest(opened)
        except Exception:
            opened.close()
            raise
        return opened, digest

    def sha256(self, name: str) -> str:
        """The published installer's digest."""
        opened, digest = self.open_verified(name)
        opened.close()
        return digest

    def _open(self, name: str) -> OpenRelease:
        if self._directory is None:
            raise ReleaseUnavailable("installer directory is not configured")
        if SETUP_NAME_RE.fullmatch(name) is None:
            raise ReleaseNotFound("unknown installer")

        directory_fd = _open_directory(self._directory)
        try:
            opened = _open_child_file(directory_fd, name)
        finally:
            os.close(directory_fd)

        info = os.fstat(opened.file_descriptor)
        if not stat.S_ISREG(info.st_mode):
            opened.close()
            raise ReleaseUnavailable("installer is not a regular file")
        if _is_writable(info):
            # The same rule the update store applies: a writable published file is a
            # file being written, and a checksum taken from it would endorse a partial
            # download. Ownership and mode are enforced by the operator's publish step.
            opened.close()
            raise ReleaseUnavailable("installer must be immutable while published")
        return opened

    def _digest(self, opened: OpenRelease) -> str:
        key = _identity(opened.file_descriptor)
        with self._hash_lock:
            cached = self._digests.get(key)
            if cached is not None:
                self._digests.move_to_end(key)
                return cached

            hasher = hashlib.sha256()
            offset = 0
            while offset < opened.size:
                size = min(HASH_CHUNK_BYTES, opened.size - offset)
                chunk = _read_at(opened.file_descriptor, size, offset)
                if not chunk:
                    raise ReleaseUnavailable("installer changed while hashing")
                hasher.update(chunk)
                offset += len(chunk)

            if _identity(opened.file_descriptor) != key:
                raise ReleaseUnavailable("installer changed while hashing")

            digest = hasher.hexdigest()
            self._digests[key] = digest
            self._digests.move_to_end(key)
            while len(self._digests) > HASH_CACHE_ENTRIES:
                self._digests.popitem(last=False)
            return digest


def _has_write_bit(mode: int) -> bool:
    """Whether a POSIX mode grants write access to anyone.

    Split from the platform guard below so the decision itself can be tested where the
    store cannot run.
    """
    return bool(mode & WRITE_BITS)


def _is_writable(info: os.stat_result) -> bool:
    # Windows has no meaningful POSIX mode; the store does not run there anyway, and the
    # update store guards the same check the same way.
    return os.name == "posix" and _has_write_bit(info.st_mode)


def _identity(file_descriptor: int) -> tuple[int, int, int, int, int]:
    # ctime is included deliberately: it moves when a file is replaced even if the
    # replacement is made to look identical, so a stale digest is never reused for it.
    info = os.fstat(file_descriptor)
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def _read_at(file_descriptor: int, size: int, offset: int) -> bytes:
    if hasattr(os, "pread"):
        return os.pread(file_descriptor, size, offset)
    os.lseek(file_descriptor, offset, os.SEEK_SET)
    return os.read(file_descriptor, size)
