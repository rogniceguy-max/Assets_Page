# -*- coding: utf-8 -*-
"""Permission-checked virtual filesystem for the local mock backend."""

import os
import re
import stat
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Tuple

from core.nas_manager import (
    FILM_DIR_NAME,
    PERSONAL_DIR_NAME,
    PUBLIC_DIR_NAME,
    WRITING_DIR_NAME,
    ensure_local_mock_roots,
)


PERSONAL_ALIAS = "我的空间"
WINDOWS_RESERVED_NAMES = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{number}" for number in range(1, 10)),
    *(f"LPT{number}" for number in range(1, 10)),
}
WINDOWS_INVALID_CHARS = set('<>:"|?*')


class FileServiceError(Exception):
    def __init__(self, status_code: int, message: str, details: dict = None):
        super().__init__(message)
        self.status_code = status_code
        self.message = message
        self.details = details or {}


def _validate_component(component: str) -> None:
    if (
        not component
        or component in {".", ".."}
        or "/" in component
        or "\\" in component
        or "\x00" in component
        or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in component)
        or any(char in WINDOWS_INVALID_CHARS for char in component)
        or component.endswith((" ", "."))
    ):
        raise FileServiceError(400, "路径或文件名无效")

    stem = component.split(".", 1)[0].rstrip(" .").upper()
    if stem in WINDOWS_RESERVED_NAMES:
        raise FileServiceError(400, "路径或文件名无效")

    try:
        if len(component.encode("utf-8")) > 255:
            raise FileServiceError(400, "路径或文件名过长")
    except UnicodeEncodeError:
        raise FileServiceError(400, "路径或文件名无效") from None


def parse_virtual_path(raw_path: str) -> Tuple[str, ...]:
    """Parse one already URL-decoded canonical virtual path."""
    if raw_path in ("", "/"):
        return ()
    if not isinstance(raw_path, str) or not raw_path.startswith("/"):
        raise FileServiceError(400, "路径必须是以 / 开头的虚拟路径")
    if "\\" in raw_path or "\x00" in raw_path:
        raise FileServiceError(400, "路径无效")

    components = raw_path[1:].split("/")
    for component in components:
        _validate_component(component)
    return tuple(components)


def _group(user: dict) -> str:
    group_type = user.get("group_type")
    if group_type not in {"film", "writing"}:
        raise FileServiceError(403, "没有文件访问权限")
    return group_type


def _safe_existing_path(root: Path, components: Tuple[str, ...]) -> Path:
    """Resolve existing components without following any symlink."""
    root = root.resolve(strict=True)
    current = root
    for component in components:
        _validate_component(component)
        candidate = current / component
        try:
            if candidate.is_symlink():
                raise FileServiceError(404, "文件或目录不存在")
            candidate.lstat()
            current = candidate
        except FileNotFoundError:
            raise FileServiceError(404, "文件或目录不存在") from None

    try:
        resolved = current.resolve(strict=True)
        if not resolved.is_relative_to(root):
            raise FileServiceError(404, "文件或目录不存在")
    except (OSError, RuntimeError, ValueError):
        raise FileServiceError(404, "文件或目录不存在") from None
    return resolved


