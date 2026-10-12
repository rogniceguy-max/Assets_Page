# -*- coding: utf-8 -*-
"""Session-bound resumable uploads for the local mock filesystem."""

import hashlib
import os
import re
import secrets
import shutil
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Optional, Tuple

from core.file_service import FileServiceError, resolve_upload_target
from core.nas_manager import local_mock_upload_staging_directory


UPLOAD_RANGE_RE = re.compile(r"^bytes ([0-9]+)-([0-9]+)/([0-9]+)$")
UPLOAD_CHUNK_BYTES = 64 * 1024
MAX_UPLOAD_CHUNK_BYTES = 4 * 1024 * 1024
STAGING_NAME_RE = re.compile(r"^[0-9a-f]{64}\.part$")


@dataclass
class _UploadRecord:
    user_id: int
    session_key: str
    directory_path: str
    filename: str
    size: int
    virtual_path: str
    staging_path: Path
    received: int = 0
    reserved_remaining: int = 0
    complete: bool = False
    lock: threading.Lock = field(default_factory=threading.Lock)


class ResumableUploadStore:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._records: Dict[str, _UploadRecord] = {}
        self._reserved_bytes = 0

    @staticmethod
    def _key(upload_id: str) -> str:
        return hashlib.sha256(upload_id.encode("ascii")).hexdigest()

    @staticmethod
    def _available_space(path: Path) -> int:
        try:
            return shutil.disk_usage(path).free
        except OSError:
            raise FileServiceError(507, "无法确认上传暂存卷的剩余空间") from None

    def begin(
        self,
        user: dict,
        session_key: str,
        directory_path: str,
        filename: str,
        size: int,
    ) -> Tuple[str, dict]:
        if isinstance(size, bool) or not isinstance(size, int) or size < 0:
            raise FileServiceError(400, "文件大小无效")

        _target_path, virtual_path = resolve_upload_target(user, directory_path, filename)
        staging_root = local_mock_upload_staging_directory()
        upload_id = secrets.token_urlsafe(32)
        key = self._key(upload_id)
        staging_path = staging_root / f"{key}.part"

        with self._lock:
            available = self._available_space(staging_root)
            if self._reserved_bytes + size > available:
                raise FileServiceError(507, "上传暂存卷剩余空间不足")
            try:
                descriptor = os.open(
                    str(staging_path),
                    os.O_CREAT | os.O_EXCL | os.O_WRONLY,
                    0o600,
                )
                os.close(descriptor)
            except FileExistsError:
                raise FileServiceError(500, "无法建立上传暂存文件") from None
            self._records[key] = _UploadRecord(
                user_id=int(user["id"]),
                session_key=session_key,
                directory_path=directory_path,
                filename=filename,
                size=size,
                virtual_path=virtual_path,
                staging_path=staging_path,
                reserved_remaining=size,
            )
            self._reserved_bytes += size

        return upload_id, {
            "upload_id": upload_id,
            "offset": 0,
            "size": size,
            "path": virtual_path,
            "complete": False,
        }

    def _get_owned_record(self, upload_id: str, user_id: int, session_key: str):
        if not isinstance(upload_id, str) or not upload_id:
            raise FileServiceError(404, "上传任务不存在")
        try:
            key = self._key(upload_id)
        except UnicodeEncodeError:
            raise FileServiceError(404, "上传任务不存在") from None

        with self._lock:
            record = self._records.get(key)
            if (
                record is None
                or record.user_id != user_id
                or record.session_key != session_key
            ):
                raise FileServiceError(404, "上传任务不存在")
            return key, record

    def status(self, upload_id: str, user_id: int, session_key: str) -> dict:
        _key, record = self._get_owned_record(upload_id, user_id, session_key)
        with record.lock:
            with self._lock:
                if self._records.get(_key) is not record:
                    raise FileServiceError(404, "上传任务不存在")
            return {
                "upload_id": upload_id,
                "offset": record.received,
                "size": record.size,
                "path": record.virtual_path,
                "complete": record.complete,
            }

    def _discard_record(self, key: str, record: _UploadRecord) -> None:
        """Remove one owned task and reclaim its outstanding space reservation."""
        with self._lock:
            if self._records.get(key) is record:
                self._records.pop(key)
                self._reserved_bytes -= record.reserved_remaining
                record.reserved_remaining = 0
        try:
            record.staging_path.unlink(missing_ok=True)
        except OSError:
            pass

    @staticmethod
    def _parse_content_range(value: str) -> Tuple[int, int, int]:
        match = UPLOAD_RANGE_RE.fullmatch(value or "")
        if match is None:
            raise FileServiceError(400, "Content-Range 格式应为 bytes start-end/total")
        start, end, total = (int(part) for part in match.groups())
        if end < start or total <= end:
            raise FileServiceError(400, "Content-Range 范围无效")
        return start, end, total

    def append_chunk(
        self,
        user: dict,
        session_key: str,
        upload_id: str,
        content_range: str,
        content_length: int,
        source,
    ) -> dict:
        key, record = self._get_owned_record(upload_id, int(user["id"]), session_key)
        start, end, total = self._parse_content_range(content_range)
        chunk_length = end - start + 1
        if chunk_length > MAX_UPLOAD_CHUNK_BYTES:
            raise FileServiceError(413, "单个上传分块不能超过 4 MiB")
        if content_length != chunk_length:
            raise FileServiceError(400, "Content-Length 与 Content-Range 不一致")
        if total != record.size:
            raise FileServiceError(409, "上传总长度与初始化不一致", {"expected_offset": record.received})

        with record.lock:
            if record.complete:
                return {
                    "upload_id": upload_id,
                    "offset": record.received,
                    "size": record.size,
                    "path": record.virtual_path,
                    "complete": True,
                }
            if start != record.received:
                raise FileServiceError(
                    409,
                    "上传偏移不匹配",
                    {"expected_offset": record.received},
                )
            if end >= record.size:
                raise FileServiceError(409, "上传数据超过声明大小")

            # Re-check the authenticated user's current group permissions before
            # accepting each chunk. The username is never used as a path.
            try:
                resolve_upload_target(user, record.directory_path, record.filename)
            except FileServiceError as error:
                if error.status_code == 409 and error.details.get("code") == "FILE_EXISTS":
                    self._discard_record(key, record)
                    raise FileServiceError(
                        409,
                        "目标已存在；不会覆盖现有文件",
                        {"code": "FILE_EXISTS"},
                    ) from None
                raise
            with self._lock:
                if self._records.get(key) is not record:
                    raise FileServiceError(404, "上传任务不存在")
                if self._reserved_bytes > self._available_space(record.staging_path.parent):
                    raise FileServiceError(507, "上传暂存卷剩余空间不足")
            remaining = chunk_length
            try:
                with record.staging_path.open("r+b") as output:
                    output.seek(start)
                    output.truncate(start)
                    while remaining:
                        chunk = source.read(min(UPLOAD_CHUNK_BYTES, remaining))
                        if not chunk:
                            output.truncate(start)
                            raise FileServiceError(400, "上传数据未完整到达")
                        output.write(chunk)
                        remaining -= len(chunk)
                    output.flush()
                    os.fsync(output.fileno())
            except FileServiceError:
                raise
            except OSError:
                try:
                    with record.staging_path.open("r+b") as output:
                        output.truncate(start)
                except OSError:
                    pass
                raise FileServiceError(500, "无法写入上传暂存文件") from None

            with self._lock:
                if self._records.get(key) is not record:
                    try:
                        with record.staging_path.open("r+b") as output:
                            output.truncate(start)
                    except OSError:
                        pass
                    raise FileServiceError(404, "上传任务不存在")
                record.received = end + 1
                record.reserved_remaining -= chunk_length
                self._reserved_bytes -= chunk_length
            return {
                "upload_id": upload_id,
                "offset": record.received,
                "size": record.size,
                "path": record.virtual_path,
                "complete": record.complete,
            }

    def complete(self, user: dict, session_key: str, upload_id: str) -> dict:
        key, record = self._get_owned_record(upload_id, int(user["id"]), session_key)
        with record.lock:
            with self._lock:
                if self._records.get(key) is not record:
                    raise FileServiceError(404, "上传任务不存在")
            if record.complete:
                return {
                    "upload_id": upload_id,
                    "offset": record.received,
                    "size": record.size,
                    "path": record.virtual_path,
                    "complete": True,
                }
            if record.received != record.size:
                raise FileServiceError(
                    409,
                    "上传尚未完成",
                    {"expected_offset": record.received},
                )

            try:
                target_path, virtual_path = resolve_upload_target(
                    user, record.directory_path, record.filename
                )
            except FileServiceError as error:
                if error.status_code == 409:
                    self._discard_record(key, record)
                    raise FileServiceError(
                        409,
                        "目标已存在；不会覆盖现有文件",
                        {"code": "FILE_EXISTS"},
                    ) from None
                raise
            try:
                stage_stat = record.staging_path.stat()
                if stage_stat.st_size != record.size:
                    raise FileServiceError(409, "暂存数据长度不匹配")
                # Same-directory filesystem hard-link creation is atomic and
                # fails if the destination exists; never replace an existing file.
                os.link(record.staging_path, target_path)
            except FileExistsError:
                self._discard_record(key, record)
                raise FileServiceError(409, "目标已存在；不会覆盖现有文件", {"code": "FILE_EXISTS"}) from None
            except FileServiceError:
                raise
            except OSError:
                raise FileServiceError(500, "无法安全提交上传文件") from None

            try:
                record.staging_path.unlink()
            except OSError:
                # The committed destination is valid; an orphaned staging link
                # remains hidden and is cleaned with the session record.
                pass
            record.complete = True
            record.virtual_path = virtual_path
            return {
                "upload_id": upload_id,
                "offset": record.received,
                "size": record.size,
                "path": record.virtual_path,
                "complete": True,
            }

    def discard_session(self, session_key: str) -> None:
        with self._lock:
            matching = [
                key for key, record in self._records.items()
                if record.session_key == session_key
            ]
            records = []
            for key in matching:
                record = self._records.pop(key)
                self._reserved_bytes -= record.reserved_remaining
                record.reserved_remaining = 0
                records.append((key, record))

        for _key, record in records:
            with record.lock:
                try:
                    record.staging_path.unlink(missing_ok=True)
                except OSError:
                    pass

    def clear_abandoned_staging(self) -> None:
        """Remove only orphaned upload chunks after process restart."""
        try:
            staging_root = local_mock_upload_staging_directory()
            for entry in staging_root.iterdir():
                if (
                    STAGING_NAME_RE.fullmatch(entry.name)
                    and not entry.is_symlink()
                    and entry.is_file()
                ):
                    try:
                        entry.unlink()
                    except OSError:
                        pass
        except OSError:
            pass


UPLOAD_STORE = ResumableUploadStore()
