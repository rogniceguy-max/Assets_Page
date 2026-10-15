"""Logical file catalog and authenticated control API for File Storage V2.

File bytes are owned by the separate transfer service. This module stores only logical
paths, immutable revision metadata, operation state, scoped HMAC tickets, and durable
change events in the project-workbook SQLite database.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import mimetypes
import re
import sqlite3
import time
import unicodedata
import uuid
from datetime import datetime, timezone
from typing import Any, Mapping
from urllib.parse import parse_qs, unquote, urlsplit

from .project_workbooks.projects_api import ApiFault, Response, error_response, json_response
from .project_workbooks.sqlite_store import SqliteProjectWorkbookStore, _now, _uid

API_ROOT = "/api/v2"
FILES_ROOT = API_ROOT + "/files"
OPERATIONS_ROOT = API_ROOT + "/file-operations"
DIRECTORIES_ROOT = API_ROOT + "/directories"
CHANGES_PATH = API_ROOT + "/sync/changes"
ACK_PATH = API_ROOT + "/sync/ack"
MEDIA_WORKER_ROOT = API_ROOT + "/media-worker"
MAX_CONTROL_BODY = 4 * 1024 * 1024
MAX_BATCH_ITEMS = 512
MAX_BATCH_DIRECTORIES = 4096
MAX_PAGE_SIZE = 500
TICKET_LIFETIME_SECONDS = 15 * 60
THUMBNAIL_GENERATOR_VERSION = "m4-webp-640-v1"
THUMBNAIL_MAX_BYTES = 8 * 1024 * 1024
PATH_MAX_CHARS = 4096
SEGMENT_MAX_CHARS = 255
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
MIME_RE = re.compile(r"^[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+$")

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic"}
VIDEO_EXTENSIONS = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".mpeg", ".mpg"}


def _canonical(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, ensure_ascii=False,
                      separators=(",", ":"), allow_nan=False).encode("utf-8")


def _b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def _unb64url(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def _json_object(value: str | None) -> dict[str, Any]:
    try:
        decoded = json.loads(value)
    except (TypeError, json.JSONDecodeError):
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "节拍表数据无法安全更新。") from None
    if not isinstance(decoded, dict):
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "节拍表数据无法安全更新。")
    return decoded


def _opaque_id(value: Any, label: str = "操作标识") -> str:
    if not isinstance(value, str) or len(value) > 64:
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。") from None
    if str(parsed) != value.lower():
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")
    return str(parsed)


def _positive_int(value: Any, label: str, *, allow_zero: bool = False,
                  maximum: int = 2**63 - 1) -> int:
    minimum = 0 if allow_zero else 1
    if type(value) is not int or not minimum <= value <= maximum:
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")
    return value


def _logical_path(value: Any, *, allow_empty: bool = False) -> tuple[str, str]:
    if not isinstance(value, str) or len(value) > PATH_MAX_CHARS:
        raise ApiFault(422, "INVALID_PATH", "逻辑路径无效。")
    if any(0xD800 <= ord(char) <= 0xDFFF or ord(char) < 32 or ord(char) == 127 for char in value):
        raise ApiFault(422, "INVALID_PATH", "逻辑路径无效。")
    if value == "" and allow_empty:
        return "", ""
    normalized = unicodedata.normalize("NFC", value)
    parts = normalized.split("/")
    if (not normalized or normalized.startswith("/") or normalized.endswith("/")
            or "\\" in normalized or any(part in {"", ".", ".."} for part in parts)
            or any(len(part) > SEGMENT_MAX_CHARS or ":" in part for part in parts)):
        raise ApiFault(422, "INVALID_PATH", "逻辑路径必须是规范的相对路径。")
    return normalized, normalized.casefold()


def _actor(actor: Any) -> tuple[str, str, dict[str, Any]]:
    user = actor.get("user", {}) if isinstance(actor, dict) else {}
    if not isinstance(user, dict) or user.get("id") is None:
        raise ApiFault(401, "SESSION_REQUIRED", "请先登录。")
    user_id = str(user.get("id"))
    name = str(user.get("username") or user.get("name") or "同事")[:200]
    return user_id, name, user


def _writable(actor: Any) -> tuple[str, str, dict[str, Any]]:
    user_id, name, user = _actor(actor)
    role = actor.get("role") if isinstance(actor, dict) else None
    role = role or user.get("role")
    can_write = actor.get("can_write", user.get("can_write", True)) if isinstance(actor, dict) else True
    if role == "visitor" or user.get("group_type") == "visitor" or can_write is False:
        raise ApiFault(403, "FORBIDDEN", "访客账户只有只读权限。")
    return user_id, name, user


def _media_kind(media_type: str, logical_path: str) -> str:
    if media_type.startswith("image/"):
        return "image"
    if media_type.startswith("video/"):
        return "video"
    suffix = "." + logical_path.rsplit(".", 1)[-1].casefold() if "." in logical_path.rsplit("/", 1)[-1] else ""
    if suffix in IMAGE_EXTENSIONS:
        return "image"
    if suffix in VIDEO_EXTENSIONS:
        return "video"
    return "other"


def _normalize_mime(value: Any, logical_path: str) -> str:
    if value is None:
        value = mimetypes.guess_type(logical_path)[0] or "application/octet-stream"
    if not isinstance(value, str) or len(value) > 255 or not MIME_RE.fullmatch(value):
        raise ApiFault(422, "INVALID_MEDIA_TYPE", "文件类型无效。")
    return value.lower()


def _store_json(row: sqlite3.Row | Mapping[str, Any] | None) -> dict[str, Any] | None:
    if row is None:
        return None
    return dict(row)


class FileStorageService:
    """SQLite control plane. It never opens or receives file-content bytes."""

    def __init__(self, store: SqliteProjectWorkbookStore, *, transfer_base_url: str | None,
                 ticket_secret: str | bytes | None, media_worker_token: str | None = None):
        self.store = store
        self.transfer_base_url = self._validate_transfer_url(transfer_base_url)
        self.media_worker_token = (media_worker_token if isinstance(media_worker_token, str)
                                   and len(media_worker_token) >= 32 else None)
        if isinstance(ticket_secret, str):
            ticket_secret = ticket_secret.encode("utf-8")
        self.ticket_secret = ticket_secret if isinstance(ticket_secret, bytes) and len(ticket_secret) >= 32 else None

    @staticmethod
    def _validate_transfer_url(value: str | None) -> str | None:
        if not isinstance(value, str) or not value:
            return None
        parsed = urlsplit(value)
        if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
            return None
        return value.rstrip("/")

    def _require_transfer_config(self) -> tuple[str, bytes]:
        if self.transfer_base_url is None or self.ticket_secret is None:
            raise ApiFault(503, "TRANSFER_UNAVAILABLE", "文件传输服务尚未配置。")
        return self.transfer_base_url, self.ticket_secret

    def _ticket(self, claims: dict[str, Any]) -> str:
        _, secret = self._require_transfer_config()
        payload = _canonical(claims)
        return "v1." + _b64url(payload) + "." + _b64url(hmac.new(secret, payload, hashlib.sha256).digest())

    def _scope(self, actor: Any, scope_id: Any, project_id: Any = None, *, write: bool = False,
               allow_deleted_project: bool = False) -> tuple[str, str | None, str | None]:
        user_id, _name, user = _actor(actor)
        if not isinstance(scope_id, str) or len(scope_id) > 256 or ":" not in scope_id:
            raise ApiFault(422, "INVALID_SCOPE", "文件范围无效。")
        kind, key = scope_id.split(":", 1)
        if kind == "department" and key in {"film", "writing"}:
            own = user.get("group_type")
            role = actor.get("role") if isinstance(actor, dict) else None
            is_visitor = role == "visitor" or user.get("role") == "visitor" or own == "visitor"
            if own not in {"film", "writing"} and not is_visitor:
                raise ApiFault(403, "FORBIDDEN", "当前账户无权访问此文件范围。")
            if write and (is_visitor or own != key):
                raise ApiFault(403, "FORBIDDEN", "此部门空间为只读。")
            return scope_id, None, None
        if scope_id == "public:assets":
            return scope_id, None, None
        if kind == "personal":
            if key != user_id:
                raise ApiFault(403, "FORBIDDEN", "只能访问自己的个人空间。")
            return scope_id, None, user_id
        if kind == "project":
            project = _opaque_id(key, "项目标识")
            if project_id is not None and project_id != project:
                raise ApiFault(422, "INVALID_SCOPE", "项目范围与项目标识不一致。")
            with self.store._reading() as conn:
                row = conn.execute("SELECT deleted_at FROM projects WHERE id=?", (project,)).fetchone()
            if row is None or (row["deleted_at"] is not None and not allow_deleted_project):
                raise ApiFault(404, "NOT_FOUND", "项目不存在。")
            if row["deleted_at"] is not None and write:
                raise ApiFault(409, "PROJECT_DELETED", "项目已删除，请先恢复项目。")
            return "project:" + project, project, None
        raise ApiFault(422, "INVALID_SCOPE", "文件范围无效。")

    def _read_scope(self, actor: Any, scope_id: Any, project_id: Any = None,
                    *, allow_deleted_project: bool = False) -> tuple[str, str | None, str | None]:
        user_id, _name, user = _actor(actor)
        role = actor.get("role") if isinstance(actor, dict) else None
        is_visitor = (role == "visitor" or user.get("role") == "visitor"
                      or user.get("group_type") == "visitor" or user_id == "0")
        if is_visitor and isinstance(scope_id, str) and scope_id.startswith("personal:"):
            raise ApiFault(403, "FORBIDDEN", "访客账户无权访问此文件范围。")
        if scope_id == "all":
            return "all", None, None
        return self._scope(actor, scope_id, project_id, allow_deleted_project=allow_deleted_project)

    @staticmethod
    def _event(conn: sqlite3.Connection, entity_kind: str, entity_id: str, action: str,
               *, file_id: str | None = None, scope_id: str | None = None,
               project_id: str | None = None, logical_path: str | None = None,
               previous_scope_id: str | None = None, previous_project_id: str | None = None,
               previous_logical_path: str | None = None, revision_id: str | None = None,
               version: int | None = None, source: str | None = None,
               actor_id: str | None = None, actor_name: str | None = None) -> int:
        cursor = conn.execute("""INSERT INTO file_events
            (entity_kind,entity_id,file_id,scope_id,project_id,action,logical_path,
             previous_scope_id,previous_project_id,previous_logical_path,revision_id,version,
             source,actor_id,actor_name,created_at)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (entity_kind, entity_id, file_id, scope_id, project_id, action, logical_path,
             previous_scope_id, previous_project_id, previous_logical_path, revision_id, version,
             source, actor_id, actor_name, _now()))
        return int(cursor.lastrowid)

    @staticmethod
    def _ensure_parent_directories(conn: sqlite3.Connection, scope_id: str, project_id: str | None,
                                   owner_user_id: str | None, logical_path: str,
                                   actor_id: str, actor_name: str) -> None:
        parent = logical_path.rpartition("/")[0]
        if not parent:
            return
        parts = parent.split("/")
        for index in range(1, len(parts) + 1):
            current = "/".join(parts[:index])
            key = current.casefold()
            if conn.execute("SELECT 1 FROM files WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL",
                            (scope_id, key)).fetchone():
                raise ApiFault(409, "PATH_OCCUPIED", "同名文件已占用文件夹路径。")
            existing = conn.execute("""SELECT directory_id FROM file_directories
                WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL""", (scope_id, key)).fetchone()
            if existing:
                continue
            directory_id = _uid()
            now = _now()
            conn.execute("""INSERT INTO file_directories
                (directory_id,scope_id,project_id,owner_user_id,logical_path,logical_path_key,
                 version,created_at,updated_at) VALUES(?,?,?,?,?,?,1,?,?)""",
                (directory_id, scope_id, project_id, owner_user_id, current, key, now, now))
            FileStorageService._event(conn, "directory", directory_id, "create", scope_id=scope_id,
                project_id=project_id, logical_path=current, actor_id=actor_id, actor_name=actor_name)

    @staticmethod
    def _assert_path_free(conn: sqlite3.Connection, scope_id: str, key: str,
                          *, except_file_id: str | None = None,
                          except_directory_id: str | None = None) -> None:
        file_query = "SELECT 1 FROM files WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL"
        file_params: list[Any] = [scope_id, key]
        if except_file_id:
            file_query += " AND file_id<>?"; file_params.append(except_file_id)
        if conn.execute(file_query, file_params).fetchone():
            raise ApiFault(409, "PATH_OCCUPIED", "目标路径已被文件占用。")
        directory_query = "SELECT 1 FROM file_directories WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL"
        directory_params: list[Any] = [scope_id, key]
        if except_directory_id:
            directory_query += " AND directory_id<>?"; directory_params.append(except_directory_id)
        if conn.execute(directory_query, directory_params).fetchone():
            raise ApiFault(409, "PATH_OCCUPIED", "目标路径已被文件夹占用。")

    @staticmethod
    def _assert_parent_directories_active(conn: sqlite3.Connection, scope_id: str, logical_path: str) -> None:
        parent = logical_path.rpartition("/")[0]
        if not parent:
            return
        parts = parent.split("/")
        for index in range(1, len(parts) + 1):
            key = "/".join(parts[:index]).casefold()
            row = conn.execute("""SELECT deleted_at FROM file_directories
                WHERE scope_id=? AND logical_path_key=? ORDER BY deleted_at IS NULL DESC LIMIT 1""",
                (scope_id, key)).fetchone()
            if row is None or row["deleted_at"] is not None:
                raise ApiFault(409, "PARENT_DELETED", "上级文件夹已删除或不存在，请先恢复文件夹。")

    @staticmethod
    def _cursor_decode(cursor: Any) -> tuple[str, str, str, int] | None:
        if cursor is None:
            return None
        if not isinstance(cursor, str) or not 1 <= len(cursor) <= 4096:
            raise ApiFault(422, "INVALID_CURSOR", "文件列表游标无效。")
        try:
            raw = _unb64url(cursor)
            value = json.loads(raw.decode("utf-8"))
            if (not isinstance(value, list) or len(value) != 4
                    or not all(isinstance(item, str) for item in value[:3])
                    or type(value[3]) is not int or value[3] not in {0, 1}):
                raise ValueError()
            return value[0], value[1], value[2], value[3]
        except (ValueError, UnicodeDecodeError, json.JSONDecodeError):
            raise ApiFault(422, "INVALID_CURSOR", "文件列表游标无效。") from None

    @staticmethod
    def _cursor_encode(sort_key: tuple[str, str, str, int]) -> str:
        return _b64url(_canonical(list(sort_key)))

    @staticmethod
    def _directory_payload(row: sqlite3.Row) -> dict[str, Any]:
        return {
            "is_directory": True,
            "directory_id": row["directory_id"],
            "file_id": None,
            "scope_id": row["scope_id"],
            "project_id": row["project_id"],
            "owner_user_id": row["owner_user_id"],
            "logical_path": row["logical_path"],
            "name": row["logical_path"].rsplit("/", 1)[-1],
            "media_kind": None,
            "media_type": None,
            "size": 0,
            "sha256": None,
            "current_revision_id": None,
            "version": row["version"],
            "deleted_at": row["deleted_at"],
            "updated_at": row["updated_at"],
        }

    @staticmethod
    def _file_payload(row: sqlite3.Row) -> dict[str, Any]:
        return {
            "is_directory": False,
            "directory_id": None,
            "file_id": row["file_id"],
            "scope_id": row["scope_id"],
            "project_id": row["project_id"],
            "owner_user_id": row["owner_user_id"],
            "logical_path": row["logical_path"],
            "name": row["logical_path"].rsplit("/", 1)[-1],
            "media_kind": row["media_kind"],
            "media_type": row["media_type"],
            "size": row["size"],
            "sha256": row["sha256"],
            "current_revision_id": row["current_revision_id"],
            "version": row["version"],
            "deleted_at": row["deleted_at"],
            "updated_at": row["updated_at"],
        }

    def list_files(self, actor: Any, *, scope_id: str, project_id: str | None = None,
                   parent_path: str = "", media_kind: str = "all", include_deleted: bool = False,
                   cursor: Any = None, limit: int = 100) -> dict[str, Any]:
        scope_id, scoped_project, _owner = self._read_scope(
            actor, scope_id, project_id, allow_deleted_project=include_deleted)
        if media_kind not in {"all", "image", "video", "other"}:
            raise ApiFault(422, "INVALID_MEDIA_KIND", "媒体类型筛选无效。")
        if type(include_deleted) is not bool:
            raise ApiFault(422, "INVALID_REQUEST", "删除项筛选无效。")
        if type(limit) is not int or not 1 <= limit <= MAX_PAGE_SIZE:
            raise ApiFault(422, "INVALID_PAGE_SIZE", "文件列表页大小无效。")
        parent, parent_key = _logical_path(parent_path, allow_empty=True)
        if scope_id == "all" and parent:
            raise ApiFault(422, "INVALID_REQUEST", "聚合媒体列表不支持文件夹路径筛选。")
        decoded = self._cursor_decode(cursor)
        args: list[Any] = []
        query = """SELECT f.*,r.size,r.sha256 FROM files f
            JOIN file_revisions r ON r.revision_id=f.current_revision_id
            WHERE f.current_revision_id IS NOT NULL"""
        if not include_deleted:
            query += " AND f.deleted_at IS NULL"
        if scope_id == "all":
            user_id, _name, user = _actor(actor)
            role = actor.get("role") if isinstance(actor, dict) else None
            is_visitor = (role == "visitor" or user.get("role") == "visitor"
                          or user.get("group_type") == "visitor" or user_id == "0")
            visible_scopes = ["public:assets"]
            if is_visitor:
                visible_scopes.extend(["department:film", "department:writing"])
            elif user.get("group_type") in {"film", "writing"}:
                visible_scopes.append("department:" + str(user["group_type"]))
            query += " AND (f.scope_id IN (" + ",".join("?" for _ in visible_scopes) + ")"
            args.extend(visible_scopes)
            if not is_visitor:
                query += " OR f.scope_id=?"
                args.append("personal:" + user_id)
            query += """ OR (f.scope_id LIKE 'project:%' AND EXISTS (
                    SELECT 1 FROM projects p WHERE p.id=f.project_id AND (p.deleted_at IS NULL OR ?))))"""
            args.append(int(include_deleted))
        else:
            query += " AND f.scope_id=?"; args.append(scope_id)
        if scoped_project is not None:
            query += " AND f.project_id=?"; args.append(scoped_project)
        elif project_id is not None:
            requested_project = _opaque_id(project_id, "项目标识")
            query += " AND f.project_id=?"; args.append(requested_project)
        if media_kind != "all":
            query += " AND f.media_kind=?"; args.append(media_kind)
        if scope_id != "all" and media_kind == "all":
            if parent_key:
                child_prefix = parent_key + "/"
                # The range uses idx_files_scope_media_path; the final predicate
                # restricts it to direct children rather than all descendants.
                query += " AND f.logical_path_key>=? AND f.logical_path_key<? AND instr(substr(f.logical_path_key,?),'/')=0"
                args.extend([child_prefix, parent_key + "0", len(child_prefix) + 1])
            else:
                query += " AND instr(f.logical_path_key,'/')=0"
        if decoded is not None:
            query += " AND (f.scope_id,f.logical_path_key,f.file_id,1)>(?,?,?,?)"
            args.extend(decoded)
        query += " ORDER BY f.scope_id,f.logical_path_key,f.file_id LIMIT ?"
        args.append(limit + 1)
        with self.store._reading() as conn:
            file_rows = conn.execute(query, args).fetchall()
            directory_rows: list[sqlite3.Row] = []
            if scope_id != "all" and media_kind == "all":
                directory_query = "SELECT d.* FROM file_directories d WHERE d.scope_id=?"
                directory_args: list[Any] = [scope_id]
                if not include_deleted:
                    directory_query += " AND d.deleted_at IS NULL"
                if scoped_project is not None:
                    directory_query += " AND d.project_id=?"; directory_args.append(scoped_project)
                elif project_id is not None:
                    directory_query += " AND d.project_id=?"; directory_args.append(_opaque_id(project_id, "项目标识"))
                if parent_key:
                    child_prefix = parent_key + "/"
                    directory_query += " AND d.logical_path_key>=? AND d.logical_path_key<? AND instr(substr(d.logical_path_key,?),'/')=0"
                    directory_args.extend([child_prefix, parent_key + "0", len(child_prefix) + 1])
                else:
                    directory_query += " AND instr(d.logical_path_key,'/')=0"
                if decoded is not None:
                    directory_query += " AND (d.scope_id,d.logical_path_key,d.directory_id,0)>(?,?,?,?)"
                    directory_args.extend(decoded)
                directory_query += " ORDER BY d.scope_id,d.logical_path_key,d.directory_id LIMIT ?"
                directory_args.append(limit + 1)
                directory_rows = conn.execute(directory_query, directory_args).fetchall()

        items: list[tuple[tuple[str, str, str, int], dict[str, Any]]] = []
        for row in file_rows:
            payload = self._file_payload(row)
            key = (row["scope_id"], row["logical_path_key"], row["file_id"], 1)
            items.append((key, payload))
        for row in directory_rows:
            key = (row["scope_id"], row["logical_path_key"], row["directory_id"], 0)
            items.append((key, self._directory_payload(row)))

        items.sort(key=lambda item: item[0])
        page = items[:limit + 1]
        has_more = len(page) > limit
        page = page[:limit]
        next_cursor = self._cursor_encode(page[-1][0]) if has_more and page else None
        return {"items": [item[1] for item in page], "next_cursor": next_cursor}

    def file_history(self, actor: Any, file_id: str) -> dict[str, Any]:
        file_id = _opaque_id(file_id, "文件标识")
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if file_row is None:
                raise ApiFault(404, "NOT_FOUND", "文件不存在。")
            self._read_scope(actor, file_row["scope_id"], file_row["project_id"], allow_deleted_project=True)
            revisions = conn.execute("""SELECT revision_id,revision_no,size,sha256,media_type,
                actor_id,actor_name,source,operation_id,created_at FROM file_revisions
                WHERE file_id=? ORDER BY revision_no DESC""", (file_id,)).fetchall()
        return {"file_id": file_id, "current_revision_id": file_row["current_revision_id"],
                "items": [dict(row) for row in revisions]}

    def _ensure_directory(self, actor: Any, *, scope_id: Any, project_id: Any,
                          logical_path: Any) -> dict[str, Any]:
        actor_id, actor_name, user = _writable(actor)
        scope_id, scoped_project, owner_id = self._scope(actor, scope_id, project_id, write=True)
        path, key = _logical_path(logical_path)
        with self.store._transaction() as conn:
            self._ensure_parent_directories(conn, scope_id, scoped_project, owner_id,
                                            path, actor_id, actor_name)
            existing = conn.execute("""SELECT * FROM file_directories WHERE scope_id=? AND logical_path_key=?
                AND deleted_at IS NULL""", (scope_id, key)).fetchone()
            if existing:
                return self._directory_payload(existing)
            self._assert_path_free(conn, scope_id, key)
            directory_id = _uid()
            now = _now()
            conn.execute("""INSERT INTO file_directories
                (directory_id,scope_id,project_id,owner_user_id,logical_path,logical_path_key,
                 version,created_at,updated_at) VALUES(?,?,?,?,?,?,1,?,?)""",
                (directory_id, scope_id, scoped_project, owner_id, path, key, now, now))
            self._event(conn, "directory", directory_id, "create", scope_id=scope_id,
                project_id=scoped_project, logical_path=path, actor_id=actor_id, actor_name=actor_name)
            row = conn.execute("SELECT * FROM file_directories WHERE directory_id=?", (directory_id,)).fetchone()
        self.store._signal_change()
        return self._directory_payload(row)

    def create_directory(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        operation_id = _opaque_id(request.get("operation_id"))
        return self._ensure_directory(actor, scope_id=request.get("scope_id"),
            project_id=request.get("project_id"), logical_path=request.get("logical_path")) | {
                "operation_id": operation_id, "state": "committed"}

    def _op_request(self, operation_id: str, actor_id: str, request_json: str,
                    *, allow_committed: bool = True) -> sqlite3.Row | None:
        with self.store._reading() as conn:
            row = conn.execute("SELECT * FROM file_operations WHERE operation_id=?", (operation_id,)).fetchone()
        if row is None:
            return None
        if row["actor_id"] != actor_id or row["request_json"] != request_json:
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        if not allow_committed and row["state"] == "committed":
            raise ApiFault(409, "OPERATION_COMPLETE", "该操作已经完成。")
        return row

    def _upload_payload(self, row: sqlite3.Row, *, actor_id: str) -> dict[str, Any]:
        if row["state"] in {"committed", "aborted"} and row["result_json"]:
            return json.loads(row["result_json"])
        if row["state"] not in {"receiving", "staged"} or not row["revision_id"]:
            raise ApiFault(409, "OPERATION_NOT_RESUMABLE", "此上传操作无法继续。")
        base_url, _ = self._require_transfer_config()
        expires = int(time.time()) + TICKET_LIFETIME_SECONDS
        object_kind = _media_kind(row["media_type"], row["logical_path"])
        claims = {
            "v": 1,
            "op_id": row["operation_id"],
            "user_id": actor_id,
            "action": "upload",
            "direction": "upload",
            "object_id": row["revision_id"],
            "object_kind": object_kind,
            "expected_version": int(row["expected_version"] or 0),
            "size": int(row["size"]),
            "sha256": row["sha256"],
            "exp": expires,
            "nonce": _uid(),
        }
        return {
            "operation_id": row["operation_id"],
            "state": row["state"],
            "file_id": row["file_id"],
            "revision_id": row["revision_id"],
            "object_id": row["revision_id"],
            "object_kind": object_kind,
            "expected_version": row["expected_version"],
            "ticket": self._ticket(claims),
            "transfer_url": base_url + "/transfer/v1/uploads",
            "expires_at": datetime.fromtimestamp(expires, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        }

    def begin_upload(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        self._require_transfer_config()
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(request.get("operation_id"))
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        source = request.get("source")
        if source not in {"wpf", "sync"}:
            raise ApiFault(422, "INVALID_SOURCE", "文件来源无效。")
        scope_id, scoped_project, owner_id = self._scope(actor, request.get("scope_id"),
                                                        request.get("project_id"), write=True)
        path, path_key = _logical_path(request.get("logical_path"))
        file_id = _opaque_id(request.get("file_id"), "文件标识") if request.get("file_id") is not None else None
        expected_version = _positive_int(request.get("expected_version"), "文件版本", allow_zero=True)
        size = _positive_int(request.get("size"), "文件大小", allow_zero=True)
        digest = request.get("sha256")
        if not isinstance(digest, str) or not SHA256_RE.fullmatch(digest):
            raise ApiFault(422, "INVALID_HASH", "SHA-256 校验值无效。")
        media_type = _normalize_mime(request.get("media_type"), path)
        kind = _media_kind(media_type, path)
        canonical_request = {
            "operation_id": operation_id, "action": "upload", "client_id": client_id,
            "scope_id": scope_id, "project_id": scoped_project, "logical_path": path,
            "file_id": file_id, "expected_version": expected_version, "size": size,
            "sha256": digest, "media_type": media_type, "source": source,
        }
        request_json = _canonical(canonical_request).decode("utf-8")
        previous = self._op_request(operation_id, actor_id, request_json)
        if previous is not None:
            return self._upload_payload(previous, actor_id=actor_id)

        revision_id = _uid()
        now = _now()
        with self.store._transaction() as conn:
            # Recheck idempotency after acquiring the write lock.
            existing_op = conn.execute("SELECT * FROM file_operations WHERE operation_id=?", (operation_id,)).fetchone()
            if existing_op is not None:
                if existing_op["actor_id"] != actor_id or existing_op["request_json"] != request_json:
                    raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
                result_row = existing_op
            else:
                path_row = conn.execute("""SELECT * FROM files WHERE scope_id=? AND logical_path_key=?
                    AND deleted_at IS NULL""", (scope_id, path_key)).fetchone()
                if file_id is None and path_row is not None:
                    file_id = path_row["file_id"]
                elif file_id is not None and (path_row is None or path_row["file_id"] != file_id):
                    raise ApiFault(409, "FILE_PATH_MISMATCH", "文件标识与逻辑路径不一致。")
                self._assert_path_free(conn, scope_id, path_key, except_file_id=file_id)
                if path_row is None:
                    if expected_version != 0:
                        raise ApiFault(409, "CONFLICT", "文件不存在或版本已变化，请刷新后重试。")
                    file_id = file_id or _uid()
                    conn.execute("""INSERT INTO files
                        (file_id,scope_id,project_id,owner_user_id,logical_path,logical_path_key,
                         media_kind,media_type,current_revision_id,version,created_at,updated_at)
                        VALUES(?,?,?,?,?,?,?,?,NULL,0,?,?)""",
                        (file_id, scope_id, scoped_project, owner_id, path, path_key, kind, media_type, now, now))
                else:
                    if int(path_row["version"]) != expected_version:
                        raise ApiFault(409, "CONFLICT", "文件已被其他人修改，请刷新后重试。")
                    if path_row["current_revision_id"] is None:
                        raise ApiFault(409, "UPLOAD_IN_PROGRESS", "该文件已有上传正在进行。")
                self._ensure_parent_directories(conn, scope_id, scoped_project, owner_id,
                                                path, actor_id, actor_name)
                conn.execute("""INSERT INTO file_operations
                    (operation_id,action,actor_id,actor_name,client_id,scope_id,project_id,file_id,
                     revision_id,logical_path,expected_version,size,sha256,media_type,source,state,
                     request_json,created_at,updated_at)
                    VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,'receiving',?,?,?)""",
                    (operation_id, "upload", actor_id, actor_name, client_id, scope_id, scoped_project,
                     file_id, revision_id, path, expected_version, size, digest, media_type, source,
                     request_json, now, now))
                result_row = conn.execute("SELECT * FROM file_operations WHERE operation_id=?",
                                         (operation_id,)).fetchone()
        if result_row is None:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "上传操作状态暂不可用。")
        return self._upload_payload(result_row, actor_id=actor_id)

    def abort_upload(self, actor: Any, operation_id: str,
                     request: Mapping[str, Any]) -> dict[str, Any]:
        """Safely stop a pending upload without changing an existing revision."""
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(operation_id)
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        expected_state = request.get("expected_state")
        if expected_state not in {"receiving", "staged"}:
            raise ApiFault(422, "INVALID_OPERATION_STATE", "上传操作状态无效。")
        now = _now()
        with self.store._transaction() as conn:
            operation = conn.execute("SELECT * FROM file_operations WHERE operation_id=?",
                                     (operation_id,)).fetchone()
            if operation is None or operation["action"] != "upload":
                raise ApiFault(404, "NOT_FOUND", "上传操作不存在。")
            if operation["actor_id"] != actor_id:
                raise ApiFault(403, "FORBIDDEN", "上传操作不属于当前账户。")
            if operation["client_id"] != client_id:
                raise ApiFault(409, "CLIENT_MISMATCH", "上传操作属于另一个客户端。")
            if operation["state"] == "aborted" and operation["result_json"]:
                return json.loads(operation["result_json"])
            if operation["state"] != expected_state:
                raise ApiFault(409, "OPERATION_STATE_CHANGED", "上传状态已变化，请刷新任务状态后重试。")

            retired_placeholder = False
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?",
                                     (operation["file_id"],)).fetchone()
            if (file_row is not None and file_row["deleted_at"] is None
                    and file_row["current_revision_id"] is None
                    and int(file_row["version"]) == int(operation["expected_version"])
                    and int(operation["expected_version"]) == 0):
                has_revision = conn.execute("SELECT 1 FROM file_revisions WHERE file_id=? LIMIT 1",
                                            (operation["file_id"],)).fetchone()
                if has_revision is None:
                    conn.execute("""UPDATE files SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                        version=version+1,updated_at=? WHERE file_id=? AND deleted_at IS NULL""",
                        (now, actor_name, operation_id, now, operation["file_id"]))
                    retired_placeholder = True

            result = {"operation_id": operation_id, "state": "aborted",
                      "file_id": operation["file_id"],
                      "placeholder_retired": retired_placeholder}
            conn.execute("""UPDATE file_operations SET state='aborted',error_code='ABORTED',
                result_json=?,updated_at=? WHERE operation_id=?""",
                (_canonical(result).decode("utf-8"), now, operation_id))
        self.store._signal_change()
        return result

    def begin_upload_batch(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        _writable(actor)
        items = request.get("items")
        if not isinstance(items, list) or not items or len(items) > MAX_BATCH_ITEMS:
            raise ApiFault(422, "INVALID_BATCH", "上传批次项目数无效。")
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        source = request.get("source")
        if source not in {"wpf", "sync"}:
            raise ApiFault(422, "INVALID_SOURCE", "文件来源无效。")
        directories = request.get("directories", [])
        if not isinstance(directories, list) or len(directories) > MAX_BATCH_DIRECTORIES:
            raise ApiFault(422, "INVALID_BATCH", "目录清单过大。")
        for directory in directories:
            if not isinstance(directory, dict):
                raise ApiFault(422, "INVALID_BATCH", "目录清单无效。")
            self._ensure_directory(actor, scope_id=directory.get("scope_id"),
                project_id=directory.get("project_id"), logical_path=directory.get("logical_path"))
        results: list[dict[str, Any]] = []
        errors: list[dict[str, str]] = []
        for item in items:
            if not isinstance(item, dict):
                errors.append({"operation_id": "", "code": "INVALID_REQUEST", "message": "上传项目无效。"})
                continue
            payload = dict(item)
            payload.setdefault("action", "upload")
            payload.setdefault("client_id", client_id)
            payload.setdefault("source", source)
            try:
                results.append(self.begin_upload(actor, payload))
            except ApiFault as error:
                errors.append({"operation_id": str(item.get("operation_id") or ""),
                               "code": error.code, "message": error.message})
        return {"items": results, "errors": errors}

    @staticmethod
    def _insert_committed_operation(conn: sqlite3.Connection, *, operation_id: str, action: str,
                                    actor_id: str, actor_name: str, client_id: str,
                                    file_id: str | None, scope_id: str | None,
                                    project_id: str | None, logical_path: str | None,
                                    expected_version: int | None, source: str | None,
                                    request_json: str, result: dict[str, Any],
                                    revision_id: str | None = None,
                                    target_scope_id: str | None = None,
                                    target_project_id: str | None = None,
                                    target_logical_path: str | None = None) -> None:
        now = _now()
        conn.execute("""INSERT INTO file_operations
            (operation_id,action,actor_id,actor_name,client_id,scope_id,project_id,file_id,revision_id,
             logical_path,target_scope_id,target_project_id,target_logical_path,expected_version,source,
             state,request_json,result_json,created_at,updated_at)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,'committed',?,?,?,?)""",
            (operation_id, action, actor_id, actor_name, client_id, scope_id, project_id, file_id,
             revision_id, logical_path, target_scope_id, target_project_id, target_logical_path,
             expected_version, source, request_json, _canonical(result).decode("utf-8"), now, now))

    def _previous_committed_result(self, operation_id: str, actor_id: str,
                                   request_json: str) -> dict[str, Any] | None:
        row = self._op_request(operation_id, actor_id, request_json)
        if row is None:
            return None
        if row["state"] == "committed" and row["result_json"]:
            return json.loads(row["result_json"])
        raise ApiFault(409, "OPERATION_IN_PROGRESS", "该操作尚未完成。")

    @staticmethod
    def _operation_result_tx(conn: sqlite3.Connection, operation_id: str, actor_id: str,
                             request_json: str) -> dict[str, Any] | None:
        """Recheck retries after acquiring SQLite's write lock."""
        row = conn.execute("SELECT * FROM file_operations WHERE operation_id=?",
                           (operation_id,)).fetchone()
        if row is None:
            return None
        if row["actor_id"] != actor_id or row["request_json"] != request_json:
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        if row["state"] == "committed" and row["result_json"]:
            return json.loads(row["result_json"])
        raise ApiFault(409, "OPERATION_IN_PROGRESS", "该操作尚未完成。")

    def _replay_move(self, actor_id: str, request: Mapping[str, Any], operation_id: str,
                     client_id: str, file_id: str, expected_version: int,
                     source: str) -> dict[str, Any] | None:
        """Recognize an already committed move before checking its now-stale source path."""
        with self.store._reading() as conn:
            row = conn.execute("SELECT * FROM file_operations WHERE operation_id=?",
                               (operation_id,)).fetchone()
        if row is None:
            return None
        if row["actor_id"] != actor_id or row["action"] != "move":
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        stored = json.loads(row["request_json"])
        base = {"operation_id": operation_id, "action": "move", "client_id": client_id,
                "file_id": file_id, "expected_version": expected_version, "source": source}
        if any(stored.get(key) != value for key, value in base.items()):
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        supplied_scope = request.get("target_scope_id", request.get("scope_id"))
        supplied_project = request.get("target_project_id", request.get("project_id"))
        supplied_path = request.get("target_logical_path", request.get("logical_path"))
        try:
            target_path = _logical_path(supplied_path)[0] if supplied_path is not None else row["target_logical_path"]
        except ApiFault:
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。") from None
        if ((supplied_scope is not None and supplied_scope != row["target_scope_id"])
                or (supplied_project is not None and supplied_project != row["target_project_id"])
                or target_path != row["target_logical_path"]):
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        if row["state"] == "committed" and row["result_json"]:
            return json.loads(row["result_json"])
        raise ApiFault(409, "OPERATION_IN_PROGRESS", "该操作尚未完成。")

    def _replay_directory_operation(self, actor_id: str, request: Mapping[str, Any],
                                    operation_id: str, client_id: str,
                                    directory_id: str, action: str,
                                    expected_version: int) -> dict[str, Any] | None:
        with self.store._reading() as conn:
            row = conn.execute("SELECT * FROM file_operations WHERE operation_id=?",
                               (operation_id,)).fetchone()
        if row is None:
            return None
        action_name = "directory_" + action
        if row["actor_id"] != actor_id or row["action"] != action_name:
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        stored = json.loads(row["request_json"])
        base = {"operation_id": operation_id, "action": action_name, "client_id": client_id,
                "directory_id": directory_id, "expected_version": expected_version}
        if any(stored.get(key) != value for key, value in base.items()):
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        supplied_scope = request.get("target_scope_id")
        supplied_project = request.get("target_project_id")
        supplied_path = request.get("target_logical_path")
        try:
            target_path = _logical_path(supplied_path)[0] if supplied_path is not None else row["target_logical_path"]
        except ApiFault:
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。") from None
        if ((supplied_scope is not None and supplied_scope != row["target_scope_id"])
                or (supplied_project is not None and supplied_project != row["target_project_id"])
                or target_path != row["target_logical_path"]):
            raise ApiFault(409, "OPERATION_ID_REUSED", "操作标识已用于另一项请求。")
        if row["state"] == "committed" and row["result_json"]:
            return json.loads(row["result_json"])
        raise ApiFault(409, "OPERATION_IN_PROGRESS", "该操作尚未完成。")

    def delete_file(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(request.get("operation_id"))
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        file_id = _opaque_id(request.get("file_id"), "文件标识")
        expected_version = _positive_int(request.get("expected_version"), "文件版本")
        source = request.get("source")
        if source not in {"wpf", "sync"}:
            raise ApiFault(422, "INVALID_SOURCE", "文件来源无效。")
        request_json = _canonical({"operation_id": operation_id, "action": "delete", "client_id": client_id,
            "file_id": file_id, "expected_version": expected_version, "source": source}).decode("utf-8")
        previous = self._previous_committed_result(operation_id, actor_id, request_json)
        if previous is not None:
            return previous
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
        if file_row is None:
            raise ApiFault(404, "NOT_FOUND", "文件不存在。")
        if ((request.get("scope_id") is not None and request.get("scope_id") != file_row["scope_id"])
                or (request.get("project_id") is not None and request.get("project_id") != file_row["project_id"])
                or (request.get("logical_path") is not None and request.get("logical_path") != file_row["logical_path"])):
            raise ApiFault(409, "CONFLICT", "文件路径已变化，请刷新同步状态。")
        scope_id, project_id, _owner = self._scope(actor, file_row["scope_id"], file_row["project_id"], write=True)
        path = file_row["logical_path"]
        now = _now()
        with self.store._transaction() as conn:
            previous = self._operation_result_tx(conn, operation_id, actor_id, request_json)
            if previous is not None:
                return previous
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if file_row is None or file_row["deleted_at"] is not None:
                raise ApiFault(404, "NOT_FOUND", "活动文件不存在。")
            if file_row["current_revision_id"] is None:
                raise ApiFault(409, "UPLOAD_IN_PROGRESS", "文件仍在上传，暂时不能删除。")
            if int(file_row["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "文件已被其他人修改，请刷新后重试。")
            next_version = expected_version + 1
            conn.execute("""UPDATE files SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                version=?,updated_at=? WHERE file_id=?""",
                (now, actor_name, operation_id, next_version, now, file_id))
            result = {"operation_id": operation_id, "state": "committed", "file_id": file_id,
                      "version": next_version}
            sequence = self._event(conn, "file", file_id, "delete", file_id=file_id,
                scope_id=scope_id, project_id=project_id, logical_path=path,
                revision_id=file_row["current_revision_id"], version=next_version,
                source=source, actor_id=actor_id, actor_name=actor_name)
            result["change_sequence"] = sequence
            self._insert_committed_operation(conn, operation_id=operation_id, action="delete",
                actor_id=actor_id, actor_name=actor_name, client_id=client_id, file_id=file_id,
                scope_id=scope_id, project_id=project_id, logical_path=path,
                expected_version=expected_version, source=source, request_json=request_json,
                result=result, revision_id=file_row["current_revision_id"])
            if file_row["media_kind"] == "image" and project_id:
                conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                             ("__deleted__/" + file_id, now, file_id))
        self.store._signal_change()
        return result

    def move_file(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(request.get("operation_id"))
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        file_id = _opaque_id(request.get("file_id"), "文件标识")
        expected_version = _positive_int(request.get("expected_version"), "文件版本")
        source = request.get("source")
        if source not in {"wpf", "sync"}:
            raise ApiFault(422, "INVALID_SOURCE", "文件来源无效。")
        replay = self._replay_move(actor_id, request, operation_id, client_id,
                                   file_id, expected_version, source)
        if replay is not None:
            return replay
        with self.store._reading() as conn:
            old = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
        if old is None:
            raise ApiFault(404, "NOT_FOUND", "文件不存在。")
        old_scope, old_project, _ = self._scope(actor, old["scope_id"], old["project_id"], write=True)
        if ((request.get("scope_id") is not None and request.get("scope_id") != old_scope)
                or (request.get("project_id") is not None and request.get("project_id") != old_project)
                or (request.get("previous_logical_path") is not None
                    and request.get("previous_logical_path") != old["logical_path"])):
            raise ApiFault(409, "CONFLICT", "文件路径已变化，请刷新同步状态。")
        raw_target_scope = request.get("target_scope_id", request.get("scope_id", old_scope))
        raw_target_project = request.get("target_project_id", request.get("project_id", old_project))
        raw_target_path = request.get("target_logical_path", request.get("logical_path"))
        target_scope, target_project, target_owner = self._scope(actor, raw_target_scope,
            raw_target_project, write=True)
        target_path, target_key = _logical_path(raw_target_path)
        request_json = _canonical({"operation_id": operation_id, "action": "move", "client_id": client_id,
            "file_id": file_id, "expected_version": expected_version, "source": source,
            "target_scope_id": target_scope, "target_project_id": target_project,
            "target_logical_path": target_path}).decode("utf-8")
        previous = self._previous_committed_result(operation_id, actor_id, request_json)
        if previous is not None:
            return previous
        now = _now()
        with self.store._transaction() as conn:
            previous = self._operation_result_tx(conn, operation_id, actor_id, request_json)
            if previous is not None:
                return previous
            current = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if current is None or current["deleted_at"] is not None or current["current_revision_id"] is None:
                raise ApiFault(404, "NOT_FOUND", "活动文件不存在。")
            if int(current["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "文件已被其他人修改，请刷新后重试。")
            self._assert_path_free(conn, target_scope, target_key, except_file_id=file_id)
            self._ensure_parent_directories(conn, target_scope, target_project, target_owner,
                                            target_path, actor_id, actor_name)
            next_version = expected_version + 1
            conn.execute("""UPDATE files SET scope_id=?,project_id=?,owner_user_id=?,logical_path=?,
                logical_path_key=?,version=?,updated_at=? WHERE file_id=?""",
                (target_scope, target_project, target_owner, target_path, target_key,
                 next_version, now, file_id))
            result = {"operation_id": operation_id, "state": "committed", "file_id": file_id,
                      "version": next_version}
            sequence = self._event(conn, "file", file_id, "move", file_id=file_id,
                scope_id=target_scope, project_id=target_project, logical_path=target_path,
                previous_scope_id=old_scope, previous_project_id=old_project,
                previous_logical_path=old["logical_path"], revision_id=current["current_revision_id"],
                version=next_version, source=source, actor_id=actor_id, actor_name=actor_name)
            result["change_sequence"] = sequence
            self._insert_committed_operation(conn, operation_id=operation_id, action="move",
                actor_id=actor_id, actor_name=actor_name, client_id=client_id, file_id=file_id,
                scope_id=old_scope, project_id=old_project, logical_path=old["logical_path"],
                target_scope_id=target_scope, target_project_id=target_project,
                target_logical_path=target_path, expected_version=expected_version,
                source=source, request_json=request_json, result=result,
                revision_id=current["current_revision_id"])
            if current["media_kind"] == "image":
                if target_project:
                    self._sync_image_asset_tx(conn, file_id, target_project, target_path,
                                              current["media_type"], current["version"], now)
                else:
                    conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                                 ("__deleted__/" + file_id, now, file_id))
            self._sync_linked_video_paths_tx(conn, {
                file_id: (target_path, target_project)
            }, now)
        self.store._signal_change()
        return result

    @staticmethod
    def _file_has_workbook_video_link_tx(conn: sqlite3.Connection, file_id: str) -> bool:
        for row in conn.execute("SELECT data FROM workbook_rows WHERE kind='scene_beats'"):
            try:
                data = json.loads(row["data"])
            except (TypeError, json.JSONDecodeError):
                continue
            if not isinstance(data, dict):
                continue
            for field in ("video", "videos", "discarded_videos"):
                values = data.get(field)
                if not isinstance(values, list):
                    values = [values]
                if any(isinstance(value, dict) and
                       (value.get("id") == file_id or value.get("file_id") == file_id)
                       for value in values):
                    return True
        return False

    @staticmethod
    def _sync_image_asset_tx(conn: sqlite3.Connection, file_id: str, project_id: str,
                             logical_path: str, media_type: str, version: int, now: str) -> None:
        parts = logical_path.split("/")
        parent = "/".join(parts[:-1])
        folder_id = None
        if parent:
            folder = conn.execute("SELECT id FROM asset_folders WHERE project_id=? AND relative_path=?",
                                  (project_id, parent)).fetchone()
            if folder is None:
                folder_id = _uid()
                conn.execute("INSERT INTO asset_folders(id,project_id,relative_path) VALUES(?,?,?)",
                             (folder_id, project_id, parent))
            else:
                folder_id = folder["id"]
        filename = parts[-1]
        collisions = conn.execute("""SELECT id FROM image_assets WHERE project_id=? AND relative_path=?
            AND id<>?""", (project_id, logical_path, file_id)).fetchall()
        for collision in collisions:
            existing_file = conn.execute("SELECT current_revision_id,deleted_at FROM files WHERE file_id=?",
                                          (collision["id"],)).fetchone()
            if existing_file is not None and existing_file["deleted_at"] is None:
                raise ApiFault(409, "PATH_OCCUPIED", "已有活动图片占用此逻辑路径。")
            conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                         ("__deleted__/" + collision["id"], now, collision["id"]))
        conn.execute("""INSERT INTO image_assets
            (id,project_id,folder_id,relative_path,file_name,display_name,media_type,active,version,updated_at)
            VALUES(?,?,?,?,?,?,?,1,?,?)
            ON CONFLICT(id) DO UPDATE SET project_id=excluded.project_id,folder_id=excluded.folder_id,
              relative_path=excluded.relative_path,file_name=excluded.file_name,
              display_name=excluded.display_name,media_type=excluded.media_type,
              active=1,version=excluded.version,updated_at=excluded.updated_at""",
            (file_id, project_id, folder_id, logical_path, filename, filename.rsplit(".", 1)[0],
             media_type, version, now))

    def _sync_linked_video_paths_tx(self, conn: sqlite3.Connection,
                                    moved_files: Mapping[str, tuple[str, str | None]], now: str) -> None:
        """Keep workbook video links in the same SQLite transaction as logical file moves."""
        if not moved_files:
            return
        rows = conn.execute("""SELECT r.id,r.data,r.field_versions,r.version,
                s.workbook_id,w.project_id
            FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
            JOIN workbooks w ON w.id=s.workbook_id
            WHERE r.kind='scene_beats'""").fetchall()
        for row in rows:
            data = _json_object(row["data"])
            versions = _json_object(row["field_versions"])
            changed_fields: set[str] = set()
            for field in ("videos", "discarded_videos"):
                values = data.get(field)
                if not isinstance(values, list):
                    continue
                updated = []
                for item in values:
                    if not isinstance(item, dict) or item.get("file_id") not in moved_files:
                        updated.append(item)
                        continue
                    file_id = item["file_id"]
                    if item.get("id") != file_id:
                        raise ApiFault(409, "VIDEO_FILE_MISMATCH", "视频关联的文件标识无效，不能移动文件。")
                    target_path, target_project = moved_files[file_id]
                    if row["project_id"] != target_project:
                        raise ApiFault(409, "VIDEO_PROJECT_MOVE", "该视频仍关联节拍表，请先在节拍表中解除关联后再移出项目。")
                    replacement = dict(item)
                    target_name = target_path.rsplit("/", 1)[-1]
                    replacement["path"] = target_path
                    replacement["name"] = target_name
                    updated.append(replacement)
                    if item.get("path") != target_path or item.get("name") != target_name:
                        changed_fields.add(field)
                data[field] = updated
            current = data.get("video")
            if isinstance(current, dict) and current.get("file_id") in moved_files:
                file_id = current["file_id"]
                if current.get("id") != file_id:
                    raise ApiFault(409, "VIDEO_FILE_MISMATCH", "视频关联的文件标识无效，不能移动文件。")
                target_path, target_project = moved_files[file_id]
                if row["project_id"] != target_project:
                    raise ApiFault(409, "VIDEO_PROJECT_MOVE", "该视频仍关联节拍表，请先在节拍表中解除关联后再移出项目。")
                target_name = target_path.rsplit("/", 1)[-1]
                if current.get("path") != target_path or current.get("name") != target_name:
                    current = dict(current)
                    current["path"] = target_path
                    current["name"] = target_name
                    data["video"] = current
                    changed_fields.add("video")
            if not changed_fields:
                continue
            for field in changed_fields:
                versions[field] = int(versions.get(field, 1)) + 1
            row_version = int(row["version"]) + 1
            conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=? WHERE id=?",
                (_canonical(data).decode("utf-8"), _canonical(versions).decode("utf-8"),
                 row_version, row["id"]))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?",
                         (now, row["workbook_id"]))
            self.store._event_tx(conn, row["project_id"], row["workbook_id"],
                "row", row["id"], sorted(changed_fields))

    def _verify_receipt(self, receipt: Any) -> dict[str, Any]:
        if not isinstance(receipt, dict) or set(receipt) != {
                "v", "op_id", "object_id", "size", "sha256", "staged_at", "nonce", "mac"}:
            raise ApiFault(422, "INVALID_RECEIPT", "上传回执格式无效。")
        value = dict(receipt)
        signature = value.pop("mac")
        if (value.get("v") != 1 or type(value.get("size")) is not int
                or value["size"] < 0 or not isinstance(value.get("sha256"), str)
                or not SHA256_RE.fullmatch(value["sha256"])
                or not isinstance(value.get("staged_at"), str)
                or not isinstance(value.get("nonce"), str)):
            raise ApiFault(422, "INVALID_RECEIPT", "上传回执格式无效。")
        try:
            datetime.fromisoformat(value["staged_at"].replace("Z", "+00:00"))
            _opaque_id(value["op_id"])
            _opaque_id(value["object_id"])
            _opaque_id(value["nonce"])
            supplied = _unb64url(signature)
        except (ValueError, TypeError, AttributeError):
            raise ApiFault(422, "INVALID_RECEIPT", "上传回执格式无效。") from None
        _, secret = self._require_transfer_config()
        expected = hmac.new(secret, _canonical(value), hashlib.sha256).digest()
        if not hmac.compare_digest(supplied, expected):
            raise ApiFault(403, "RECEIPT_INVALID", "上传回执签名无效。")
        return value

    def commit_upload(self, actor: Any, operation_id: str, receipt: Any) -> dict[str, Any]:
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(operation_id)
        verified = self._verify_receipt(receipt)
        if verified["op_id"] != operation_id:
            raise ApiFault(422, "RECEIPT_MISMATCH", "上传回执与操作标识不一致。")
        conflict = False
        result: dict[str, Any] | None = None
        with self.store._transaction() as conn:
            operation = conn.execute("SELECT * FROM file_operations WHERE operation_id=?", (operation_id,)).fetchone()
            if operation is None or operation["action"] != "upload":
                raise ApiFault(404, "NOT_FOUND", "上传操作不存在。")
            if operation["actor_id"] != actor_id:
                raise ApiFault(403, "FORBIDDEN", "上传操作不属于当前账户。")
            if operation["state"] == "committed" and operation["result_json"]:
                return json.loads(operation["result_json"])
            if operation["state"] not in {"receiving", "staged"}:
                raise ApiFault(409, "OPERATION_NOT_RESUMABLE", "上传操作已结束，不能提交。")
            if (verified["object_id"] != operation["revision_id"]
                    or verified["size"] != int(operation["size"])
                    or verified["sha256"] != operation["sha256"]):
                raise ApiFault(422, "RECEIPT_MISMATCH", "上传回执与文件清单不一致。")
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (operation["file_id"],)).fetchone()
            if file_row is None or file_row["deleted_at"] is not None:
                conflict = True
            else:
                project = conn.execute("SELECT deleted_at FROM projects WHERE id=?", (file_row["project_id"],)).fetchone() if file_row["project_id"] else None
                if project is not None and project["deleted_at"] is not None:
                    conflict = True
            if not conflict and int(file_row["version"]) != int(operation["expected_version"]):
                conflict = True
            if conflict:
                conn.execute("UPDATE file_operations SET state='failed',error_code='CONFLICT',updated_at=? WHERE operation_id=?",
                             (_now(), operation_id))
            else:
                next_version = int(operation["expected_version"]) + 1
                revision_no = conn.execute("SELECT COALESCE(MAX(revision_no),0)+1 AS n FROM file_revisions WHERE file_id=?",
                                           (operation["file_id"],)).fetchone()["n"]
                kind = _media_kind(operation["media_type"], operation["logical_path"])
                if kind != "video" and operation["project_id"] and self._file_has_workbook_video_link_tx(
                        conn, operation["file_id"]):
                    raise ApiFault(409, "VIDEO_FILE_TYPE_MISMATCH",
                                   "此文件仍关联在节拍表中，不能改成非视频类型。")
                revision_id = operation["revision_id"]
                compact_id = revision_id.replace("-", "")
                object_key = f"_objects/{kind}/{compact_id[:2]}/{compact_id}"
                created_at = verified["staged_at"]
                conn.execute("""INSERT INTO file_revisions
                    (revision_id,file_id,revision_no,storage_key,sha256,size,media_type,actor_id,
                     actor_name,source,operation_id,created_at)
                    VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                    (revision_id, operation["file_id"], revision_no, object_key, operation["sha256"],
                     operation["size"], operation["media_type"], actor_id, actor_name,
                     operation["source"], operation_id, created_at))
                conn.execute("""UPDATE files SET current_revision_id=?,media_kind=?,media_type=?,version=?,updated_at=?
                    WHERE file_id=?""", (revision_id, kind, operation["media_type"], next_version,
                    _now(), operation["file_id"]))
                sequence = self._event(conn, "file", operation["file_id"], "upload",
                    file_id=operation["file_id"], scope_id=operation["scope_id"],
                    project_id=operation["project_id"], logical_path=operation["logical_path"],
                    revision_id=revision_id, version=next_version, source=operation["source"],
                    actor_id=actor_id, actor_name=actor_name)
                result = {"operation_id": operation_id, "state": "committed",
                          "file_id": operation["file_id"], "revision_id": revision_id,
                          "version": next_version, "change_sequence": sequence}
                conn.execute("UPDATE file_operations SET state='committed',result_json=?,updated_at=? WHERE operation_id=?",
                             (_canonical(result).decode("utf-8"), _now(), operation_id))
                if kind == "image" and operation["project_id"]:
                    self._sync_image_asset_tx(conn, operation["file_id"], operation["project_id"],
                        operation["logical_path"], operation["media_type"], next_version, _now())
                elif file_row["media_kind"] == "image" and file_row["project_id"]:
                    conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                        ("__deleted__/" + operation["file_id"], _now(), operation["file_id"]))
        self.store._signal_change()
        if conflict:
            raise ApiFault(409, "CONFLICT", "文件已被删除或更新，请刷新后重新提交。")
        if result is None:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "上传版本暂时无法提交。")
        return result

    def restore_file(self, actor: Any, file_id: str, request: Mapping[str, Any]) -> dict[str, Any]:
        actor_id, actor_name, _user = _writable(actor)
        operation_id = _opaque_id(request.get("operation_id"))
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        revision_id = _opaque_id(request.get("revision_id"), "版本标识")
        expected_version = _positive_int(request.get("expected_version"), "文件版本")
        source = request.get("source")
        if source not in {"wpf", "sync"}:
            raise ApiFault(422, "INVALID_SOURCE", "文件来源无效。")
        file_id = _opaque_id(file_id, "文件标识")
        request_json = _canonical({"operation_id": operation_id, "action": "restore",
            "file_id": file_id, "revision_id": revision_id, "expected_version": expected_version,
            "client_id": client_id, "source": source}).decode("utf-8")
        previous = self._previous_committed_result(operation_id, actor_id, request_json)
        if previous is not None:
            return previous
        now = _now()
        with self.store._transaction() as conn:
            previous = self._operation_result_tx(conn, operation_id, actor_id, request_json)
            if previous is not None:
                return previous
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if file_row is None:
                raise ApiFault(404, "NOT_FOUND", "文件不存在。")
            scope_id, project_id, _owner = self._scope(actor, file_row["scope_id"],
                file_row["project_id"], write=True)
            revision = conn.execute("SELECT * FROM file_revisions WHERE revision_id=? AND file_id=?",
                                    (revision_id, file_id)).fetchone()
            if revision is None:
                raise ApiFault(404, "NOT_FOUND", "文件版本不存在。")
            if int(file_row["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "文件已被其他人修改，请刷新后重试。")
            self._assert_path_free(conn, scope_id, file_row["logical_path_key"], except_file_id=file_id)
            self._assert_parent_directories_active(conn, scope_id, file_row["logical_path"])
            next_version = expected_version + 1
            restored_kind = _media_kind(revision["media_type"], file_row["logical_path"])
            if restored_kind != "video" and project_id and self._file_has_workbook_video_link_tx(conn, file_id):
                raise ApiFault(409, "VIDEO_FILE_TYPE_MISMATCH",
                               "此文件仍关联在节拍表中，不能恢复为非视频类型。")
            conn.execute("""UPDATE files SET current_revision_id=?,media_kind=?,media_type=?,
                deleted_at=NULL,deleted_by=NULL,deleted_operation_id=NULL,version=?,updated_at=? WHERE file_id=?""",
                (revision_id, restored_kind, revision["media_type"], next_version, now, file_id))
            result = {"operation_id": operation_id, "state": "committed", "file_id": file_id,
                      "revision_id": revision_id, "version": next_version}
            sequence = self._event(conn, "file", file_id, "restore", file_id=file_id,
                scope_id=scope_id, project_id=project_id, logical_path=file_row["logical_path"],
                revision_id=revision_id, version=next_version, source=source,
                actor_id=actor_id, actor_name=actor_name)
            result["change_sequence"] = sequence
            self._insert_committed_operation(conn, operation_id=operation_id, action="restore",
                actor_id=actor_id, actor_name=actor_name, client_id=client_id, file_id=file_id,
                scope_id=scope_id, project_id=project_id, logical_path=file_row["logical_path"],
                expected_version=expected_version, source=source, request_json=request_json,
                result=result, revision_id=revision_id)
            if restored_kind == "image" and project_id:
                self._sync_image_asset_tx(conn, file_id, project_id, file_row["logical_path"],
                    revision["media_type"], next_version, now)
            elif file_row["media_kind"] == "image" and project_id:
                conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                    ("__deleted__/" + file_id, now, file_id))
        self.store._signal_change()
        return result

    def download_ticket(self, actor: Any, file_id: str, request: Mapping[str, Any]) -> dict[str, Any]:
        base_url, _ = self._require_transfer_config()
        actor_id, _actor_name, _user = _actor(actor)
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        file_id = _opaque_id(file_id, "文件标识")
        requested_revision = request.get("revision_id")
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if file_row is None:
                raise ApiFault(404, "NOT_FOUND", "文件不存在。")
            self._read_scope(actor, file_row["scope_id"], file_row["project_id"], allow_deleted_project=True)
            revision_id = _opaque_id(requested_revision, "版本标识") if requested_revision is not None else file_row["current_revision_id"]
            if revision_id is None:
                raise ApiFault(404, "NOT_FOUND", "文件内容尚未提交。")
            revision = conn.execute("SELECT * FROM file_revisions WHERE file_id=? AND revision_id=?",
                                    (file_id, revision_id)).fetchone()
            if revision is None:
                raise ApiFault(404, "NOT_FOUND", "文件版本不存在。")
            generation = int(file_row["version"])
        expires = int(time.time()) + TICKET_LIFETIME_SECONDS
        operation_id = _uid()
        object_kind = self._revision_object_kind(revision["storage_key"])
        claims = {"v": 1, "op_id": operation_id, "user_id": actor_id,
            "action": "download", "direction": "download", "object_id": revision_id,
            "object_kind": object_kind,
            "expected_version": generation, "size": int(revision["size"]),
            "sha256": revision["sha256"], "exp": expires, "nonce": _uid()}
        return {"file_id": file_id, "revision_id": revision_id, "object_id": revision_id,
            "object_kind": object_kind,
            "size": int(revision["size"]), "sha256": revision["sha256"],
            "ticket": self._ticket(claims), "transfer_url": base_url + "/transfer/v1/downloads",
            "expires_at": datetime.fromtimestamp(expires, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")}

    @staticmethod
    def _revision_object_kind(storage_key: str) -> str:
        parts = storage_key.split("/")
        if len(parts) == 4 and parts[0] == "_objects" and parts[1] in {"image", "video", "other", "thumbnail"}:
            return parts[1]
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "文件版本的对象类型记录无效。")

    @staticmethod
    def _thumbnail_object_id(file_id: str, revision_id: str) -> str:
        key = f"{file_id}:{revision_id}:{THUMBNAIL_GENERATOR_VERSION}"
        return str(uuid.uuid5(uuid.NAMESPACE_OID, "muying-thumbnail-object:" + key))

    def media_worker_jobs(self, limit: int = 10, cursor: Any = None) -> dict[str, Any]:
        base_url, _ = self._require_transfer_config()
        if type(limit) is not int or not 1 <= limit <= 10:
            raise ApiFault(422, "INVALID_PAGE_SIZE", "缩略图任务数量无效。")
        after: tuple[str, str] | None = None
        if cursor is not None:
            if not isinstance(cursor, str) or not 1 <= len(cursor) <= 512:
                raise ApiFault(422, "INVALID_CURSOR", "缩略图任务游标无效。")
            try:
                decoded = json.loads(_unb64url(cursor).decode("utf-8"))
                if (not isinstance(decoded, list) or len(decoded) != 2
                        or not all(isinstance(value, str) for value in decoded)):
                    raise ValueError()
                after = (_opaque_id(decoded[0], "文件标识"), _opaque_id(decoded[1], "版本标识"))
            except (ValueError, UnicodeDecodeError, json.JSONDecodeError, ApiFault):
                raise ApiFault(422, "INVALID_CURSOR", "缩略图任务游标无效。") from None
        query = """SELECT f.file_id,f.scope_id,f.project_id,f.logical_path,
                f.current_revision_id,f.version,r.storage_key,r.media_type,r.size,r.sha256
            FROM files f JOIN file_revisions r ON r.revision_id=f.current_revision_id
            LEFT JOIN file_thumbnails t ON t.file_id=f.file_id
                AND t.revision_id=f.current_revision_id
                AND t.generator_version=?
            WHERE f.deleted_at IS NULL AND f.media_kind IN ('image','video')
                AND (f.scope_id='public:assets' OR f.scope_id LIKE 'department:%'
                     OR f.scope_id LIKE 'personal:%' OR f.scope_id LIKE 'project:%')
                AND t.file_id IS NULL"""
        args: list[Any] = [THUMBNAIL_GENERATOR_VERSION]
        if after is not None:
            query += " AND (f.file_id>? OR (f.file_id=? AND f.current_revision_id>?))"
            args.extend((after[0], after[0], after[1]))
        query += " ORDER BY f.file_id,f.current_revision_id LIMIT ?"
        args.append(limit)
        with self.store._reading() as conn:
            rows = conn.execute(query, args).fetchall()
        items = []
        for row in rows:
            expires = int(time.time()) + TICKET_LIFETIME_SECONDS
            operation_id = _uid()
            object_kind = self._revision_object_kind(row["storage_key"])
            claims = {"v": 1, "op_id": operation_id, "user_id": "media-worker",
                "action": "download", "direction": "download", "object_id": row["current_revision_id"],
                "object_kind": object_kind, "expected_version": int(row["version"]),
                "size": int(row["size"]), "sha256": row["sha256"],
                "exp": expires, "nonce": _uid()}
            items.append({"file_id": row["file_id"], "revision_id": row["current_revision_id"],
                "scope_id": row["scope_id"], "project_id": row["project_id"],
                "logical_path": row["logical_path"], "media_type": row["media_type"],
                "size": int(row["size"]), "sha256": row["sha256"],
                "download_ticket": self._ticket(claims),
                "download_url": base_url + "/transfer/v1/downloads",
                "expires_at": datetime.fromtimestamp(expires, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")})
        next_cursor = None
        if len(rows) == limit:
            last = rows[-1]
            next_cursor = _b64url(_canonical([last["file_id"], last["current_revision_id"]]))
        return {"items": items, "next_cursor": next_cursor}

    def begin_thumbnail_operation(self, file_id: str, revision_id: str,
                                  request: Mapping[str, Any]) -> dict[str, Any]:
        base_url, _ = self._require_transfer_config()
        file_id = _opaque_id(file_id, "文件标识")
        revision_id = _opaque_id(revision_id, "版本标识")
        worker_id = request.get("worker_id")
        if not isinstance(worker_id, str) or not 1 <= len(worker_id) <= 128:
            raise ApiFault(422, "INVALID_REQUEST", "媒体工人标识无效。")
        size = _positive_int(request.get("size"), "缩略图大小")
        if size > THUMBNAIL_MAX_BYTES:
            raise ApiFault(413, "THUMBNAIL_TOO_LARGE", "缩略图超过大小限制。")
        sha256 = request.get("sha256")
        if not isinstance(sha256, str) or not SHA256_RE.fullmatch(sha256):
            raise ApiFault(422, "INVALID_HASH", "缩略图校验值无效。")
        media_type = request.get("media_type")
        if media_type != "image/webp":
            raise ApiFault(422, "INVALID_MEDIA_TYPE", "缩略图必须是 WebP 格式。")
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            revision = conn.execute("SELECT * FROM file_revisions WHERE file_id=? AND revision_id=?",
                                    (file_id, revision_id)).fetchone()
        if (file_row is None or revision is None or file_row["deleted_at"] is not None
                or file_row["current_revision_id"] != revision_id
                or file_row["media_kind"] not in {"image", "video"}):
            raise ApiFault(404, "NOT_FOUND", "媒体文件版本不存在。")
        object_id = self._thumbnail_object_id(file_id, revision_id)
        now = _now()
        with self.store._transaction() as conn:
            existing = conn.execute("""SELECT * FROM file_thumbnails WHERE file_id=? AND revision_id=?
                    AND generator_version=?""",
                (file_id, revision_id, THUMBNAIL_GENERATOR_VERSION)).fetchone()
            operation = conn.execute("""SELECT * FROM thumbnail_operations WHERE file_id=? AND revision_id=?
                    AND generator_version=?""",
                (file_id, revision_id, THUMBNAIL_GENERATOR_VERSION)).fetchone()
            if existing is not None:
                if operation is None:
                    operation_id = _uid()
                    conn.execute("""INSERT INTO thumbnail_operations
                        (operation_id,file_id,revision_id,generator_version,object_id,size,sha256,
                         media_type,state,created_at,updated_at)
                        VALUES(?,?,?,?,?,?,?,?,'committed',?,?)""",
                        (operation_id, file_id, revision_id, THUMBNAIL_GENERATOR_VERSION,
                         existing["object_id"], existing["size"], existing["sha256"],
                         existing["media_type"], now, now))
                else:
                    operation_id = operation["operation_id"]
                return {"operation_id": operation_id, "object_id": existing["object_id"],
                    "state": "committed", "size": int(existing["size"]), "sha256": existing["sha256"]}
            if operation is not None and (int(operation["size"]) != size or operation["sha256"] != sha256):
                raise ApiFault(409, "THUMBNAIL_CONFLICT", "相同文件版本生成了不同的缩略图，请刷新媒体工人版本。")
            if operation is not None:
                if operation["state"] == "committed":
                    raise ApiFault(409, "THUMBNAIL_STATE_CONFLICT", "缩略图操作状态与记录不一致，请检查服务状态。")
                operation_id = operation["operation_id"]
                object_id = operation["object_id"]
                conn.execute("UPDATE thumbnail_operations SET updated_at=? WHERE operation_id=?",
                             (now, operation_id))
            else:
                operation_id = _uid()
                conn.execute("""INSERT INTO thumbnail_operations
                    (operation_id,file_id,revision_id,generator_version,object_id,size,sha256,
                     media_type,state,created_at,updated_at)
                    VALUES(?,?,?,?,?,?,?,?,'receiving',?,?)""",
                    (operation_id, file_id, revision_id, THUMBNAIL_GENERATOR_VERSION,
                     object_id, size, sha256, media_type, now, now))
        expires = int(time.time()) + TICKET_LIFETIME_SECONDS
        claims = {"v": 1, "op_id": operation_id, "user_id": "media-worker",
            "action": "upload", "direction": "upload", "object_id": object_id,
            "object_kind": "thumbnail", "expected_version": 0,
            "size": size, "sha256": sha256, "exp": expires, "nonce": _uid()}
        return {"operation_id": operation_id, "object_id": object_id,
            "object_kind": "thumbnail", "expected_version": 0,
            "ticket": self._ticket(claims), "transfer_url": base_url + "/transfer/v1/uploads",
            "expires_at": datetime.fromtimestamp(expires, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")}

    def commit_thumbnail_operation(self, file_id: str, revision_id: str, operation_id: str,
                                   receipt: Any) -> dict[str, Any]:
        file_id = _opaque_id(file_id, "文件标识")
        revision_id = _opaque_id(revision_id, "版本标识")
        operation_id = _opaque_id(operation_id)
        verified = self._verify_receipt(receipt)
        if verified["op_id"] != operation_id:
            raise ApiFault(422, "RECEIPT_MISMATCH", "缩略图回执与操作不一致。")
        now = _now()
        with self.store._transaction() as conn:
            operation = conn.execute("SELECT * FROM thumbnail_operations WHERE operation_id=?",
                                     (operation_id,)).fetchone()
            if operation is None or operation["file_id"] != file_id or operation["revision_id"] != revision_id:
                raise ApiFault(404, "NOT_FOUND", "缩略图操作不存在。")
            if (verified["object_id"] != operation["object_id"]
                    or verified["size"] != int(operation["size"])
                    or verified["sha256"] != operation["sha256"]):
                raise ApiFault(422, "RECEIPT_MISMATCH", "缩略图回执与文件清单不一致。")
            existing = conn.execute("""SELECT * FROM file_thumbnails WHERE file_id=? AND revision_id=?
                    AND generator_version=?""",
                (file_id, revision_id, THUMBNAIL_GENERATOR_VERSION)).fetchone()
            if existing is None:
                compact_id = operation["object_id"].replace("-", "")
                storage_key = f"_objects/thumbnail/{compact_id[:2]}/{compact_id}"
                conn.execute("""INSERT INTO file_thumbnails
                    (file_id,revision_id,generator_version,object_id,storage_key,sha256,size,media_type,created_at)
                    VALUES(?,?,?,?,?,?,?,?,?)""",
                    (file_id, revision_id, THUMBNAIL_GENERATOR_VERSION, operation["object_id"],
                     storage_key, operation["sha256"], operation["size"], operation["media_type"], now))
            conn.execute("UPDATE thumbnail_operations SET state='committed',receipt_json=?,updated_at=? WHERE operation_id=?",
                         (_canonical(receipt).decode("utf-8"), now, operation_id))
        return {"operation_id": operation_id, "state": "committed", "file_id": file_id,
            "revision_id": revision_id, "object_id": operation["object_id"]}

    def thumbnail_download_ticket(self, actor: Any, file_id: str,
                                  request: Mapping[str, Any]) -> dict[str, Any]:
        base_url, _ = self._require_transfer_config()
        actor_id, _actor_name, _user = _actor(actor)
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        file_id = _opaque_id(file_id, "文件标识")
        revision_id = _opaque_id(request.get("revision_id"), "版本标识")
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT * FROM files WHERE file_id=?", (file_id,)).fetchone()
            if file_row is None:
                raise ApiFault(404, "NOT_FOUND", "文件不存在。")
            self._read_scope(actor, file_row["scope_id"], file_row["project_id"], allow_deleted_project=True)
            artifact = conn.execute("""SELECT * FROM file_thumbnails WHERE file_id=? AND revision_id=?
                AND generator_version=?""", (file_id, revision_id, THUMBNAIL_GENERATOR_VERSION)).fetchone()
        if artifact is None:
            raise ApiFault(404, "THUMBNAIL_NOT_READY", "此文件版本的缩略图尚未生成。")
        expires = int(time.time()) + TICKET_LIFETIME_SECONDS
        operation_id = _uid()
        claims = {"v": 1, "op_id": operation_id, "user_id": actor_id,
            "action": "download", "direction": "download", "object_id": artifact["object_id"],
            "object_kind": "thumbnail", "expected_version": int(file_row["version"]),
            "size": int(artifact["size"]), "sha256": artifact["sha256"],
            "exp": expires, "nonce": _uid()}
        return {"file_id": file_id, "revision_id": revision_id, "object_id": artifact["object_id"],
            "object_kind": "thumbnail", "size": int(artifact["size"]), "sha256": artifact["sha256"],
            "ticket": self._ticket(claims), "transfer_url": base_url + "/transfer/v1/downloads",
            "expires_at": datetime.fromtimestamp(expires, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")}

    def authenticate_media_worker(self, headers: Mapping[str, str]) -> None:
        if self.media_worker_token is None:
            raise ApiFault(503, "MEDIA_WORKER_UNAVAILABLE", "媒体处理服务尚未配置。")
        raw = headers.get("authorization", "")
        supplied = raw[7:].strip() if raw.startswith("Bearer ") else ""
        if not supplied or not hmac.compare_digest(supplied, self.media_worker_token):
            raise ApiFault(401, "MEDIA_WORKER_UNAUTHORIZED", "媒体处理服务认证失败。")

    def directory_operation(self, actor: Any, directory_id: str,
                            request: Mapping[str, Any]) -> dict[str, Any]:
        actor_id, actor_name, _user = _writable(actor)
        directory_id = _opaque_id(directory_id, "文件夹标识")
        operation_id = _opaque_id(request.get("operation_id"))
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        action = request.get("action")
        if action not in {"delete", "move", "restore"}:
            raise ApiFault(422, "INVALID_ACTION", "文件夹操作无效。")
        expected = _positive_int(request.get("expected_version"), "文件夹版本")
        previous = self._replay_directory_operation(actor_id, request, operation_id,
            client_id, directory_id, action, expected)
        if previous is not None:
            return previous
        with self.store._reading() as conn:
            old = conn.execute("SELECT * FROM file_directories WHERE directory_id=?", (directory_id,)).fetchone()
        if old is None:
            raise ApiFault(404, "NOT_FOUND", "文件夹不存在。")
        old_scope, old_project, _old_owner = self._scope(actor, old["scope_id"], old["project_id"],
            write=(action != "restore"), allow_deleted_project=(action == "restore"))
        target_scope = target_project = target_owner = target_path = target_key = None
        if action == "move":
            target_scope, target_project, target_owner = self._scope(actor,
                request.get("target_scope_id"), request.get("target_project_id"), write=True)
            target_path, target_key = _logical_path(request.get("target_logical_path"))
        request_json = _canonical({"operation_id": operation_id, "action": "directory_" + action,
            "client_id": client_id, "directory_id": directory_id, "expected_version": expected,
            "target_scope_id": target_scope, "target_project_id": target_project,
            "target_logical_path": target_path}).decode("utf-8")
        previous = self._previous_committed_result(operation_id, actor_id, request_json)
        if previous is not None:
            return previous
        now = _now()
        prefix = old["logical_path_key"] + "/"
        with self.store._transaction() as conn:
            previous = self._operation_result_tx(conn, operation_id, actor_id, request_json)
            if previous is not None:
                return previous
            current = conn.execute("SELECT * FROM file_directories WHERE directory_id=?", (directory_id,)).fetchone()
            if current is None:
                raise ApiFault(404, "NOT_FOUND", "文件夹不存在。")
            if int(current["version"]) != expected:
                raise ApiFault(409, "CONFLICT", "文件夹已被其他人修改，请刷新后重试。")
            result: dict[str, Any] = {"operation_id": operation_id, "state": "committed",
                                      "directory_id": directory_id}
            if action == "delete":
                if current["deleted_at"] is not None:
                    raise ApiFault(404, "NOT_FOUND", "活动文件夹不存在。")
                conn.execute("""UPDATE file_directories SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                    version=version+1,updated_at=? WHERE scope_id=? AND (logical_path_key=? OR
                    substr(logical_path_key,1,?)=?) AND deleted_at IS NULL""",
                    (now, actor_name, operation_id, now, old_scope, current["logical_path_key"],
                     len(prefix), prefix))
                files = conn.execute("""SELECT * FROM files WHERE scope_id=? AND
                    (substr(logical_path_key,1,?)=?) AND deleted_at IS NULL""",
                    (old_scope, len(prefix), prefix)).fetchall()
                for file_row in files:
                    next_version = int(file_row["version"]) + 1
                    conn.execute("""UPDATE files SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                        version=?,updated_at=? WHERE file_id=?""",
                        (now, actor_name, operation_id, next_version, now, file_row["file_id"]))
                    self._event(conn, "file", file_row["file_id"], "delete", file_id=file_row["file_id"],
                        scope_id=file_row["scope_id"], project_id=file_row["project_id"],
                        logical_path=file_row["logical_path"], revision_id=file_row["current_revision_id"],
                        version=next_version, actor_id=actor_id, actor_name=actor_name)
                    if file_row["media_kind"] == "image" and file_row["project_id"]:
                        conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                                     ("__deleted__/" + file_row["file_id"], now, file_row["file_id"]))
                sequence = self._event(conn, "directory", directory_id, "delete", scope_id=old_scope,
                    project_id=old_project, logical_path=current["logical_path"],
                    version=int(current["version"]) + 1, actor_id=actor_id, actor_name=actor_name)
                result.update({"version": int(current["version"]) + 1, "change_sequence": sequence})
            elif action == "restore":
                if current["deleted_at"] is None or current["deleted_operation_id"] is None:
                    raise ApiFault(409, "NOT_DELETED", "文件夹尚未删除。")
                deletion_id = current["deleted_operation_id"]
                # Restore only descendants tombstoned by this directory deletion.
                dirs = conn.execute("""SELECT * FROM file_directories WHERE scope_id=?
                    AND deleted_operation_id=?""", (old_scope, deletion_id)).fetchall()
                files = conn.execute("""SELECT * FROM files WHERE scope_id=? AND deleted_operation_id=?""",
                                     (old_scope, deletion_id)).fetchall()
                for item in dirs:
                    self._assert_path_free(conn, old_scope, item["logical_path_key"],
                                           except_directory_id=item["directory_id"])
                for item in files:
                    self._assert_path_free(conn, old_scope, item["logical_path_key"],
                                           except_file_id=item["file_id"])
                conn.execute("""UPDATE file_directories SET deleted_at=NULL,deleted_by=NULL,
                    deleted_operation_id=NULL,version=version+1,updated_at=? WHERE scope_id=? AND deleted_operation_id=?""",
                    (now, old_scope, deletion_id))
                conn.execute("""UPDATE files SET deleted_at=NULL,deleted_by=NULL,deleted_operation_id=NULL,
                    version=version+1,updated_at=? WHERE scope_id=? AND deleted_operation_id=?""",
                    (now, old_scope, deletion_id))
                for file_row in files:
                    self._event(conn, "file", file_row["file_id"], "restore", file_id=file_row["file_id"],
                        scope_id=file_row["scope_id"], project_id=file_row["project_id"],
                        logical_path=file_row["logical_path"], revision_id=file_row["current_revision_id"],
                        version=int(file_row["version"]) + 1, actor_id=actor_id, actor_name=actor_name)
                    if file_row["media_kind"] == "image" and file_row["project_id"]:
                        revision = conn.execute("SELECT media_type FROM file_revisions WHERE revision_id=?",
                                                (file_row["current_revision_id"],)).fetchone()
                        if revision is not None:
                            self._sync_image_asset_tx(conn, file_row["file_id"], file_row["project_id"],
                                file_row["logical_path"], revision["media_type"],
                                int(file_row["version"]) + 1, now)
                sequence = self._event(conn, "directory", directory_id, "restore", scope_id=old_scope,
                    project_id=old_project, logical_path=current["logical_path"],
                    version=int(current["version"]) + 1, actor_id=actor_id, actor_name=actor_name)
                result.update({"version": int(current["version"]) + 1, "change_sequence": sequence})
            else:
                if current["deleted_at"] is not None:
                    raise ApiFault(404, "NOT_FOUND", "活动文件夹不存在。")
                if target_key == current["logical_path_key"] and target_scope == old_scope:
                    result.update({"version": int(current["version"]), "change_sequence": 0})
                else:
                    if target_scope == old_scope and target_key.startswith(current["logical_path_key"] + "/"):
                        raise ApiFault(422, "INVALID_PATH", "文件夹不能移动到自己的子目录中。")
                    self._assert_path_free(conn, target_scope, target_key)
                    self._ensure_parent_directories(conn, target_scope, target_project, target_owner,
                        target_path, actor_id, actor_name)
                    moved_dirs = conn.execute("""SELECT * FROM file_directories WHERE scope_id=?
                        AND deleted_at IS NULL AND (logical_path_key=? OR substr(logical_path_key,1,?)=?)""",
                        (old_scope, current["logical_path_key"], len(prefix), prefix)).fetchall()
                    moved_files = conn.execute("""SELECT * FROM files WHERE scope_id=?
                        AND deleted_at IS NULL AND substr(logical_path_key,1,?)=?""",
                        (old_scope, len(prefix), prefix)).fetchall()
                    video_path_updates: dict[str, tuple[str, str | None]] = {}
                    for row in moved_dirs:
                        suffix = row["logical_path"][len(current["logical_path"]):].lstrip("/")
                        new_path = target_path + ("/" + suffix if suffix else "")
                        new_path, new_key = _logical_path(new_path)
                        conn.execute("""UPDATE file_directories SET scope_id=?,project_id=?,owner_user_id=?,
                            logical_path=?,logical_path_key=?,version=version+1,updated_at=? WHERE directory_id=?""",
                            (target_scope, target_project, target_owner, new_path, new_key, now, row["directory_id"]))
                        self._event(conn, "directory", row["directory_id"], "move", scope_id=target_scope,
                            project_id=target_project, logical_path=new_path, previous_scope_id=old_scope,
                            previous_project_id=old_project, previous_logical_path=row["logical_path"],
                            version=int(row["version"]) + 1, actor_id=actor_id, actor_name=actor_name)
                    for row in moved_files:
                        suffix = row["logical_path"][len(current["logical_path"]) + 1:]
                        new_path, new_key = _logical_path(target_path + "/" + suffix)
                        conn.execute("""UPDATE files SET scope_id=?,project_id=?,owner_user_id=?,logical_path=?,
                            logical_path_key=?,version=version+1,updated_at=? WHERE file_id=?""",
                            (target_scope, target_project, target_owner, new_path, new_key, now, row["file_id"]))
                        video_path_updates[row["file_id"]] = (new_path, target_project)
                        self._event(conn, "file", row["file_id"], "move", file_id=row["file_id"],
                            scope_id=target_scope, project_id=target_project, logical_path=new_path,
                            previous_scope_id=old_scope, previous_project_id=old_project,
                            previous_logical_path=row["logical_path"], revision_id=row["current_revision_id"],
                            version=int(row["version"]) + 1, actor_id=actor_id, actor_name=actor_name)
                        if row["media_kind"] == "image":
                            if target_project:
                                revision = conn.execute("SELECT media_type FROM file_revisions WHERE revision_id=?",
                                    (row["current_revision_id"],)).fetchone()
                                if revision is not None:
                                    self._sync_image_asset_tx(conn, row["file_id"], target_project,
                                        new_path, revision["media_type"], int(row["version"]) + 1, now)
                            else:
                                conn.execute("UPDATE image_assets SET active=0,relative_path=?,updated_at=? WHERE id=?",
                                    ("__deleted__/" + row["file_id"], now, row["file_id"]))
                    self._sync_linked_video_paths_tx(conn, video_path_updates, now)
                    moved_root = next((row for row in moved_dirs if row["directory_id"] == directory_id), current)
                    root_version = int(moved_root["version"]) + 1
                    sequence = self._event(conn, "directory", directory_id, "move", scope_id=target_scope,
                        project_id=target_project, logical_path=target_path, previous_scope_id=old_scope,
                        previous_project_id=old_project, previous_logical_path=current["logical_path"],
                        version=root_version, actor_id=actor_id, actor_name=actor_name)
                    result.update({"version": root_version, "logical_path": target_path,
                                   "change_sequence": sequence})
            self._insert_committed_operation(conn, operation_id=operation_id,
                action="directory_" + action, actor_id=actor_id, actor_name=actor_name,
                client_id=client_id, file_id=None, scope_id=old_scope, project_id=old_project,
                logical_path=current["logical_path"], expected_version=expected, source=None,
                request_json=request_json, result=result, target_scope_id=target_scope,
                target_project_id=target_project, target_logical_path=target_path)
        self.store._signal_change()
        return result

    def changes(self, actor: Any, *, client_id: str, after_sequence: int, limit: int = 500) -> dict[str, Any]:
        _writable(actor)
        actor_id, _name, _user = _actor(actor)
        client_id = _opaque_id(client_id, "客户端标识")
        after_sequence = _positive_int(after_sequence, "变更游标", allow_zero=True)
        if type(limit) is not int or not 1 <= limit <= 1000:
            raise ApiFault(422, "INVALID_PAGE_SIZE", "变更数量无效。")
        now = _now()
        with self.store._transaction() as conn:
            client = conn.execute("SELECT * FROM sync_clients WHERE client_id=?", (client_id,)).fetchone()
            if client is not None and client["user_id"] != actor_id:
                raise ApiFault(403, "FORBIDDEN", "同步客户端标识属于其他账户。")
            conn.execute("""INSERT INTO sync_clients(client_id,user_id,last_sequence,status,updated_at)
                VALUES(?,?,0,'active',?) ON CONFLICT(client_id) DO UPDATE SET status='active',updated_at=excluded.updated_at""",
                (client_id, actor_id, now))
            rows = conn.execute("SELECT * FROM file_events WHERE sequence>? ORDER BY sequence LIMIT ?",
                                (after_sequence, limit + 1)).fetchall()
            max_row = conn.execute("SELECT COALESCE(MAX(sequence),0) AS n FROM file_events").fetchone()
        has_more = len(rows) > limit
        scanned = rows[:limit]
        items: list[dict[str, Any]] = []
        for row in scanned:
            def can_read_side(scope_id: str | None, project_id: str | None) -> bool:
                if not scope_id:
                    return False
                try:
                    self._read_scope(actor, scope_id, project_id, allow_deleted_project=True)
                    return True
                except ApiFault:
                    return False

            current_visible = can_read_side(row["scope_id"], row["project_id"])
            previous_visible = can_read_side(row["previous_scope_id"], row["previous_project_id"])
            if row["entity_kind"] in {"project", "workbook"}:
                current_visible = True
            if current_visible or previous_visible:
                item = {key: row[key] for key in (
                    "sequence", "entity_kind", "entity_id", "action", "file_id", "scope_id",
                    "project_id", "logical_path", "previous_scope_id", "previous_project_id",
                    "previous_logical_path", "revision_id", "version", "source", "actor_id", "created_at")}
                if row["action"] in {"move", "rename", "retarget_deleted"} and current_visible != previous_visible:
                    item["previous_scope_id"] = None
                    item["previous_project_id"] = None
                    item["previous_logical_path"] = None
                    if current_visible:
                        # The caller can see only the destination. Present it as a new
                        # visible item and avoid exposing the source scope or path.
                        item["action"] = "create"
                    else:
                        # The caller can see only the source. Present a removal from
                        # that scope without revealing where the item moved.
                        item["action"] = "delete"
                        item["scope_id"] = row["previous_scope_id"]
                        item["project_id"] = row["previous_project_id"]
                        item["logical_path"] = row["previous_logical_path"]
                        item["revision_id"] = None
                items.append(item)
        next_sequence = scanned[-1]["sequence"] if scanned else after_sequence
        return {"items": items, "next_sequence": next_sequence, "has_more": has_more,
                "reset_required": False}

    def acknowledge(self, actor: Any, request: Mapping[str, Any]) -> dict[str, Any]:
        _writable(actor)
        actor_id, _name, _user = _actor(actor)
        client_id = _opaque_id(request.get("client_id"), "客户端标识")
        sequence = _positive_int(request.get("sequence"), "变更游标", allow_zero=True)
        now = _now()
        with self.store._transaction() as conn:
            max_seq = int(conn.execute("SELECT COALESCE(MAX(sequence),0) AS n FROM file_events").fetchone()["n"])
            if sequence > max_seq:
                raise ApiFault(422, "INVALID_CURSOR", "变更游标超出服务器范围。")
            existing = conn.execute("SELECT user_id,last_sequence FROM sync_clients WHERE client_id=?",
                                    (client_id,)).fetchone()
            if existing is not None and existing["user_id"] != actor_id:
                raise ApiFault(403, "FORBIDDEN", "同步客户端标识属于其他账户。")
            conn.execute("""INSERT INTO sync_clients(client_id,user_id,last_sequence,status,updated_at)
                VALUES(?,?,?,'active',?) ON CONFLICT(client_id) DO UPDATE SET
                last_sequence=MAX(sync_clients.last_sequence,excluded.last_sequence),
                status='active',updated_at=excluded.updated_at""",
                (client_id, actor_id, sequence, now))
            last = int(conn.execute("SELECT last_sequence FROM sync_clients WHERE client_id=?",
                                    (client_id,)).fetchone()["last_sequence"])
        return {"client_id": client_id, "sequence": last}


class FileStorageApi:
    """HTTP adapter for file metadata operations; it never accepts file content bytes."""

    def __init__(self, security: Any, service: FileStorageService):
        self.security = security
        self.service = service

    @staticmethod
    def owns(target: str) -> bool:
        path = urlsplit(target).path
        return (path == FILES_ROOT or path.startswith(FILES_ROOT + "/")
                or path == OPERATIONS_ROOT or path.startswith(OPERATIONS_ROOT + "/")
                or path == DIRECTORIES_ROOT or path.startswith(DIRECTORIES_ROOT + "/")
                or path == MEDIA_WORKER_ROOT or path.startswith(MEDIA_WORKER_ROOT + "/")
                or path in {CHANGES_PATH, ACK_PATH})

    @staticmethod
    def _query(raw: str) -> dict[str, str]:
        parsed = parse_qs(raw, keep_blank_values=True, strict_parsing=True)
        if any(len(values) != 1 for values in parsed.values()):
            raise ApiFault(422, "INVALID_REQUEST", "请求参数不能重复。")
        return {key: values[0] for key, values in parsed.items()}

    @staticmethod
    def _bool(value: str, label: str) -> bool:
        if value in {"true", "1"}:
            return True
        if value in {"false", "0"}:
            return False
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")

    @staticmethod
    def _id_segment(value: str) -> str:
        try:
            return _opaque_id(unquote(value, encoding="utf-8", errors="strict"))
        except (UnicodeDecodeError, ApiFault):
            raise ApiFault(404, "NOT_FOUND", "接口不存在。") from None

    def _route(self, actor: Any, method: str, target: str,
               body: Mapping[str, Any] | None = None) -> Response:
        parsed = urlsplit(target)
        path = parsed.path.rstrip("/") or "/"
        query = self._query(parsed.query)
        body = body or {}
        if method == "GET" and path == FILES_ROOT:
            allowed = {"scope_id", "project_id", "parent_path", "media_kind", "include_deleted", "cursor", "limit"}
            if set(query) - allowed or "scope_id" not in query:
                raise ApiFault(422, "INVALID_REQUEST", "文件查询参数无效。")
            include_deleted = self._bool(query.get("include_deleted", "false"), "删除项筛选")
            try:
                limit = int(query.get("limit", "100"))
            except ValueError:
                raise ApiFault(422, "INVALID_PAGE_SIZE", "文件列表页大小无效。") from None
            return json_response(200, self.service.list_files(actor,
                scope_id=query["scope_id"], project_id=query.get("project_id"),
                parent_path=query.get("parent_path", ""), media_kind=query.get("media_kind", "all"),
                include_deleted=include_deleted, cursor=query.get("cursor"), limit=limit))
        if method == "GET" and path == CHANGES_PATH:
            if set(query) != {"client_id", "after_sequence", "limit"}:
                raise ApiFault(422, "INVALID_REQUEST", "变更查询参数无效。")
            try:
                after, limit = int(query["after_sequence"]), int(query["limit"])
            except ValueError:
                raise ApiFault(422, "INVALID_REQUEST", "变更查询参数无效。") from None
            return json_response(200, self.service.changes(actor, client_id=query["client_id"],
                after_sequence=after, limit=limit))
        parts = path[len(API_ROOT):].strip("/").split("/") if path.startswith(API_ROOT + "/") else []
        parts = [unquote(part, encoding="utf-8", errors="strict") for part in parts]
        if method == "GET" and len(parts) == 3 and parts[0] == "files" and parts[2] == "revisions":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "历史查询不接受额外参数。")
            return json_response(200, self.service.file_history(actor, self._id_segment(parts[1])))
        if method == "POST" and path == OPERATIONS_ROOT:
            action = body.get("action")
            if action == "upload":
                return json_response(201, self.service.begin_upload(actor, body))
            if action == "delete":
                return json_response(200, self.service.delete_file(actor, body))
            if action == "move":
                return json_response(200, self.service.move_file(actor, body))
            raise ApiFault(422, "INVALID_ACTION", "文件操作无效。")
        if method == "POST" and path == OPERATIONS_ROOT + "/batch":
            return json_response(207, self.service.begin_upload_batch(actor, body))
        if method == "POST" and len(parts) == 3 and parts[0] == "file-operations" and parts[2] == "abort":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "取消上传不接受额外参数。")
            return json_response(200, self.service.abort_upload(actor,
                self._id_segment(parts[1]), body))
        if method == "POST" and len(parts) == 3 and parts[0] == "file-operations" and parts[2] == "commit":
            return json_response(200, self.service.commit_upload(actor, self._id_segment(parts[1]), body.get("receipt")))
        if method == "POST" and len(parts) == 3 and parts[0] == "files" and parts[2] == "restore":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "恢复请求不接受额外参数。")
            return json_response(200, self.service.restore_file(actor, self._id_segment(parts[1]), body))
        if method == "POST" and len(parts) == 3 and parts[0] == "files" and parts[2] == "download":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "下载请求不接受额外参数。")
            return json_response(200, self.service.download_ticket(actor, self._id_segment(parts[1]), body))
        if method == "POST" and len(parts) == 3 and parts[0] == "files" and parts[2] == "thumbnail":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "缩略图请求不接受额外参数。")
            return json_response(200, self.service.thumbnail_download_ticket(
                actor, self._id_segment(parts[1]), body))
        if method == "POST" and path == DIRECTORIES_ROOT:
            if body.get("action") != "create":
                raise ApiFault(422, "INVALID_ACTION", "文件夹操作无效。")
            return json_response(201, self.service.create_directory(actor, body))
        if method == "POST" and len(parts) == 3 and parts[0] == "directories" and parts[2] == "operations":
            return json_response(200, self.service.directory_operation(actor,
                self._id_segment(parts[1]), body))
        if method == "POST" and path == ACK_PATH:
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "同步确认请求不接受额外参数。")
            return json_response(200, self.service.acknowledge(actor, body))
        if path.startswith(FILES_ROOT) or path.startswith(OPERATIONS_ROOT) or path.startswith(DIRECTORIES_ROOT) or path in {CHANGES_PATH, ACK_PATH}:
            raise ApiFault(404, "NOT_FOUND", "接口不存在。")
        raise ApiFault(404, "NOT_FOUND", "接口不存在。")

    def _media_worker_route(self, method: str, target: str,
                            body: Mapping[str, Any] | None = None) -> Response:
        parsed = urlsplit(target)
        path = parsed.path.rstrip("/") or "/"
        query = self._query(parsed.query)
        body = body or {}
        if method == "GET" and path == MEDIA_WORKER_ROOT + "/jobs":
            if set(query) - {"limit", "cursor"}:
                raise ApiFault(422, "INVALID_REQUEST", "缩略图任务查询参数无效。")
            try:
                limit = int(query.get("limit", "10"))
            except ValueError:
                raise ApiFault(422, "INVALID_PAGE_SIZE", "缩略图任务数量无效。") from None
            return json_response(200, self.service.media_worker_jobs(limit, query.get("cursor")))
        parts = path[len(MEDIA_WORKER_ROOT):].strip("/").split("/")
        parts = [unquote(part, encoding="utf-8", errors="strict") for part in parts]
        if method == "POST" and len(parts) == 4 and parts[0] == "thumbnails" and parts[3] == "operations":
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "缩略图操作不接受额外参数。")
            return json_response(201, self.service.begin_thumbnail_operation(
                self._id_segment(parts[1]), self._id_segment(parts[2]), body))
        if (method == "POST" and len(parts) == 6 and parts[0] == "thumbnails"
                and parts[3] == "operations" and parts[5] == "commit"):
            if query:
                raise ApiFault(422, "INVALID_REQUEST", "缩略图提交不接受额外参数。")
            return json_response(200, self.service.commit_thumbnail_operation(
                self._id_segment(parts[1]), self._id_segment(parts[2]),
                self._id_segment(parts[4]), body.get("receipt")))
        raise ApiFault(404, "NOT_FOUND", "媒体工人接口不存在。")

    def handle(self, handler: Any) -> bool:
        if not self.owns(handler.path):
            return False
        response: Response
        try:
            headers = {key.lower(): value for key, value in handler.headers.items()}
            method = handler.command
            path = urlsplit(handler.path).path.rstrip("/") or "/"
            is_media_worker = path == MEDIA_WORKER_ROOT or path.startswith(MEDIA_WORKER_ROOT + "/")
            if is_media_worker:
                self.service.authenticate_media_worker(headers)
                actor = None
            else:
                actor = self.security.authenticate(headers)
            if method == "GET":
                if handler.headers.get("Content-Length") not in (None, "0"):
                    raise ApiFault(400, "INVALID_REQUEST", "查询请求不能包含内容。")
                response = (self._media_worker_route(method, handler.path) if is_media_worker
                            else self._route(actor, method, handler.path))
            elif method == "POST":
                body = handler._read_json(MAX_CONTROL_BODY, reject_duplicate_fields=True)
                if body is None:
                    return True
                response = (self._media_worker_route(method, handler.path, body) if is_media_worker
                            else self._route(actor, method, handler.path, body))
            else:
                raise ApiFault(405, "METHOD_NOT_ALLOWED", "请求方法不受支持。")
        except ApiFault as error:
            response = error_response(error)
        except (ValueError, UnicodeDecodeError):
            response = error_response(ApiFault(422, "INVALID_REQUEST", "请求内容无效。"))
        except Exception:
            response = error_response(ApiFault(503, "STORAGE_UNAVAILABLE", "文件目录暂不可用，请稍后重试。"))
        body_bytes = response.body if isinstance(response.body, bytes) else b"{}"
        try:
            payload = json.loads(body_bytes.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            payload = {"error": {"code": "STORAGE_UNAVAILABLE", "message": "文件目录暂不可用。"}}
            response = json_response(503, payload)
        handler._send_json(response.status, payload)
        return True