def _directory_target(user: dict, components: Tuple[str, ...]):
    roots = ensure_local_mock_roots()
    group_type = _group(user)

    if not components:
        return None, "/", False, "root"

    top = components[0]
    if top == PERSONAL_DIR_NAME:
        if len(components) == 1:
            return roots[PERSONAL_DIR_NAME], f"/{PERSONAL_DIR_NAME}", False, "personal_index"
        if components[1] != PERSONAL_ALIAS:
            # Never distinguish another user's directory from a missing path.
            raise FileServiceError(404, "文件或目录不存在")
        user_id = user.get("id")
        if not isinstance(user_id, int) or user_id <= 0:
            raise FileServiceError(403, "没有文件访问权限")
        physical_root = roots[PERSONAL_DIR_NAME] / str(user_id)
        resolved_root = _safe_existing_path(roots[PERSONAL_DIR_NAME], (str(user_id),))
        virtual_path = f"/{PERSONAL_DIR_NAME}/{PERSONAL_ALIAS}"
        remainder = components[2:]
        target = _safe_existing_path(resolved_root, remainder)
        return target, virtual_path + _suffix(remainder), True, "personal"

    if top == PUBLIC_DIR_NAME:
        physical_root = roots[PUBLIC_DIR_NAME]
        can_write = True
        category = "public"
    elif top == FILM_DIR_NAME:
        physical_root = roots[FILM_DIR_NAME]
        can_write = group_type == "film"
        category = "film"
    elif top == WRITING_DIR_NAME:
        physical_root = roots[WRITING_DIR_NAME]
        can_write = group_type == "writing"
        category = "writing"
    else:
        raise FileServiceError(404, "文件或目录不存在")

    remainder = components[1:]
    target = _safe_existing_path(physical_root, remainder)
    return target, f"/{top}" + _suffix(remainder), can_write, category


def _suffix(components: Tuple[str, ...]) -> str:
    return "" if not components else "/" + "/".join(components)


def _entry(
    virtual_path: str,
    name: str,
    item_type: str,
    size: int,
    modified: float,
    writable=None,
) -> dict:
    child_path = (virtual_path.rstrip("/") + "/" + name) if virtual_path != "/" else "/" + name
    entry = {
        "name": name,
        "path": child_path,
        "type": item_type,
        "size": size,
        "modified_at": (
            datetime.fromtimestamp(modified, timezone.utc).isoformat()
            if modified > 0 else None
        ),
    }
    if writable is not None:
        entry["writable"] = writable
    return entry


def _visible_roots(group_type: str) -> List[dict]:
    if group_type not in {"film", "writing"}:
        raise FileServiceError(403, "没有文件访问权限")
    return [
        _entry("/", PUBLIC_DIR_NAME, "directory", 0, 0, True),
        _entry("/", FILM_DIR_NAME, "directory", 0, 0, group_type == "film"),
        _entry("/", WRITING_DIR_NAME, "directory", 0, 0, group_type == "writing"),
        _entry("/", PERSONAL_DIR_NAME, "directory", 0, 0, False),
    ]


def list_directory(user: dict, raw_path: str) -> dict:
    components = parse_virtual_path(raw_path)
    group_type = _group(user)
    target, virtual_path, can_write, category = _directory_target(user, components)

    if category == "root":
        ensure_local_mock_roots()
        return {"path": "/", "can_upload": False, "entries": _visible_roots(group_type)}

    if category == "personal_index":
        personal_path = f"/{PERSONAL_DIR_NAME}/{PERSONAL_ALIAS}"
        entry = _entry(
            personal_path.rsplit("/", 1)[0],
            PERSONAL_ALIAS,
            "directory",
            0,
            0,
            True,
        )
        return {"path": virtual_path, "can_upload": False, "entries": [entry]}

    try:
        target_stat = target.stat()
    except OSError:
        raise FileServiceError(404, "文件或目录不存在") from None
    if not stat.S_ISDIR(target_stat.st_mode):
        raise FileServiceError(400, "指定路径不是目录")

    entries = []
    try:
        for child in target.iterdir():
            try:
                _validate_component(child.name)
                child_stat = child.lstat()
            except (FileServiceError, OSError):
                continue
            if stat.S_ISLNK(child_stat.st_mode):
                continue
            if stat.S_ISDIR(child_stat.st_mode):
                item_type = "directory"
                size = 0
            elif stat.S_ISREG(child_stat.st_mode):
                item_type = "file"
                size = child_stat.st_size
            else:
                continue
            entries.append(
                _entry(
                    virtual_path,
                    child.name,
                    item_type,
                    size,
                    child_stat.st_mtime,
                    can_write if item_type == "directory" else False,
                )
            )
    except OSError:
        raise FileServiceError(404, "文件或目录不存在") from None

    entries.sort(key=lambda item: (item["type"] != "directory", item["name"].casefold()))
    return {"path": virtual_path, "can_upload": can_write, "entries": entries}


