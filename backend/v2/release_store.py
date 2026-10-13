"""Read-only access to signed updater releases stored on the NAS."""

from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import json
import os
import re
import stat
import threading
from collections import OrderedDict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


MAX_ENVELOPE_BYTES = 64 * 1024
MAX_PAYLOAD_BYTES = 32 * 1024
MAX_SIGNATURE_BYTES = 256
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
PACKAGE_ID_RE = re.compile(r"^[a-f0-9]{32}$")
VERSION_RE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
SHA256_RE = re.compile(r"^[a-fA-F0-9]{64}$")
MANIFEST_FIELDS = {
    "schema_version", "channel", "version", "platform", "architecture",
    "package_id", "package_size", "sha256", "published_at_utc",
}


class ReleaseUnavailable(Exception):
    """The release directory is unset, unavailable, unsafe, or malformed."""


class ReleaseNotFound(ReleaseUnavailable):
    """A requested opaque package ID has no corresponding package file."""


@dataclass
class OpenRelease:
    file_descriptor: int
    size: int
    modified_ns: int
    device: int
    inode: int
    changed_ns: int

    def close(self) -> None:
        os.close(self.file_descriptor)


def _open_directory(path: Path) -> int:
    path = Path(os.path.abspath(path))
    flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = -1
    try:
        descriptor = os.open(path.anchor, flags)
        for component in path.parts[1:]:
            child = os.open(component, flags, dir_fd=descriptor)
            previous = descriptor
            descriptor = child
            os.close(previous)
    except OSError as error:
        if descriptor >= 0:
            os.close(descriptor)
        raise ReleaseUnavailable("release directory unavailable") from error
    if not stat.S_ISDIR(os.fstat(descriptor).st_mode):
        os.close(descriptor)
        raise ReleaseUnavailable("release directory unavailable")
    return descriptor


def _open_child_file(directory_fd: int, name: str) -> OpenRelease:
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(name, flags, dir_fd=directory_fd)
        info = os.fstat(descriptor)
    except FileNotFoundError as error:
        raise ReleaseNotFound("release file not found") from error
    except OSError as error:
        raise ReleaseUnavailable("release file unavailable") from error
    if not stat.S_ISREG(info.st_mode):
        os.close(descriptor)
        raise ReleaseUnavailable("release file unavailable")
    return OpenRelease(descriptor, info.st_size, info.st_mtime_ns, info.st_dev, info.st_ino, info.st_ctime_ns)


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field")
        result[key] = value
    return result


def validate_signed_manifest(raw: bytes) -> dict[str, object]:
    """Validate the strict envelope and stable Windows x64 manifest shape.

    Signature trust and cryptographic verification remain client responsibilities.
    The server never signs, publishes, or modifies release files.
    """
    if not raw or len(raw) > MAX_ENVELOPE_BYTES:
        raise ValueError("invalid envelope size")
    envelope = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object)
    if not isinstance(envelope, dict) or set(envelope) != {"payload", "signature"}:
        raise ValueError("invalid envelope fields")
    if not all(isinstance(envelope[key], str) for key in ("payload", "signature")):
        raise ValueError("invalid envelope values")
    try:
        payload = base64.b64decode(envelope["payload"], validate=True)
        signature = base64.b64decode(envelope["signature"], validate=True)
    except (binascii.Error, ValueError) as error:
        raise ValueError("invalid base64") from error
    if (not payload or len(payload) > MAX_PAYLOAD_BYTES or not signature or
            len(signature) > MAX_SIGNATURE_BYTES or
            base64.b64encode(payload).decode("ascii") != envelope["payload"] or
            base64.b64encode(signature).decode("ascii") != envelope["signature"]):
        raise ValueError("invalid encoded envelope sizes")

    manifest = json.loads(payload.decode("utf-8"), object_pairs_hook=_unique_object)
    if not isinstance(manifest, dict) or set(manifest) != MANIFEST_FIELDS:
        raise ValueError("invalid manifest fields")
    if (type(manifest["schema_version"]) is not int or manifest["schema_version"] != 1 or
            manifest["channel"] != "stable" or manifest["platform"] != "windows" or
            manifest["architecture"] != "x64"):
        raise ValueError("unsupported release manifest")
    if not isinstance(manifest["version"], str) or not VERSION_RE.fullmatch(manifest["version"]):
        raise ValueError("invalid version")
    if not isinstance(manifest["package_id"], str) or not PACKAGE_ID_RE.fullmatch(manifest["package_id"]):
        raise ValueError("invalid package id")
    if type(manifest["package_size"]) is not int or not 0 < manifest["package_size"] <= MAX_PACKAGE_BYTES:
        raise ValueError("invalid package size")
    if not isinstance(manifest["sha256"], str) or not SHA256_RE.fullmatch(manifest["sha256"]):
        raise ValueError("invalid package hash")
    if not isinstance(manifest["published_at_utc"], str):
        raise ValueError("invalid publication timestamp")
    timestamp = manifest["published_at_utc"]
    parsed = datetime.fromisoformat(timestamp[:-1] + "+00:00" if timestamp.endswith("Z") else timestamp)
    if parsed.tzinfo is None or parsed.astimezone(timezone.utc).timestamp() > datetime.now(timezone.utc).timestamp() + 24 * 60 * 60:
        raise ValueError("invalid publication timestamp")
    return manifest