def open_download(user: dict, raw_path: str):
    components = parse_virtual_path(raw_path)
    if not components:
        raise FileServiceError(400, "必须指定文件路径")

    directory, _virtual_path, _can_write, _category = _directory_target(user, components[:-1])
    # _directory_target expects the parent directory path; a personal alias is
    # therefore translated using the authenticated user's ID only.
    if directory is None:
        raise FileServiceError(400, "必须指定文件路径")
    filename = components[-1]
    _validate_component(filename)

    file_path = directory / filename
    try:
        if file_path.is_symlink():
            raise FileServiceError(404, "文件或目录不存在")
        file_stat = file_path.lstat()
        if not stat.S_ISREG(file_stat.st_mode):
            raise FileServiceError(404, "文件或目录不存在")
        resolved_file = file_path.resolve(strict=True)
        resolved_directory = directory.resolve(strict=True)
        if not resolved_file.is_relative_to(resolved_directory):
            raise FileServiceError(404, "文件或目录不存在")
        flags = os.O_RDONLY
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        descriptor = os.open(str(resolved_file), flags)
        handle = os.fdopen(descriptor, "rb")
        opened_stat = os.fstat(handle.fileno())
        if not stat.S_ISREG(opened_stat.st_mode):
            handle.close()
            raise FileServiceError(404, "文件或目录不存在")
    except FileServiceError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise FileServiceError(404, "文件或目录不存在") from None

    return handle, filename, opened_stat.st_size


def resolve_upload_target(user: dict, raw_directory: str, filename: str):
    """Authorize and resolve a single-file upload destination without creating it."""
    _validate_component(filename)
    components = parse_virtual_path(raw_directory)
    directory, virtual_path, can_write, _category = _directory_target(user, components)
    if directory is None:
        raise FileServiceError(403, "该位置不允许上传")
    if not can_write:
        raise FileServiceError(403, "该目录为只读")

    try:
        directory_stat = directory.stat()
        if not stat.S_ISDIR(directory_stat.st_mode):
            raise FileServiceError(400, "上传目标必须是目录")
        target = directory / filename
        if target.is_symlink():
            raise FileServiceError(409, "目标已存在；不会覆盖现有文件", {"code": "FILE_EXISTS"})
        try:
            target.lstat()
        except FileNotFoundError:
            pass
        else:
            raise FileServiceError(409, "目标已存在；不会覆盖现有文件", {"code": "FILE_EXISTS"})
    except FileServiceError:
        raise
    except OSError:
        raise FileServiceError(404, "目录不存在") from None

    virtual_target = (virtual_path.rstrip("/") + "/" + filename)
    return target, virtual_target


def parse_range_header(value: str, file_size: int):
    """Parse one RFC 7233 byte range; return (start, end) or None."""
    if not value:
        return None
    if not value.startswith("bytes=") or "," in value:
        raise FileServiceError(416, "只支持单个字节范围")

    byte_range = value[6:].strip()
    if re.fullmatch(r"[0-9]*-[0-9]*", byte_range) is None:
        raise FileServiceError(416, "字节范围无效")
    first, last = byte_range.split("-", 1)
    if not first and not last:
        raise FileServiceError(416, "字节范围无效")

    try:
        if first:
            start = int(first)
            end = int(last) if last else file_size - 1
            if start < 0 or end < start or start >= file_size:
                raise ValueError
            return start, min(end, file_size - 1)

        suffix_length = int(last)
        if suffix_length <= 0 or file_size <= 0:
            raise ValueError
        return max(0, file_size - suffix_length), file_size - 1
    except ValueError:
        raise FileServiceError(416, "字节范围不可满足") from None