class ReleaseStore:
    def __init__(self, releases_dir: Path | None):
        self._root = releases_dir
        self._package_hash_cache: OrderedDict[tuple[int, int, int, int, int], str] = OrderedDict()
        self._package_hash_cache_lock = threading.Lock()

    @property
    def configured(self) -> bool:
        return self._root is not None

    def open_manifest(self) -> tuple[bytes, OpenRelease, dict[str, object]]:
        root_fd = self._require_root()
        try:
            opened = _open_child_file(root_fd, "stable.manifest.json")
        finally:
            os.close(root_fd)
        try:
            if opened.size > MAX_ENVELOPE_BYTES:
                raise ValueError("manifest too large")
            with os.fdopen(os.dup(opened.file_descriptor), "rb") as stream:
                raw = stream.read(MAX_ENVELOPE_BYTES + 1)
            manifest = validate_signed_manifest(raw)
            return raw, opened, manifest
        except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
            opened.close()
            raise ReleaseUnavailable("release manifest unavailable") from error

    def open_verified_package(self, package_id: str) -> tuple[OpenRelease, str]:
        _raw_manifest, manifest_file, manifest = self.open_manifest()
        manifest_file.close()
        if manifest["package_id"] != package_id:
            raise ReleaseNotFound("package is not the current stable release")
        expected_sha256 = str(manifest["sha256"]).lower()
        expected_size = int(manifest["package_size"])
        if not PACKAGE_ID_RE.fullmatch(package_id):
            raise ReleaseUnavailable("release package unavailable")
        root_fd = self._require_root()
        package_dir_fd = -1
        try:
            flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
            try:
                package_dir_fd = os.open("packages", flags, dir_fd=root_fd)
            except OSError as error:
                raise ReleaseUnavailable("release package unavailable") from error
            if not stat.S_ISDIR(os.fstat(package_dir_fd).st_mode):
                raise ReleaseUnavailable("release package unavailable")
            opened = _open_child_file(package_dir_fd, f"{package_id}.zip")
            if opened.size != expected_size or opened.size <= 0 or opened.size > MAX_PACKAGE_BYTES:
                opened.close()
                raise ReleaseUnavailable("release package size differs from signed manifest")
            info = os.fstat(opened.file_descriptor)
            if os.name == "posix" and info.st_mode & (stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH):
                opened.close()
                raise ReleaseUnavailable("release package must be immutable while published")
            try:
                actual_sha256 = self._verified_sha256(opened, expected_sha256)
            except OSError as error:
                opened.close()
                raise ReleaseUnavailable("release package could not be verified") from error
            except Exception:
                opened.close()
                raise
            return opened, actual_sha256
        finally:
            if package_dir_fd >= 0:
                os.close(package_dir_fd)
            os.close(root_fd)

    def _verified_sha256(self, opened: OpenRelease, expected_sha256: str) -> str:
        key = (opened.device, opened.inode, opened.size, opened.modified_ns, opened.changed_ns)
        if self._file_identity(opened.file_descriptor) != key:
            raise ReleaseUnavailable("release package changed while opening")
        with self._package_hash_cache_lock:
            digest = self._package_hash_cache.get(key)
            if digest is None:
                hasher = hashlib.sha256()
                offset = 0
                while offset < opened.size:
                    read_size = min(1024 * 1024, opened.size - offset)
                    if hasattr(os, "pread"):
                        chunk = os.pread(opened.file_descriptor, read_size, offset)
                    else:
                        os.lseek(opened.file_descriptor, offset, os.SEEK_SET)
                        chunk = os.read(opened.file_descriptor, read_size)
                    if not chunk:
                        raise ReleaseUnavailable("release package changed while hashing")
                    hasher.update(chunk)
                    offset += len(chunk)
                if self._file_identity(opened.file_descriptor) != key:
                    raise ReleaseUnavailable("release package changed while hashing")
                digest = hasher.hexdigest()
                self._package_hash_cache[key] = digest
                self._package_hash_cache.move_to_end(key)
                while len(self._package_hash_cache) > 128:
                    self._package_hash_cache.popitem(last=False)
        if self._file_identity(opened.file_descriptor) != key:
            raise ReleaseUnavailable("release package changed while opening")
        if not hmac.compare_digest(digest, expected_sha256):
            raise ReleaseUnavailable("release package hash differs from signed manifest")
        return digest

    @staticmethod
    def _file_identity(file_descriptor: int) -> tuple[int, int, int, int, int]:
        info = os.fstat(file_descriptor)
        return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns

    def _require_root(self) -> int:
        if self._root is None:
            raise ReleaseUnavailable("update releases are not configured")
        return _open_directory(self._root)
