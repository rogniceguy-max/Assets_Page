"""SQLite source of truth for project workbooks and logical project file metadata.

The database is local to the V2 service host. WPF clients access it only through the
authenticated HTTP API. Project folders and NAS paths are not authoritative sources.
"""
from __future__ import annotations

import json
import re
import sqlite3
import threading
import time
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Mapping

from .host_adapters import _asset_component
from .projects_api import (SCENE_ROW_VERSION_KEYS, UPLOAD_LIMIT, ApiFault,
                           is_pure_video_operation, scene_row_update_version_fields,
                           video_item_payload)

IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp"}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _dict(value: str | None) -> dict[str, Any]:
    parsed = json.loads(value or "{}")
    return parsed if isinstance(parsed, dict) else {}


def _uid() -> str:
    return str(uuid.uuid4())


def _scene_beat_key(data: Mapping[str, Any]) -> str | None:
    raw = data.get("beat_number") or ""
    digits = "".join(character for character in str(raw) if character.isdigit())
    if not digits:
        return None
    return digits.lstrip("0") or "0"


def _scene_video_path(sheet_name: str, data: Mapping[str, Any], video: Mapping[str, Any]) -> str:
    beat = _scene_beat_key(data)
    if beat is None:
        raise ApiFault(422, "INVALID_REQUEST", "请先为节拍行填写有效编号，再关联视频。")
    name = str(video.get("name") or "")
    if (not sheet_name or sheet_name in {".", ".."} or any(char in sheet_name for char in "/\\")
            or not name):
        raise ApiFault(422, "INVALID_REQUEST", "工作表或视频名称无法用于项目内路径。")
    return f"{sheet_name}/第{beat}beat/{name}"


def _is_stored_scene_video_path(sheet_name: str, video: Mapping[str, Any]) -> bool:
    path = video.get("path")
    name = str(video.get("name") or "")
    if not isinstance(path, str) or not path or not name or "\\" in path or path.startswith("/"):
        return False
    if re.match(r"^[A-Za-z]:", path):
        return False
    segments = path.split("/")
    return (
        len(segments) == 3
        and all(segment not in {"", ".", ".."} for segment in segments)
        and segments[0].casefold() == sheet_name.casefold()
        and segments[2].casefold() == name.casefold()
        and re.fullmatch(r"(?:第)?\d+beat", segments[1], flags=re.IGNORECASE) is not None
    )


class SqliteProjectWorkbookStore:
    """Implements workbook and workflow APIs over one server-local SQLite database."""

    def __init__(self, db_path: Path | str) -> None:
        self._db_path = Path(db_path)
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._events = threading.Condition()
        self._local = threading.local()
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self._db_path, timeout=30, isolation_level=None)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA busy_timeout=30000")
        conn.execute("PRAGMA synchronous=NORMAL")
        conn.execute("PRAGMA cache_size=-8000")
        conn.execute("PRAGMA temp_store=MEMORY")
        return conn

    @contextmanager
    def _reading(self) -> Iterator[sqlite3.Connection]:
        depth = getattr(self._local, "depth", 0)
        self._local.depth = depth + 1
        if depth == 0:
            self._local.conn = self._connect()
        try:
            yield self._local.conn
        finally:
            self._local.depth = max(0, getattr(self._local, "depth", 1) - 1)
            if self._local.depth == 0:
                conn = getattr(self._local, "conn", None)
                self._local.conn = None
                if conn is not None:
                    try:
                        conn.close()
                    except Exception:
                        pass

    @contextmanager
    def _transaction(self) -> Iterator[sqlite3.Connection]:
        conn = self._connect()
        try:
            conn.execute("BEGIN IMMEDIATE")
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    def _init_db(self) -> None:
        conn = self._connect()
        try:
            starting_version = int(conn.execute("PRAGMA user_version").fetchone()[0])
            if starting_version > 6:
                raise RuntimeError(
                    f"project SQLite schema {starting_version} is newer than supported schema 6")
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA synchronous=NORMAL")
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    directory_identity TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    directory_name TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS workbooks (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    display_name TEXT NOT NULL,
                    template_kind TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    name_version INTEGER NOT NULL DEFAULT 1,
                    position INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_workbooks_project ON workbooks(project_id, position, display_name);
                CREATE TABLE IF NOT EXISTS sheets (
                    id TEXT PRIMARY KEY,
                    workbook_id TEXT NOT NULL REFERENCES workbooks(id) ON DELETE CASCADE,
                    display_name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    UNIQUE(workbook_id, display_name)
                );
                CREATE TABLE IF NOT EXISTS workbook_rows (
                    id TEXT PRIMARY KEY,
                    sheet_id TEXT NOT NULL REFERENCES sheets(id) ON DELETE CASCADE,
                    kind TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    field_versions TEXT NOT NULL DEFAULT '{}',
                    data TEXT NOT NULL,
                    deleted_at TEXT,
                    deleted_by TEXT,
                    UNIQUE(sheet_id, position)
                );
                CREATE INDEX IF NOT EXISTS idx_rows_sheet ON workbook_rows(sheet_id, position);
                CREATE TABLE IF NOT EXISTS asset_folders (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    relative_path TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    UNIQUE(project_id, relative_path)
                );
                CREATE TABLE IF NOT EXISTS image_assets (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    folder_id TEXT REFERENCES asset_folders(id) ON DELETE SET NULL,
                    relative_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    active INTEGER NOT NULL DEFAULT 1,
                    version INTEGER NOT NULL DEFAULT 1,
                    updated_at TEXT NOT NULL,
                    UNIQUE(project_id, relative_path)
                );
                CREATE INDEX IF NOT EXISTS idx_images_project ON image_assets(project_id, active, file_name);
                CREATE TABLE IF NOT EXISTS operation_results (
                    operation_id TEXT PRIMARY KEY,
                    result TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS project_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    workbook_id TEXT NOT NULL,
                    entity_kind TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    changed_fields TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_events_project_sequence ON project_events(project_id, sequence);
            """)
            has_legacy_imports = conn.execute(
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='legacy_imports'").fetchone()
            if has_legacy_imports:
                imported_ids = [row["workbook_id"] for row in conn.execute(
                    "SELECT workbook_id FROM legacy_imports").fetchall()]
                conn.execute("DELETE FROM legacy_imports")
            else:
                imported_ids = []
            generic_ids = [row["id"] for row in conn.execute(
                "SELECT id FROM workbooks WHERE template_kind='generic'").fetchall()]
            unsupported_sheet_ids = [row["id"] for row in conn.execute("""
                SELECT DISTINCT w.id FROM workbooks w JOIN sheets s ON s.workbook_id=w.id
                WHERE s.kind NOT IN ('asset_review','scene_beats')
            """).fetchall()]
            obsolete_workbook_ids = set(imported_ids) | set(generic_ids) | set(unsupported_sheet_ids)
            for obsolete_id in obsolete_workbook_ids:
                conn.execute("DELETE FROM project_events WHERE workbook_id=?", (obsolete_id,))
                conn.execute("DELETE FROM workbooks WHERE id=?", (obsolete_id,))
            conn.execute("DROP TABLE IF EXISTS filesystem_operations")
            conn.execute("DROP TABLE IF EXISTS generic_columns")
            if has_legacy_imports:
                conn.execute("DROP TABLE legacy_imports")
            image_columns = {row["name"] for row in conn.execute("PRAGMA table_info(image_assets)")}
            if "display_name" not in image_columns:
                conn.execute("ALTER TABLE image_assets ADD COLUMN display_name TEXT NOT NULL DEFAULT ''")
                conn.execute("UPDATE image_assets SET display_name=file_name WHERE display_name='' ")
            if "version" not in image_columns:
                conn.execute("ALTER TABLE image_assets ADD COLUMN version INTEGER NOT NULL DEFAULT 1")
            conn.execute("DROP TABLE IF EXISTS selection_history")
            for row in conn.execute("SELECT id,kind,data,field_versions FROM workbook_rows WHERE kind IN ('scene_beats','asset_review')").fetchall():
                data = _dict(row["data"])
                field_versions = _dict(row["field_versions"])
                changed_data = False
                changed_versions = False
                if row["kind"] == "scene_beats":
                    for field in ("origins", "final_video", "legacy_notes"):
                        if field in data:
                            data.pop(field, None)
                            changed_data = True
                        if field in field_versions:
                            field_versions.pop(field, None)
                            changed_versions = True
                else:
                    selection = data.get("image_selection")
                    if isinstance(selection, dict) and "selection_reason" in selection:
                        selection.pop("selection_reason", None)
                        changed_data = True
                    if "selection_reason" in field_versions:
                        field_versions.pop("selection_reason", None)
                        changed_versions = True
                if changed_data or changed_versions:
                    conn.execute("UPDATE workbook_rows SET data=?,field_versions=? WHERE id=?",
                                 (_json(data), _json(field_versions), row["id"]))
            workbook_columns = {row["name"] for row in conn.execute("PRAGMA table_info(workbooks)")}
            if "revision_of" in workbook_columns:
                stale_revisions = [row["id"] for row in conn.execute(
                    "SELECT id FROM workbooks WHERE revision_of IS NOT NULL").fetchall()]
                for workbook_id in stale_revisions:
                    conn.execute("DELETE FROM project_events WHERE workbook_id=?", (workbook_id,))
                    conn.execute("DELETE FROM workbooks WHERE id=?", (workbook_id,))
            if "name_version" not in workbook_columns:
                conn.execute("ALTER TABLE workbooks ADD COLUMN name_version INTEGER NOT NULL DEFAULT 1")
            row_columns = {row["name"] for row in conn.execute("PRAGMA table_info(workbook_rows)")}
            if "deleted_at" not in row_columns:
                conn.execute("ALTER TABLE workbook_rows ADD COLUMN deleted_at TEXT")
            if "deleted_by" not in row_columns:
                conn.execute("ALTER TABLE workbook_rows ADD COLUMN deleted_by TEXT")
            self._ensure_column(conn, "projects", "deleted_at", "TEXT")
            self._ensure_column(conn, "projects", "deleted_by", "TEXT")
            self._ensure_column(conn, "workbooks", "deleted_at", "TEXT")
            self._ensure_column(conn, "workbooks", "deleted_by", "TEXT")
            conn.executescript("""
                CREATE INDEX IF NOT EXISTS idx_projects_active_name
                    ON projects(display_name COLLATE NOCASE) WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS idx_workbooks_active_project
                    ON workbooks(project_id, position, display_name) WHERE deleted_at IS NULL;
                CREATE TABLE IF NOT EXISTS files (
                    file_id TEXT PRIMARY KEY,
                    scope_id TEXT NOT NULL,
                    project_id TEXT REFERENCES projects(id),
                    owner_user_id TEXT,
                    logical_path TEXT NOT NULL,
                    logical_path_key TEXT NOT NULL,
                    media_kind TEXT NOT NULL CHECK (media_kind IN ('image','video','other')),
                    media_type TEXT NOT NULL,
                    current_revision_id TEXT,
                    version INTEGER NOT NULL DEFAULT 1,
                    deleted_at TEXT,
                    deleted_by TEXT,
                    deleted_operation_id TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_files_active_path
                    ON files(scope_id, logical_path_key) WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS idx_files_project_media
                    ON files(project_id, media_kind, deleted_at, logical_path_key);
                CREATE INDEX IF NOT EXISTS idx_files_scope_path
                    ON files(scope_id, logical_path_key);
                CREATE INDEX IF NOT EXISTS idx_files_scope_media_path
                    ON files(scope_id,media_kind,deleted_at,logical_path_key,file_id);
                CREATE TABLE IF NOT EXISTS file_revisions (
                    revision_id TEXT PRIMARY KEY,
                    file_id TEXT NOT NULL REFERENCES files(file_id),
                    revision_no INTEGER NOT NULL,
                    storage_key TEXT NOT NULL UNIQUE,
                    sha256 TEXT NOT NULL,
                    size INTEGER NOT NULL CHECK (size >= 0),
                    media_type TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    actor_name TEXT NOT NULL,
                    source TEXT NOT NULL CHECK (source IN ('wpf','sync')),
                    operation_id TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    UNIQUE(file_id, revision_no)
                );
                CREATE INDEX IF NOT EXISTS idx_file_revisions_file
                    ON file_revisions(file_id, revision_no DESC);
                CREATE TABLE IF NOT EXISTS file_thumbnails (
                    file_id TEXT NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
                    revision_id TEXT NOT NULL REFERENCES file_revisions(revision_id) ON DELETE CASCADE,
                    generator_version TEXT NOT NULL,
                    object_id TEXT NOT NULL UNIQUE,
                    storage_key TEXT NOT NULL UNIQUE,
                    sha256 TEXT NOT NULL,
                    size INTEGER NOT NULL CHECK (size >= 0),
                    media_type TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(file_id, revision_id, generator_version)
                );
                CREATE INDEX IF NOT EXISTS idx_file_thumbnails_revision
                    ON file_thumbnails(revision_id, generator_version);
                CREATE TABLE IF NOT EXISTS thumbnail_operations (
                    operation_id TEXT PRIMARY KEY,
                    file_id TEXT NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
                    revision_id TEXT NOT NULL REFERENCES file_revisions(revision_id) ON DELETE CASCADE,
                    generator_version TEXT NOT NULL,
                    object_id TEXT NOT NULL UNIQUE,
                    size INTEGER NOT NULL CHECK (size >= 0),
                    sha256 TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    receipt_json TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(file_id, revision_id, generator_version)
                );
                CREATE INDEX IF NOT EXISTS idx_thumbnail_operations_state
                    ON thumbnail_operations(state, updated_at);
                CREATE TABLE IF NOT EXISTS file_operations (
                    operation_id TEXT PRIMARY KEY,
                    action TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    actor_name TEXT NOT NULL,
                    client_id TEXT NOT NULL,
                    scope_id TEXT,
                    project_id TEXT,
                    file_id TEXT,
                    revision_id TEXT,
                    logical_path TEXT,
                    target_scope_id TEXT,
                    target_project_id TEXT,
                    target_logical_path TEXT,
                    expected_version INTEGER,
                    size INTEGER,
                    sha256 TEXT,
                    media_type TEXT,
                    source TEXT,
                    state TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    result_json TEXT,
                    error_code TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_file_ops_state ON file_operations(state, updated_at);
                CREATE TABLE IF NOT EXISTS sync_clients (
                    client_id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    last_sequence INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'active',
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS file_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_kind TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    file_id TEXT,
                    scope_id TEXT,
                    project_id TEXT,
                    action TEXT NOT NULL,
                    logical_path TEXT,
                    previous_scope_id TEXT,
                    previous_project_id TEXT,
                    previous_logical_path TEXT,
                    revision_id TEXT,
                    version INTEGER,
                    source TEXT,
                    actor_id TEXT,
                    actor_name TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_file_events_sequence ON file_events(sequence);
                CREATE INDEX IF NOT EXISTS idx_file_events_project_sequence
                    ON file_events(project_id, sequence);
                CREATE TABLE IF NOT EXISTS file_directories (
                    directory_id TEXT PRIMARY KEY,
                    scope_id TEXT NOT NULL,
                    project_id TEXT REFERENCES projects(id),
                    owner_user_id TEXT,
                    logical_path TEXT NOT NULL,
                    logical_path_key TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    deleted_at TEXT,
                    deleted_by TEXT,
                    deleted_operation_id TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_directories_active_path
                    ON file_directories(scope_id, logical_path_key) WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS idx_directories_scope_parent
                    ON file_directories(scope_id, logical_path_key, deleted_at);
            """)
            self._ensure_column(conn, "files", "deleted_operation_id", "TEXT")
            self._ensure_column(conn, "file_directories", "deleted_operation_id", "TEXT")
            self._ensure_column(conn, "file_events", "previous_scope_id", "TEXT")
            self._ensure_column(conn, "file_events", "previous_project_id", "TEXT")
            self._ensure_column(conn, "file_events", "previous_logical_path", "TEXT")
            if starting_version < 6:
                conn.execute("PRAGMA user_version=6")
        finally:
            conn.close()

    @staticmethod
    def _ensure_column(conn: sqlite3.Connection, table: str, column: str, declaration: str) -> None:
        columns = {row["name"] for row in conn.execute(f"PRAGMA table_info({table})")}
        if column not in columns:
            conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} {declaration}")

    def _signal_change(self) -> None:
        with self._events:
            self._events.notify_all()

    def projects(self, actor: Any, include_deleted: bool = False) -> list[dict[str, str]]:
        where = "" if include_deleted else "WHERE deleted_at IS NULL"
        with self._reading() as conn:
            rows = conn.execute(f"""SELECT id,display_name,deleted_at FROM projects
                {where} ORDER BY display_name COLLATE NOCASE,id""").fetchall()
        return [{"id": row["id"], "name": row["display_name"],
                 **({"deleted_at": row["deleted_at"]} if row["deleted_at"] is not None else {})}
                for row in rows]

    def create_project_record(self, name: object, actor: Any = None) -> dict[str, str]:
        if not isinstance(name, str) or not name.strip():
            raise ApiFault(422, "INVALID_NAME", "请输入项目名称。")
        name = name.strip()
        if (len(name) > 120 or any(char in name for char in "/\\:")
                or name in {".", ".."} or any(ord(char) < 32 or ord(char) == 127 for char in name)):
            raise ApiFault(422, "INVALID_NAME", "项目名称无效。")
        now = _now()
        project_id = _uid()
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        actor_id = str(user.get("id") or "")
        with self._transaction() as conn:
            if conn.execute("""SELECT 1 FROM projects WHERE display_name=? COLLATE NOCASE
                    AND deleted_at IS NULL""", (name,)).fetchone():
                raise ApiFault(409, "NAME_TAKEN", "已存在同名项目。")
            conn.execute("""INSERT INTO projects
                (id,directory_identity,display_name,directory_name,created_at,updated_at)
                VALUES(?,?,?,?,?,?)""", (project_id, "logical:" + project_id, name, project_id, now, now))
            self._create_workbook_tx(conn, project_id, "节拍表", "scene_beats", None)
            self._create_workbook_tx(conn, project_id, "资产表", "asset_review", None)
            self._file_event_tx(conn, "project", project_id, "create", project_id=project_id,
                                actor_id=actor_id, actor_name=str(user.get("username") or "") or None)
        self._signal_change()
        return {"id": project_id, "name": name}

    def register_created_project(self, name: str) -> dict[str, str]:
        return self.create_project_record(name)

    def delete_project_record(self, project_id: str, actor: Any = None) -> dict[str, Any]:
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        actor_id = str(user.get("id") or "")
        actor_name = str(user.get("username") or "") or None
        deleted_at = _now()
        marker = "project:" + project_id
        with self._transaction() as conn:
            row = conn.execute("SELECT id,display_name,deleted_at FROM projects WHERE id=?", (project_id,)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "项目不存在。")
            if row["deleted_at"] is not None:
                return {"ok": True, "id": project_id, "name": row["display_name"],
                        "deleted_at": row["deleted_at"], "already_deleted": True}
            conn.execute("UPDATE projects SET deleted_at=?,deleted_by=?,updated_at=? WHERE id=?",
                         (deleted_at, actor_name, deleted_at, project_id))
            conn.execute("""UPDATE workbooks SET deleted_at=?,deleted_by=?,version=version+1,updated_at=?
                WHERE project_id=? AND deleted_at IS NULL""",
                (deleted_at, marker, deleted_at, project_id))
            conn.execute("""UPDATE files SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                version=version+1,updated_at=? WHERE project_id=? AND deleted_at IS NULL""",
                (deleted_at, actor_name, marker, deleted_at, project_id))
            conn.execute("""UPDATE file_directories SET deleted_at=?,deleted_by=?,deleted_operation_id=?,
                version=version+1,updated_at=? WHERE project_id=? AND deleted_at IS NULL""",
                (deleted_at, actor_name, marker, deleted_at, project_id))
            for file_row in conn.execute("SELECT * FROM files WHERE project_id=? AND deleted_operation_id=?",
                                         (project_id, marker)).fetchall():
                self._file_event_tx(conn, "file", file_row["file_id"], "delete",
                    file_id=file_row["file_id"], scope_id=file_row["scope_id"],
                    project_id=project_id, logical_path=file_row["logical_path"],
                    revision_id=file_row["current_revision_id"], version=file_row["version"],
                    actor_id=actor_id, actor_name=actor_name)
            for dir_row in conn.execute("SELECT * FROM file_directories WHERE project_id=? AND deleted_operation_id=?",
                                        (project_id, marker)).fetchall():
                self._file_event_tx(conn, "directory", dir_row["directory_id"], "delete",
                    scope_id=dir_row["scope_id"], project_id=project_id,
                    logical_path=dir_row["logical_path"], version=dir_row["version"],
                    actor_id=actor_id, actor_name=actor_name)
            conn.execute("UPDATE image_assets SET active=0,relative_path='__deleted__/' || id,updated_at=? WHERE project_id=?",
                         (deleted_at, project_id))
            self._file_event_tx(conn, "project", project_id, "delete", project_id=project_id,
                                actor_id=actor_id, actor_name=actor_name)
            for workbook in conn.execute("SELECT id,version FROM workbooks WHERE project_id=? AND deleted_by=?",
                                         (project_id, marker)).fetchall():
                self._file_event_tx(conn, "workbook", workbook["id"], "delete", project_id=project_id,
                                    version=workbook["version"], actor_id=actor_id, actor_name=actor_name)
        self._signal_change()
        return {"ok": True, "id": project_id, "name": row["display_name"],
                "deleted": True, "deleted_at": deleted_at}

    def restore_project_record(self, project_id: str, actor: Any = None) -> dict[str, Any]:
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        actor_id = str(user.get("id") or "")
        actor_name = str(user.get("username") or "") or None
        now = _now()
        marker = "project:" + project_id
        with self._transaction() as conn:
            row = conn.execute("SELECT id,display_name,deleted_at FROM projects WHERE id=?", (project_id,)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "项目不存在。")
            if row["deleted_at"] is None:
                raise ApiFault(409, "NOT_DELETED", "项目尚未删除。")
            collision = conn.execute("""SELECT 1 FROM projects WHERE id<>? AND display_name=? COLLATE NOCASE
                    AND deleted_at IS NULL""", (project_id, row["display_name"])).fetchone()
            if collision:
                raise ApiFault(409, "PATH_OCCUPIED", "已有同名活动项目，无法恢复。")
            conn.execute("UPDATE projects SET deleted_at=NULL,deleted_by=NULL,updated_at=? WHERE id=?",
                         (now, project_id))
            conn.execute("""UPDATE workbooks SET deleted_at=NULL,deleted_by=NULL,version=version+1,updated_at=?
                WHERE project_id=? AND deleted_by=?""", (now, project_id, marker))
            restored_files = conn.execute("SELECT * FROM files WHERE project_id=? AND deleted_operation_id=?",
                                          (project_id, marker)).fetchall()
            restored_dirs = conn.execute("SELECT * FROM file_directories WHERE project_id=? AND deleted_operation_id=?",
                                         (project_id, marker)).fetchall()
            conn.execute("""UPDATE files SET deleted_at=NULL,deleted_by=NULL,deleted_operation_id=NULL,
                version=version+1,updated_at=? WHERE project_id=? AND deleted_operation_id=?""",
                (now, project_id, marker))
            conn.execute("""UPDATE file_directories SET deleted_at=NULL,deleted_by=NULL,deleted_operation_id=NULL,
                version=version+1,updated_at=? WHERE project_id=? AND deleted_operation_id=?""",
                (now, project_id, marker))
            for file_row in restored_files:
                self._file_event_tx(conn, "file", file_row["file_id"], "restore",
                    file_id=file_row["file_id"], scope_id=file_row["scope_id"],
                    project_id=project_id, logical_path=file_row["logical_path"],
                    revision_id=file_row["current_revision_id"], version=int(file_row["version"]) + 1,
                    actor_id=actor_id, actor_name=actor_name)
            for dir_row in restored_dirs:
                self._file_event_tx(conn, "directory", dir_row["directory_id"], "restore",
                    scope_id=dir_row["scope_id"], project_id=project_id,
                    logical_path=dir_row["logical_path"], version=int(dir_row["version"]) + 1,
                    actor_id=actor_id, actor_name=actor_name)
            image_rows = conn.execute("""SELECT ia.id,f.logical_path,f.media_type,f.version
                FROM image_assets ia JOIN files f ON f.file_id=ia.id
                WHERE ia.project_id=? AND f.project_id=? AND f.deleted_at IS NULL
                    AND f.current_revision_id IS NOT NULL AND f.media_kind='image'""",
                (project_id, project_id)).fetchall()
            for image in image_rows:
                relative_path = image["logical_path"]
                file_name = relative_path.rsplit("/", 1)[-1]
                parent = relative_path.rpartition("/")[0]
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
                conn.execute("""UPDATE image_assets SET active=1,folder_id=?,relative_path=?,
                    file_name=?,media_type=?,version=?,updated_at=? WHERE id=? AND project_id=?""",
                    (folder_id, relative_path, file_name, image["media_type"], image["version"],
                     now, image["id"], project_id))
            self._file_event_tx(conn, "project", project_id, "restore", project_id=project_id,
                                actor_id=actor_id, actor_name=actor_name)
            for workbook in conn.execute("SELECT id,version FROM workbooks WHERE project_id=? AND deleted_at IS NULL",
                                         (project_id,)).fetchall():
                self._file_event_tx(conn, "workbook", workbook["id"], "restore", project_id=project_id,
                                    version=workbook["version"], actor_id=actor_id, actor_name=actor_name)
        self._signal_change()
        return {"ok": True, "id": project_id, "name": row["display_name"], "deleted": False}

    def _project(self, project_id: str) -> sqlite3.Row:
        with self._reading() as conn:
            row = conn.execute("SELECT * FROM projects WHERE id=? AND deleted_at IS NULL", (project_id,)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "项目不存在。")
        return row

    @staticmethod
    def _asset_relative_path(category: Any, name: Any) -> str:
        category = _asset_component(category, "类别") or "角色"
        name = _asset_component(name, "名称") or "未命名"
        return f"{category}/{name}"

    @staticmethod
    def _ensure_folder_tx(conn: sqlite3.Connection, project_id: str, relative_path: str) -> str:
        row = conn.execute("SELECT id FROM asset_folders WHERE project_id=? AND relative_path=?",
                           (project_id, relative_path)).fetchone()
        if row:
            return row["id"]
        folder_id = _uid()
        conn.execute("INSERT INTO asset_folders(id,project_id,relative_path) VALUES(?,?,?)",
                     (folder_id, project_id, relative_path))
        return folder_id

    def _create_workbook_tx(self, conn: sqlite3.Connection, project_id: str, name: str,
                            template_kind: str, operation_id: str | None) -> dict[str, Any]:
        if operation_id:
            previous = conn.execute("SELECT result FROM operation_results WHERE operation_id=?",
                                    (operation_id,)).fetchone()
            if previous:
                return _dict(previous["result"])
        if template_kind not in {"scene_beats", "asset_review"}:
            raise ApiFault(422, "INVALID_TEMPLATE", "请选择一种内置表类型。")
        workbook_id = _uid()
        now = _now()
        conn.execute("""INSERT INTO workbooks
            (id,project_id,display_name,template_kind,version,position,created_at,updated_at)
            VALUES(?,?,?,?,1,(SELECT COALESCE(MAX(position),-1)+1 FROM workbooks WHERE project_id=?),?,?)""",
            (workbook_id, project_id, name, template_kind, project_id, now, now))
        sheet_id = _uid()
        sheet_name = "节拍表" if template_kind == "scene_beats" else "资产表"
        conn.execute("INSERT INTO sheets(id,workbook_id,display_name,kind,position) VALUES(?,?,?,?,0)",
                     (sheet_id, workbook_id, sheet_name, template_kind))
        if template_kind == "scene_beats":
            for i in range(1, 9):
                row_id = _uid()
                data = {
                    "scene_number": "1",
                    "beat_number": str(i),
                    "story_content": "",
                    "label_level": None,
                    "breakdown_plan": None,
                    "note": None,
                    "video": None,
                    "discarded_videos": [],
                    "scene_context": None,
                    "cast_context": None,
                }
                field_versions = {key: 1 for key in data}
                conn.execute(
                    "INSERT INTO workbook_rows(id,sheet_id,kind,position,version,field_versions,data) VALUES(?,?,?,?,1,?,?)",
                    (row_id, sheet_id, "scene_beats", i - 1, _json(field_versions), _json(data)),
                )
        payload = {"id": workbook_id, "name": name, "template_kind": template_kind,
                   "version": 1, "name_version": 1, "project_id": project_id}
        self._event_tx(conn, project_id, workbook_id, "workbook", workbook_id, ["created"])
        if operation_id:
            conn.execute("INSERT INTO operation_results VALUES(?,?,?)",
                         (operation_id, _json(payload), now))
        return payload

    @staticmethod
    def _file_payload(row: sqlite3.Row) -> dict[str, Any]:
        return {"id": row["id"], "name": row["display_name"],
                "template_kind": row["template_kind"], "version": row["version"],
                "name_version": row["name_version"],
                "project_id": row["project_id"],
                "deleted_at": row["deleted_at"] if "deleted_at" in row.keys() else None}

    def list_files(self, actor: Any, project_id: str | None = None,
                   include_deleted: bool = False) -> list[dict[str, Any]]:
        if project_id is None:
            raise ApiFault(422, "MISSING_PROJECT", "列出工作簿需要指定项目。")
        self._project(project_id)
        with self._reading() as conn:
            rows = conn.execute("SELECT * FROM workbooks WHERE project_id=? AND (? OR deleted_at IS NULL) ORDER BY position,display_name",
                                (project_id, int(include_deleted))).fetchall()
        return [self._file_payload(row) for row in rows]

    @staticmethod
    def _is_read_only_actor(actor: Any) -> bool:
        if not actor or not isinstance(actor, dict):
            return False
        role = actor.get("role") or actor.get("user", {}).get("role")
        can_write = actor.get("can_write", actor.get("user", {}).get("can_write", True))
        return role == "visitor" or can_write is False

    @staticmethod
    def _check_writable(actor: Any) -> None:
        if SqliteProjectWorkbookStore._is_read_only_actor(actor):
            raise ApiFault(403, "FORBIDDEN", "访客账户只有只读权限。")

    def create(self, actor: Any, template_kind: str, project_id: str | None = None,
               operation_id: str | None = None, name: str | None = None) -> dict[str, Any]:
        self._check_writable(actor)
        if project_id is None or operation_id is None:
            raise ApiFault(422, "MISSING_PROJECT", "创建工作簿需要项目和操作标识。")
        self._project(project_id)
        with self._transaction() as conn:
            if name:
                cleaned = self._display_name(name, "工作簿名称")
                base = cleaned if cleaned else ("节拍表" if template_kind == "scene_beats" else "资产表")
            else:
                base = "节拍表" if template_kind == "scene_beats" else "资产表"
            final_name = base
            suffix = 2
            while conn.execute("SELECT 1 FROM workbooks WHERE project_id=? AND display_name=? COLLATE NOCASE AND deleted_at IS NULL",
                               (project_id, final_name)).fetchone():
                final_name = f"{base} {suffix}"
                suffix += 1
            result = self._create_workbook_tx(conn, project_id,
                final_name, template_kind, operation_id)
        self._signal_change()
        return result

    @staticmethod
    def _display_name(value: Any, label: str, maximum: int = 128) -> str:
        if not isinstance(value, str):
            raise ApiFault(422, "INVALID_NAME", f"{label}无效。")
        name = value.strip()
        if not name or len(name) > maximum or any(ord(char) < 32 or char in "\\/" for char in name):
            raise ApiFault(422, "INVALID_NAME", f"{label}不能为空、不能包含路径分隔符，且最多 {maximum} 个字符。")
        return name

    def rename_workbook(self, actor: Any, file_id: str, expected_name_version: int,
                        display_name: Any) -> dict[str, Any]:
        self._check_writable(actor)
        name = self._display_name(display_name, "工作簿名")
        with self._transaction() as conn:
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=? AND deleted_at IS NULL", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            if int(workbook["name_version"]) != expected_name_version:
                raise ApiFault(409, "CONFLICT", "工作簿名已被其他人修改，请刷新后重试。")
            duplicate = conn.execute("SELECT 1 FROM workbooks WHERE project_id=? AND id<>? AND display_name=? COLLATE NOCASE AND deleted_at IS NULL",
                                     (workbook["project_id"], file_id, name)).fetchone()
            if duplicate:
                raise ApiFault(409, "NAME_TAKEN", "同一项目中已有同名工作簿。")
            conn.execute("UPDATE workbooks SET display_name=?,name_version=name_version+1,version=version+1,updated_at=? WHERE id=?",
                         (name, _now(), file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "workbook", file_id, ["name"])
        self._signal_change()
        with self._reading() as conn:
            return self._file_payload(conn.execute("SELECT * FROM workbooks WHERE id=? AND deleted_at IS NULL", (file_id,)).fetchone())

    def add_scene_row(self, actor: Any, file_id: str, sheet_id: str) -> dict[str, Any]:
        self._check_writable(actor)
        return self._add_row(file_id, sheet_id, "scene_beats")

    def add_asset_row(self, actor: Any, file_id: str, sheet_id: str) -> dict[str, Any]:
        self._check_writable(actor)
        return self._add_row(file_id, sheet_id, "asset_review")

    def delete_row(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                   kind: str) -> dict[str, Any]:
        self._check_writable(actor)
        if kind not in {"scene_beats", "asset_review"}:
            raise ApiFault(422, "INVALID_ROW_KIND", "不支持删除此类型的工作表行。")
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        deleted_by = str(user.get("username") or user.get("name") or "同事")[:200]
        deleted_at = _now()
        with self._transaction() as conn:
            row = conn.execute("""SELECT r.*,w.project_id FROM workbook_rows r
                JOIN sheets s ON s.id=r.sheet_id JOIN workbooks w ON w.id=s.workbook_id
                WHERE r.id=? AND s.workbook_id=? AND r.kind=? AND r.deleted_at IS NULL""",
                (row_id, file_id, kind)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "工作表行不存在或已删除。")
            if int(row["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "该行已被其他人修改，请刷新后再删除。")
            conn.execute("""UPDATE workbook_rows SET deleted_at=?,deleted_by=?,version=version+1
                WHERE id=? AND version=? AND deleted_at IS NULL""",
                (deleted_at, deleted_by, row_id, expected_version))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?",
                         (deleted_at, file_id))
            self._event_tx(conn, row["project_id"], file_id, "row", row_id, ["deleted"])
        self._signal_change()
        return {"ok": True, "id": row_id, "deleted_at": deleted_at}

    def delete_workbook(self, actor: Any, file_id: str) -> dict[str, Any]:
        self._check_writable(actor)
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        deleted_at = _now()
        deleted_by = str(user.get("username") or user.get("name") or "同事")[:200]
        actor_id = str(user.get("id") or "")
        with self._transaction() as conn:
            workbook = conn.execute("SELECT id, project_id, display_name, version FROM workbooks WHERE id=? AND deleted_at IS NULL", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            conn.execute("UPDATE workbooks SET deleted_at=?,deleted_by=?,version=version+1,updated_at=? WHERE id=?",
                         (deleted_at, deleted_by, deleted_at, file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "workbook", file_id, ["deleted"])
            self._file_event_tx(conn, "workbook", file_id, "delete", project_id=workbook["project_id"],
                                version=int(workbook["version"]) + 1, actor_id=actor_id,
                                actor_name=deleted_by)
        self._signal_change()
        return {"ok": True, "id": file_id, "deleted_at": deleted_at}

    def restore_workbook(self, actor: Any, file_id: str) -> dict[str, Any]:
        self._check_writable(actor)
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        actor_id = str(user.get("id") or "")
        actor_name = str(user.get("username") or "") or None
        now = _now()
        with self._transaction() as conn:
            workbook = conn.execute("""SELECT w.*,p.deleted_at AS project_deleted_at FROM workbooks w
                JOIN projects p ON p.id=w.project_id WHERE w.id=?""", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            if workbook["project_deleted_at"] is not None:
                raise ApiFault(409, "PROJECT_DELETED", "请先恢复所属项目。")
            if workbook["deleted_at"] is None:
                raise ApiFault(409, "NOT_DELETED", "工作簿尚未删除。")
            if conn.execute("""SELECT 1 FROM workbooks WHERE project_id=? AND id<>?
                    AND display_name=? COLLATE NOCASE AND deleted_at IS NULL""",
                    (workbook["project_id"], file_id, workbook["display_name"])).fetchone():
                raise ApiFault(409, "NAME_TAKEN", "已有同名活动工作簿，无法恢复。")
            conn.execute("UPDATE workbooks SET deleted_at=NULL,deleted_by=NULL,version=version+1,updated_at=? WHERE id=?",
                         (now, file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "workbook", file_id, ["restored"])
            self._file_event_tx(conn, "workbook", file_id, "restore", project_id=workbook["project_id"],
                                version=int(workbook["version"]) + 1, actor_id=actor_id,
                                actor_name=actor_name)
        self._signal_change()
        return {"ok": True, "id": file_id, "deleted": False}

    def assert_active_row(self, file_id: str, row_id: str, expected_kind: str | None = None) -> None:
        with self._reading() as conn:
            row = conn.execute("""SELECT 1 FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                WHERE r.id=? AND s.workbook_id=? AND r.deleted_at IS NULL
                  AND (? IS NULL OR r.kind=?)""",
                (row_id, file_id, expected_kind, expected_kind)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "工作表行不存在或已删除。")

    def _add_row(self, file_id: str, sheet_id: str, kind: str) -> dict[str, Any]:
        with self._transaction() as conn:
            sheet = conn.execute("SELECT * FROM sheets WHERE id=? AND workbook_id=? AND kind=?",
                                 (sheet_id, file_id, kind)).fetchone()
            workbook = conn.execute("SELECT project_id FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if sheet is None or workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作表不存在。")
            position = conn.execute("SELECT COALESCE(MAX(position),-1)+1 n FROM workbook_rows WHERE sheet_id=?",
                                    (sheet_id,)).fetchone()["n"]
            row_id = _uid()
            if kind == "scene_beats":
                last_row = conn.execute(
                    "SELECT data FROM workbook_rows WHERE sheet_id=? AND kind='scene_beats' AND deleted_at IS NULL ORDER BY position DESC LIMIT 1",
                    (sheet_id,)).fetchone()
                next_scene = "1"
                next_beat = str(position + 1)
                if last_row:
                    last_data = _dict(last_row["data"])
                    next_scene = str(last_data.get("scene_number") or "1")
                    try:
                        next_beat = str(int(last_data.get("beat_number") or 0) + 1)
                    except Exception:
                        next_beat = str(position + 1)
                data = {"scene_number": next_scene, "beat_number": next_beat, "story_content": "",
                        "label_level": None, "breakdown_plan": None, "note": None,
                        "video": None, "discarded_videos": [],
                        "scene_context": None, "cast_context": None}
                beat_key = _scene_beat_key(data)
                if beat_key is not None:
                    existing = conn.execute(
                        "SELECT data FROM workbook_rows WHERE sheet_id=? AND kind='scene_beats' "
                        "AND deleted_at IS NULL",
                        (sheet_id,),
                    ).fetchall()
                    if any(_scene_beat_key(_dict(item["data"])) == beat_key for item in existing):
                        raise ApiFault(409, "SCENE_BEAT_CONFLICT", "该集已存在此 beat 编号，未新增重复行。")
                field_versions = {key: 1 for key in data}
            else:
                pending_path = f"角色/新资产-{row_id.replace('-', '')[:8]}"
                data = {"category": "角色", "name": "", "appearance": None, "variant": None,
                        "folder_id": None, "folder_path": pending_path}
                field_versions = {key: 1 for key in data}
                field_versions["category_name"] = 1
            conn.execute("INSERT INTO workbook_rows(id,sheet_id,kind,position,version,field_versions,data) VALUES(?,?,?,?,1,?,?)",
                         (row_id, sheet_id, kind, position, _json(field_versions), _json(data)))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (_now(), file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "row", row_id, ["created"])
        self._signal_change()
        return self._scene_row_payload(file_id, row_id) if kind == "scene_beats" else self._asset_row_payload(file_id, row_id)

    def document(self, actor: Any, file_id: str) -> dict[str, Any]:
        with self._reading() as conn:
            workbook = conn.execute("""SELECT w.* FROM workbooks w JOIN projects p ON p.id=w.project_id
                WHERE w.id=? AND w.deleted_at IS NULL AND p.deleted_at IS NULL""", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            sheets = conn.execute("SELECT * FROM sheets WHERE workbook_id=? ORDER BY position",
                                  (file_id,)).fetchall()
            rows_by_sheet: dict[str, list[sqlite3.Row]] = {sheet["id"]: conn.execute(
                "SELECT * FROM workbook_rows WHERE sheet_id=? AND deleted_at IS NULL ORDER BY position",
                (sheet["id"],)).fetchall()
                for sheet in sheets}
            folder_paths = {row["id"]: row["relative_path"] for row in conn.execute(
                "SELECT id,relative_path FROM asset_folders WHERE project_id=?", (workbook["project_id"],))}
        self._project(workbook["project_id"])
        can_edit = not self._is_read_only_actor(actor)
        projected_sheets = []
        for sheet in sheets:
            if sheet["kind"] not in {"asset_review", "scene_beats"}:
                raise ApiFault(500, "INVALID_WORKBOOK", "工作簿包含不支持的工作表类型。")
            projected = {"id": sheet["id"], "name": sheet["display_name"],
                         "kind": sheet["kind"],
                         "asset_rows": [], "scene_rows": []}
            for stored in rows_by_sheet[sheet["id"]]:
                data = _dict(stored["data"])
                item = {"id": stored["id"], "version": stored["version"],
                        "field_versions": _dict(stored["field_versions"]), **data}
                if stored["kind"] == "asset_review":
                    folder_path = folder_paths.get(data.get("folder_id"), data.get("folder_path")
                        or self._asset_relative_path(data.get("category"), data.get("name")))
                    images = self._images_for_folder(workbook["project_id"], data.get("folder_id"), folder_path,
                        data.get("name"), data.get("variant"))
                    selection = data.get("image_selection") or {}
                    requested_ids = selection.get("candidate_image_ids")
                    if isinstance(requested_ids, list):
                        by_id = {image["id"]: image for image in images}
                        images = [by_id[image_id] for image_id in requested_ids if image_id in by_id]
                    selected_id = selection.get("selected_image_id")
                    item["asset_folder_path"] = folder_path
                    item["candidate_images"] = images
                    item["selected_image"] = next((img for img in images if img["id"] == selected_id), None) if selected_id else next(
                        (img for img in images if data.get("name") and data["name"] in img["name"]), images[0] if images else None)
                    item["can_edit"] = can_edit
                    projected["asset_rows"].append(item)
                elif stored["kind"] == "scene_beats":
                    item.setdefault("note", None)
                    item.setdefault("video", None)
                    item.setdefault("videos", [])
                    item.setdefault("discarded_videos", [])
                    if not item["videos"] and item["video"]:
                        item["videos"] = [item["video"]]
                    if item["videos"] and not item["video"]:
                        item["video"] = item["videos"][0]
                    item["can_edit"] = can_edit
                    projected["scene_rows"].append(item)
            projected_sheets.append(projected)
        return {"file": self._file_payload(workbook), "sheets": projected_sheets}

    @staticmethod
    def _event_tx(conn: sqlite3.Connection, project_id: str, workbook_id: str,
                  entity_kind: str, entity_id: str, fields: list[str]) -> int:
        cursor = conn.execute("""INSERT INTO project_events
            (project_id,workbook_id,entity_kind,entity_id,changed_fields,created_at)
            VALUES(?,?,?,?,?,?)""",
            (project_id, workbook_id, entity_kind, entity_id, _json(fields), _now()))
        return int(cursor.lastrowid)

    @staticmethod
    def _file_event_tx(conn: sqlite3.Connection, entity_kind: str, entity_id: str, action: str,
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

    def assert_active_workbook(self, file_id: str) -> None:
        with self._reading() as conn:
            row = conn.execute("""SELECT 1 FROM workbooks w JOIN projects p ON p.id=w.project_id
                WHERE w.id=? AND w.deleted_at IS NULL AND p.deleted_at IS NULL""", (file_id,)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")

    def latest_event_sequence(self, project_id: str) -> int:
        with self._reading() as conn:
            row = conn.execute("SELECT COALESCE(MAX(sequence),0) AS n FROM project_events WHERE project_id=?",
                               (project_id,)).fetchone()
            return int(row["n"])

    def events_after(self, project_id: str, sequence: int, limit: int = 128) -> list[dict[str, Any]]:
        with self._reading() as conn:
            rows = conn.execute("""SELECT sequence,project_id,workbook_id,entity_kind,entity_id,changed_fields,created_at
                FROM project_events WHERE project_id=? AND sequence>? ORDER BY sequence LIMIT ?""",
                (project_id, sequence, limit)).fetchall()
        return [{"sequence": row["sequence"], "project_id": row["project_id"],
                 "workbook_id": row["workbook_id"],
                 "entity_kind": row["entity_kind"], "entity_id": row["entity_id"],
                 "changed_fields": json.loads(row["changed_fields"]), "created_at": row["created_at"]}
                for row in rows]

    def wait_for_events(self, project_id: str, sequence: int, timeout: float = 15.0) -> list[dict[str, Any]]:
        deadline = time.monotonic() + max(0.0, timeout)
        with self._events:
            while True:
                events = self.events_after(project_id, sequence)
                if events:
                    return events
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return []
                self._events.wait(remaining)

    def _images_for_folder(self, project_id: str, folder_id: str | None,
                           relative_path: str, asset_name: str | None,
                           variant: str | None) -> list[dict[str, Any]]:
        relative = Path(relative_path)
        if relative.is_absolute() or any(part in {"", ".", ".."} for part in relative.parts):
            return []
        with self._reading() as conn:
            if folder_id:
                rows = conn.execute("""SELECT ia.*,f.current_revision_id FROM image_assets ia
                    JOIN files f ON f.file_id=ia.id WHERE ia.folder_id=? AND ia.project_id=?
                    AND ia.active=1 AND f.deleted_at IS NULL AND f.current_revision_id IS NOT NULL
                    AND f.media_kind='image'
                    ORDER BY ia.display_name COLLATE NOCASE,ia.relative_path COLLATE NOCASE""",
                    (folder_id, project_id)).fetchall()
            else:
                prefix = relative.as_posix() + "/%"
                rows = conn.execute("""SELECT ia.*,f.current_revision_id FROM image_assets ia
                    JOIN files f ON f.file_id=ia.id WHERE ia.project_id=? AND ia.relative_path LIKE ?
                    AND ia.active=1 AND f.deleted_at IS NULL AND f.current_revision_id IS NOT NULL
                    AND f.media_kind='image'
                    ORDER BY ia.display_name COLLATE NOCASE,ia.relative_path COLLATE NOCASE""",
                    (project_id, prefix)).fetchall()
        found = [{"id": row["id"], "file_id": row["id"], "name": row["display_name"],
                  "version": row["version"], "media_type": row["media_type"],
                  "current_revision_id": row["current_revision_id"], "logical_path": row["relative_path"],
                  "width": 0, "height": 0, "project_id": project_id,
                  "category": relative.parts[0] if relative.parts else None,
                  "variant": variant}
                 for row in rows if len(Path(row["relative_path"]).parts) == len(relative.parts) + 1]
        if asset_name:
            found.sort(key=lambda item: (asset_name not in item["name"], item["name"].casefold()))
        return found

    def _row_record(self, workbook_id: str, row_id: str, kind: str) -> tuple[sqlite3.Row, sqlite3.Row, sqlite3.Row]:
        with self._reading() as conn:
            row = conn.execute("SELECT * FROM workbook_rows WHERE id=?", (row_id,)).fetchone()
            if row is None or row["kind"] != kind or row["deleted_at"] is not None:
                raise ApiFault(404, "NOT_FOUND", "表格记录不存在。")
            sheet = conn.execute("SELECT * FROM sheets WHERE id=? AND workbook_id=?",
                                 (row["sheet_id"], workbook_id)).fetchone()
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=?", (workbook_id,)).fetchone()
        if sheet is None or workbook is None:
            raise ApiFault(404, "NOT_FOUND", "表格记录不存在。")
        return workbook, sheet, row

    def _update_row(self, actor: Any, file_id: str, row_id: str, expected_versions: Mapping[str, int],
                    changes: Mapping[str, Any], kind: str) -> dict[str, Any]:
        self._check_writable(actor)
        with self._transaction() as conn:
            row = conn.execute("""SELECT r.*,s.workbook_id,w.project_id
                FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                JOIN workbooks w ON w.id=s.workbook_id
                WHERE r.id=? AND s.workbook_id=? AND r.deleted_at IS NULL""",
                (row_id, file_id)).fetchone()
            if row is None or row["kind"] != kind:
                raise ApiFault(404, "NOT_FOUND", "表格记录不存在。")
            data = _dict(row["data"])
            versions = _dict(row["field_versions"])
            for field in changes:
                expected = expected_versions.get(field)
                current = int(versions.get(field, 1))
                if expected is None or expected != current:
                    raise ApiFault(409, "CONFLICT", "该字段已被其他人修改，请核对后再保存。")
            new_versions = dict(versions)
            for field, value in changes.items():
                data[field] = value
                new_versions[field] = int(versions.get(field, 1)) + 1
            new_row_version = int(row["version"]) + 1
            conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=? WHERE id=?",
                         (_json(data), _json(new_versions), new_row_version, row_id))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?",
                         (_now(), file_id))
            self._event_tx(conn, row["project_id"], file_id, "row", row_id, list(changes))
            result = {"id": row_id, "version": new_row_version, "field_versions": new_versions, **data}
        self._signal_change()
        return (self._scene_row_payload(file_id, row_id) if kind == "scene_beats"
                else self._asset_row_payload(file_id, row_id))

    def update_story_content(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                             story_content: Any) -> dict[str, Any]:
        with self._reading() as conn:
            row = conn.execute("SELECT version,field_versions FROM workbook_rows WHERE id=?", (row_id,)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "节拍记录不存在。")
        field_versions = _dict(row["field_versions"])
        expected = field_versions.get("story_content", 1) if expected_version == row["version"] else expected_version
        return self._update_row(actor, file_id, row_id, {"story_content": expected},
                                {"story_content": story_content}, "scene_beats")

    @staticmethod
    def _move_scene_video_paths_tx(conn: sqlite3.Connection, project_id: str, sheet_name: str,
                                   data: dict[str, Any], actor_id: str, actor_name: str) -> bool:
        """Update workbook paths and file catalog in one transaction, leaving object keys intact."""
        if _scene_beat_key(data) is None:
            return False
        refs: dict[str, dict[str, Any]] = {}
        groups = [data.get("videos") or [], data.get("discarded_videos") or []]
        if data.get("video"):
            groups.append([data["video"]])
        for group in groups:
            if not isinstance(group, list):
                continue
            for item in group:
                if not isinstance(item, dict) or item.get("file_id") is None:
                    # Old rows remain readable; no path is guessed for them.
                    continue
                if item.get("id") != item.get("file_id"):
                    raise ApiFault(409, "VIDEO_FILE_MISMATCH", "视频关联的文件标识无效。")
                refs.setdefault(item["file_id"], item)
        if not refs:
            return False
        scope_id = "project:" + project_id
        moved: dict[str, str] = {}
        for file_id, item in refs.items():
            file_row = conn.execute("""SELECT * FROM files WHERE file_id=? AND project_id=? AND scope_id=?
                AND current_revision_id IS NOT NULL""",
                (file_id, project_id, scope_id)).fetchone()
            if file_row is None:
                raise ApiFault(409, "VIDEO_FILE_NOT_ACTIVE", "关联视频不存在或不属于当前项目。")
            old_path = file_row["logical_path"]
            if item.get("path") and str(item["path"]).casefold() != old_path.casefold():
                raise ApiFault(409, "VIDEO_PATH_STALE", "视频逻辑路径已变化，请刷新节拍表后重试。")
            target_path = _scene_video_path(sheet_name, data, item)
            target_key = target_path.casefold()
            if target_key != file_row["logical_path_key"]:
                if conn.execute("""SELECT 1 FROM files WHERE scope_id=? AND logical_path_key=?
                    AND file_id<>? AND deleted_at IS NULL""", (scope_id, target_key, file_id)).fetchone():
                    raise ApiFault(409, "VIDEO_PATH_CONFLICT", "新 beat 位置已有其他文件，请先处理路径冲突。")
                if conn.execute("""SELECT 1 FROM file_directories WHERE scope_id=? AND logical_path_key=?
                    AND deleted_at IS NULL""", (scope_id, target_key)).fetchone():
                    raise ApiFault(409, "VIDEO_PATH_CONFLICT", "新 beat 位置已被文件夹占用。")
                SqliteProjectWorkbookStore._ensure_video_parent_directories_tx(
                    conn, project_id, target_path, actor_id, actor_name)
                next_version = int(file_row["version"]) + 1
                now = _now()
                conn.execute("""UPDATE files SET logical_path=?,logical_path_key=?,version=?,updated_at=?
                    WHERE file_id=?""", (target_path, target_key, next_version, now, file_id))
                # A tombstoned file has no bytes in local sync roots. Retarget its
                # metadata silently for the watcher, while active files emit a move so
                # clients carry their local bytes to the new beat folder.
                action = "retarget_deleted" if file_row["deleted_at"] is not None else "move"
                SqliteProjectWorkbookStore._file_event_tx(conn, "file", file_id, action,
                    file_id=file_id, scope_id=scope_id, project_id=project_id,
                    logical_path=target_path, previous_scope_id=scope_id,
                    previous_project_id=project_id, previous_logical_path=old_path,
                    revision_id=file_row["current_revision_id"], version=next_version,
                    actor_id=actor_id, actor_name=actor_name)
            moved[file_id] = target_path
        changed = False
        for key in ("videos", "discarded_videos"):
            group = data.get(key)
            if isinstance(group, list):
                replaced = []
                for item in group:
                    if isinstance(item, dict) and item.get("file_id") in moved:
                        item = dict(item)
                        new_path = moved[item["file_id"]]
                        changed = changed or item.get("path") != new_path
                        item["path"] = new_path
                    replaced.append(item)
                data[key] = replaced
        current = data.get("video")
        if isinstance(current, dict) and current.get("file_id") in moved:
            current = dict(current)
            new_path = moved[current["file_id"]]
            changed = changed or current.get("path") != new_path
            current["path"] = new_path
            data["video"] = current
        return changed

    @staticmethod
    def _ensure_video_parent_directories_tx(conn: sqlite3.Connection, project_id: str,
                                            logical_path: str, actor_id: str,
                                            actor_name: str) -> None:
        scope_id = "project:" + project_id
        parent = logical_path.rpartition("/")[0]
        if not parent:
            return
        parts = parent.split("/")
        for index in range(1, len(parts) + 1):
            current = "/".join(parts[:index])
            key = current.casefold()
            if conn.execute("""SELECT 1 FROM files WHERE scope_id=? AND logical_path_key=?
                    AND deleted_at IS NULL""", (scope_id, key)).fetchone():
                raise ApiFault(409, "VIDEO_PATH_CONFLICT", "beat 目录路径已被文件占用。")
            if conn.execute("""SELECT 1 FROM file_directories WHERE scope_id=?
                    AND logical_path_key=? AND deleted_at IS NULL""", (scope_id, key)).fetchone():
                continue
            directory_id = _uid()
            now = _now()
            conn.execute("""INSERT INTO file_directories
                (directory_id,scope_id,project_id,owner_user_id,logical_path,logical_path_key,
                 version,created_at,updated_at) VALUES(?,?,?,NULL,?,?,1,?,?)""",
                (directory_id, scope_id, project_id, current, key, now, now))
            SqliteProjectWorkbookStore._file_event_tx(conn, "directory", directory_id, "create",
                scope_id=scope_id, project_id=project_id, logical_path=current,
                actor_id=actor_id, actor_name=actor_name)

    def update_scene_row(self, actor: Any, file_id: str, row_id: str,
                         expected_version: Any, changes: Mapping[str, Any]) -> dict[str, Any]:
        self._check_writable(actor)
        changes = dict(changes)
        if "video" in changes and "video_action" not in changes:
            raise ApiFault(422, "INVALID_REQUEST", "视频必须通过明确的视频操作关联或更新。")
        if "video" in changes:
            changes["video"] = video_item_payload(changes["video"])
        with self._transaction() as conn:
            row = conn.execute("""SELECT r.*,s.workbook_id,s.display_name AS sheet_name,w.project_id
                FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                JOIN workbooks w ON w.id=s.workbook_id
                WHERE r.id=? AND s.workbook_id=? AND r.deleted_at IS NULL AND r.kind='scene_beats'""",
                (row_id, file_id)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "节拍记录不存在。")
            data = _dict(row["data"])
            versions = _dict(row["field_versions"])
            row_version = int(row["version"])

            if isinstance(expected_version, Mapping):
                if not expected_version or set(expected_version) - SCENE_ROW_VERSION_KEYS:
                    raise ApiFault(422, "INVALID_REQUEST", "字段版本无效。")
                required_versions = scene_row_update_version_fields(changes)
                if not required_versions.issubset(expected_version):
                    raise ApiFault(422, "INVALID_REQUEST", "字段版本必须覆盖本次更新涉及的所有字段。")
                for k, v in expected_version.items():
                    if (not isinstance(k, str) or type(v) is not int or not 1 <= v <= 2**63 - 1):
                        raise ApiFault(422, "INVALID_REQUEST", "字段版本无效。")
                    if int(versions.get(k, 1)) != v:
                        raise ApiFault(409, "CONFLICT", f"字段 {k} 已被其他人修改，请核对后再保存。")
            elif expected_version is not None:
                if type(expected_version) is not int or not 1 <= expected_version <= 2**63 - 1:
                    raise ApiFault(422, "INVALID_REQUEST", "节拍版本无效。")
                if expected_version != row_version:
                    raise ApiFault(409, "CONFLICT", "节拍已被其他人修改，请重新加载后再保存。")
            elif not is_pure_video_operation(changes):
                raise ApiFault(422, "INVALID_REQUEST", "节拍字段更新必须携带版本；无版本请求只允许纯视频操作。")

            changed_keys = []
            for field in ("scene_number", "beat_number", "story_content", "breakdown_plan", "label_level", "note"):
                if field in changes:
                    val = changes[field]
                    if val != data.get(field):
                        data[field] = val
                        versions[field] = int(versions.get(field, 1)) + 1
                        changed_keys.append(field)

            def has_sibling_video_conflict(name: str, video_id: Any = None) -> bool:
                target_beat = _scene_beat_key(data)
                if target_beat is None:
                    return False
                siblings = conn.execute(
                    "SELECT r.data FROM workbook_rows r "
                    "JOIN sheets s ON s.id=r.sheet_id "
                    "JOIN workbooks w ON w.id=s.workbook_id "
                    "WHERE w.project_id=? AND s.display_name=? COLLATE NOCASE "
                    "AND r.kind='scene_beats' AND r.deleted_at IS NULL AND r.id<>?",
                    (row["project_id"], row["sheet_name"], row_id),
                ).fetchall()
                for sibling in siblings:
                    sibling_data = _dict(sibling["data"])
                    if _scene_beat_key(sibling_data) != target_beat:
                        continue
                    sibling_videos = list(sibling_data.get("videos") or [])
                    current_video = sibling_data.get("video")
                    if current_video and not any(
                        item.get("id") == current_video.get("id") or item.get("name") == current_video.get("name")
                        for item in sibling_videos
                    ):
                        sibling_videos.insert(0, current_video)
                    sibling_videos.extend(sibling_data.get("discarded_videos") or [])
                    if any(
                        str(item.get("name") or "").casefold() == name.casefold()
                        or (video_id and item.get("id") == video_id)
                        for item in sibling_videos
                    ):
                        return True
                return False

            target_beat = _scene_beat_key(data)
            beat_changed = bool({"scene_number", "beat_number"} & set(changed_keys))
            video_action = changes.get("video_action")
            if beat_changed or video_action in ("attach", "add", "restore"):
                if target_beat is None and video_action in ("attach", "add", "restore"):
                    raise ApiFault(422, "INVALID_REQUEST", "请先为节拍行填写有效编号，再关联视频。")
                if target_beat is not None:
                    beat_siblings = conn.execute(
                        "SELECT data FROM workbook_rows WHERE sheet_id=? AND kind='scene_beats' "
                        "AND deleted_at IS NULL AND id<>?",
                        (row["sheet_id"], row_id),
                    ).fetchall()
                    if any(_scene_beat_key(_dict(item["data"])) == target_beat for item in beat_siblings):
                        raise ApiFault(409, "SCENE_BEAT_CONFLICT", "该集已存在此 beat 编号；每个 beat 只能有一行。")

            if beat_changed:
                actor_user = actor.get("user", {}) if isinstance(actor, dict) else {}
                paths_changed = self._move_scene_video_paths_tx(conn, row["project_id"],
                    row["sheet_name"], data, str(actor_user.get("id") or ""),
                    str(actor_user.get("username") or "同事")[:200])
                if paths_changed:
                    for field in ("video", "videos", "discarded_videos"):
                        if field in data:
                            versions[field] = int(versions.get(field, 1)) + 1
                            if field not in changed_keys:
                                changed_keys.append(field)

            if video_action in ("attach", "add"):
                new_video = changes.get("video")
                if new_video:
                    expected_path = _scene_video_path(row["sheet_name"], data, new_video)
                    if not new_video.get("path") or str(new_video["path"]).casefold() != expected_path.casefold():
                        raise ApiFault(422, "INVALID_REQUEST", "新视频路径必须位于当前集数和 beat 文件夹中。")
                    linked_file = conn.execute("""SELECT file_id,logical_path_key,media_kind FROM files
                        WHERE file_id=? AND project_id=? AND scope_id=? AND deleted_at IS NULL
                        AND current_revision_id IS NOT NULL""",
                        (new_video["file_id"], row["project_id"], "project:" + row["project_id"])).fetchone()
                    if linked_file is None or linked_file["logical_path_key"] != expected_path.casefold():
                        raise ApiFault(409, "VIDEO_FILE_MISMATCH", "视频必须关联到路径匹配的项目文件。")
                    if linked_file["media_kind"] != "video":
                        raise ApiFault(409, "VIDEO_FILE_TYPE_MISMATCH", "节拍只能关联视频文件。")
                    new_video = dict(new_video)
                    new_video["path"] = expected_path
                    changes["video"] = new_video
                    allow_multiple = changes.get("allow_multiple", video_action == "add")
                    videos = list(data.get("videos") or [])
                    if data.get("video") and not any(
                        v.get("id") == data["video"].get("id") or v.get("name") == data["video"].get("name")
                        for v in videos
                    ):
                        videos.insert(0, data["video"])

                    new_name = str(new_video.get("name") or "").casefold()
                    discarded = list(data.get("discarded_videos") or [])
                    if has_sibling_video_conflict(new_name, new_video.get("id")):
                        raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                       "同一节拍目录已存在同名视频记录，未覆盖或重复关联。")
                    if any(
                        (new_video.get("id") and v.get("id") == new_video.get("id"))
                        or str(v.get("name") or "").casefold() == new_name
                        for v in discarded
                    ):
                        raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                       "同名视频已在废片库中，请先恢复原视频或重命名新视频。")

                    same_name_active = next((v for v in videos
                                             if str(v.get("name") or "").casefold() == new_name), None)
                    if same_name_active is not None and (
                        not new_video.get("id") or same_name_active.get("id") != new_video.get("id")
                    ):
                        raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                       "该节拍已关联同名视频，未覆盖现有内容。请先处理现有关联或重命名新视频。")

                    if allow_multiple:
                        same_id_index = next((i for i, v in enumerate(videos)
                                              if new_video.get("id") and v.get("id") == new_video.get("id")), -1)
                        same_name_index = next((i for i, v in enumerate(videos)
                                                if str(v.get("name") or "").casefold() == new_name), -1)
                        if same_name_index >= 0 and same_name_index != same_id_index:
                            raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                           "该节拍已关联同名视频，未覆盖现有关联。请先处理现有关联或重命名新视频。")
                        idx = same_id_index if same_id_index >= 0 else same_name_index
                        if idx >= 0:
                            videos[idx] = new_video
                        else:
                            videos.append(new_video)
                        data["videos"] = videos
                        data["video"] = videos[0]
                        versions["videos"] = int(versions.get("videos", 1)) + 1
                        versions["video"] = int(versions.get("video", 1)) + 1
                        changed_keys.extend(["videos", "video"])
                    else:
                        old_video = data.get("video")
                        if old_video and str(old_video.get("name") or "").casefold() == new_name:
                            if not new_video.get("id") or new_video.get("id") != old_video.get("id"):
                                raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                               "该节拍已关联同名视频，未覆盖现有内容。请先处理现有关联或重命名新视频。")
                            data["video"] = new_video
                            data["videos"] = [new_video]
                            versions["video"] = int(versions.get("video", 1)) + 1
                            versions["videos"] = int(versions.get("videos", 1)) + 1
                            changed_keys.extend(["video", "videos"])
                        else:
                            if old_video:
                                old_copy = dict(old_video)
                                old_copy["discarded_at"] = _now()
                                discarded.append(old_copy)
                                data["discarded_videos"] = discarded
                                versions["discarded_videos"] = int(versions.get("discarded_videos", 1)) + 1
                                changed_keys.append("discarded_videos")
                            data["video"] = new_video
                            data["videos"] = [new_video]
                            versions["video"] = int(versions.get("video", 1)) + 1
                            versions["videos"] = int(versions.get("videos", 1)) + 1
                            changed_keys.extend(["video", "videos"])
            elif video_action == "discard":
                video_id = changes.get("video_id")
                video_name = changes.get("video_name")
                videos = list(data.get("videos") or [])
                current_video = data.get("video")
                if current_video and not any(
                    item.get("id") == current_video.get("id") or item.get("name") == current_video.get("name")
                    for item in videos
                ):
                    videos.insert(0, current_video)
                target_video = None
                if video_id or video_name:
                    target_video = (next((v for v in videos if v.get("id") == video_id), None)
                                    if video_id else
                                    next((v for v in videos if v.get("name") == video_name), None))
                    if target_video is None:
                        raise ApiFault(409, "VIDEO_NOT_FOUND", "指定的视频已不在此节拍行中，未移动其他视频。")
                if not target_video and data.get("video"):
                    target_video = data.get("video")
                if not target_video and videos:
                    target_video = videos[0]
                if target_video:
                    videos = [v for v in videos if not ((target_video.get("id") and v.get("id") == target_video.get("id")) or v.get("name") == target_video.get("name"))]
                    discarded = list(data.get("discarded_videos") or [])
                    target_copy = dict(target_video)
                    target_copy["discarded_at"] = _now()
                    discarded.append(target_copy)
                    data["discarded_videos"] = discarded
                    data["videos"] = videos
                    data["video"] = videos[0] if videos else None
                    versions["discarded_videos"] = int(versions.get("discarded_videos", 1)) + 1
                    versions["videos"] = int(versions.get("videos", 1)) + 1
                    versions["video"] = int(versions.get("video", 1)) + 1
                    changed_keys.extend(["discarded_videos", "videos", "video"])
            elif video_action == "restore":
                video_id = changes.get("video_id")
                video_name = changes.get("video_name")
                discarded = list(data.get("discarded_videos") or [])
                target_video = (next((v for v in discarded if v.get("id") == video_id), None)
                                if video_id else
                                next((v for v in discarded if v.get("name") == video_name), None))
                if target_video:
                    videos = list(data.get("videos") or [])
                    active = data.get("video")
                    if active and not any(
                        v.get("id") == active.get("id") or v.get("name") == active.get("name")
                        for v in videos
                    ):
                        videos.insert(0, active)
                    target_name = str(target_video.get("name") or "").casefold()
                    if not _is_stored_scene_video_path(row["sheet_name"], target_video):
                        raise ApiFault(422, "INVALID_REQUEST", "废片路径不属于当前集数的 NAS 节拍目录，未恢复。")
                    if has_sibling_video_conflict(target_name, target_video.get("id")):
                        raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                       "同一节拍目录已存在同名视频记录，未恢复废片。")
                    if any(
                        (target_video.get("id") and v.get("id") == target_video.get("id"))
                        or str(v.get("name") or "").casefold() == target_name
                        for v in videos
                    ):
                        raise ApiFault(409, "VIDEO_NAME_CONFLICT",
                                       "该节拍已存在同名有效视频，未恢复废片。请先处理现有关联或重命名视频。")
                    discarded = [v for v in discarded if v != target_video]
                    data["discarded_videos"] = discarded
                    restored_video = dict(target_video)
                    restored_video.pop("discarded_at", None)
                    videos.append(restored_video)
                    data["videos"] = videos
                    data["video"] = videos[0]
                    versions["video"] = int(versions.get("video", 1)) + 1
                    versions["videos"] = int(versions.get("videos", 1)) + 1
                    versions["discarded_videos"] = int(versions.get("discarded_videos", 1)) + 1
                    changed_keys.extend(["video", "videos", "discarded_videos"])
            if not changed_keys:
                return self._scene_row_payload(file_id, row_id)

            new_row_version = row_version + 1
            conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=? WHERE id=?",
                         (_json(data), _json(versions), new_row_version, row_id))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (_now(), file_id))
            self._event_tx(conn, row["project_id"], file_id, "row", row_id, changed_keys)
        self._signal_change()
        return self._scene_row_payload(file_id, row_id)

    def update_asset_row(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                         category: Any, name: Any) -> dict[str, Any]:
        self._check_writable(actor)
        category = _asset_component(category, "类别")
        name = _asset_component(name, "名称")
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        actor_id = str(user.get("id") or "")
        actor_name = str(user.get("username") or "同事")[:200]
        now = _now()
        with self._transaction() as conn:
            row = conn.execute("""SELECT r.*,s.workbook_id,w.project_id,p.directory_name
                FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                JOIN workbooks w ON w.id=s.workbook_id JOIN projects p ON p.id=w.project_id
                WHERE r.id=? AND s.workbook_id=? AND r.kind='asset_review' AND r.deleted_at IS NULL""",
                (row_id, file_id)).fetchone()
            if row is None or row["deleted_at"] is not None:
                raise ApiFault(404, "NOT_FOUND", "资产记录不存在。")
            if conn.execute("SELECT 1 FROM projects WHERE id=? AND deleted_at IS NULL", (row["project_id"],)).fetchone() is None:
                raise ApiFault(404, "NOT_FOUND", "项目不存在。")
            current_fields = _dict(row["field_versions"])
            current_identity_version = int(current_fields.get("category_name", 1))
            if current_identity_version != expected_version and int(row["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "该资产已被其他人修改，请核对后再保存。")
            data = _dict(row["data"])
            folder_id = data.get("folder_id")
            folder = conn.execute("SELECT relative_path FROM asset_folders WHERE id=?", (folder_id,)).fetchone()
            old_path = folder["relative_path"] if folder else data.get("folder_path") or self._asset_relative_path(data.get("category"), data.get("name"))
            new_path = self._asset_relative_path(category, name)
            target_exists = conn.execute("SELECT 1 FROM asset_folders WHERE project_id=? AND relative_path=? AND id<>?",
                                         (row["project_id"], new_path, folder_id or "")).fetchone()
            target_descendants = conn.execute("""SELECT 1 FROM asset_folders WHERE project_id=?
                AND substr(relative_path,1,?)=? AND id<>? LIMIT 1""",
                (row["project_id"], len(new_path) + 1, new_path + "/", folder_id or "")).fetchone()
            if target_exists or target_descendants:
                raise ApiFault(409, "ASSET_FOLDER_EXISTS", "目标类别/名称已经关联其他素材目录。")
            if folder_id:
                related_rows = [_dict(item["data"]).get("folder_id") for item in conn.execute(
                    "SELECT data FROM workbook_rows WHERE kind='asset_review'")]
                if sum(1 for value in related_rows if value == folder_id) > 1:
                    raise ApiFault(409, "ASSET_FOLDER_SHARED", "该素材目录关联了多条资产记录，请先整理重复项。")
            scope_id = "project:" + row["project_id"]
            old_key = old_path.casefold()
            new_key = new_path.casefold()
            if old_key != new_key:
                if new_key.startswith(old_key + "/"):
                    raise ApiFault(422, "INVALID_PATH", "素材目录不能移动到自身子目录。")
                prefix_key = old_key + "/"
                file_rows = conn.execute("""SELECT * FROM files WHERE scope_id=? AND project_id=?
                    AND deleted_at IS NULL AND substr(logical_path_key,1,?)=?""",
                    (scope_id, row["project_id"], len(prefix_key), prefix_key)).fetchall()
                directory_rows = conn.execute("""SELECT * FROM file_directories WHERE scope_id=? AND project_id=?
                    AND deleted_at IS NULL AND (logical_path_key=? OR substr(logical_path_key,1,?)=?)""",
                    (scope_id, row["project_id"], old_key, len(prefix_key), prefix_key)).fetchall()
                target_prefix = new_key + "/"
                occupied_file = conn.execute("""SELECT 1 FROM files WHERE scope_id=? AND project_id=?
                    AND deleted_at IS NULL AND (logical_path_key=? OR substr(logical_path_key,1,?)=?)
                    AND (logical_path_key<>? AND substr(logical_path_key,1,?)<>?) LIMIT 1""",
                    (scope_id, row["project_id"], new_key, len(target_prefix), target_prefix,
                     old_key, len(prefix_key), prefix_key)).fetchone()
                occupied_directory = conn.execute("""SELECT 1 FROM file_directories WHERE scope_id=? AND project_id=?
                    AND deleted_at IS NULL AND (logical_path_key=? OR substr(logical_path_key,1,?)=?)
                    AND (logical_path_key<>? AND substr(logical_path_key,1,?)<>?) LIMIT 1""",
                    (scope_id, row["project_id"], new_key, len(target_prefix), target_prefix,
                     old_key, len(prefix_key), prefix_key)).fetchone()
                if occupied_file or occupied_directory:
                    raise ApiFault(409, "ASSET_FOLDER_EXISTS", "目标素材目录已经包含文件或文件夹。")
                new_parts = new_path.split("/")
                for index in range(1, len(new_parts)):
                    parent_path = "/".join(new_parts[:index])
                    parent_key = parent_path.casefold()
                    if conn.execute("SELECT 1 FROM files WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL",
                                    (scope_id, parent_key)).fetchone():
                        raise ApiFault(409, "PATH_OCCUPIED", "目标上级路径已被文件占用。")
                    if conn.execute("SELECT 1 FROM file_directories WHERE scope_id=? AND logical_path_key=? AND deleted_at IS NULL",
                                    (scope_id, parent_key)).fetchone() is None:
                        parent_id = _uid()
                        conn.execute("""INSERT INTO file_directories
                            (directory_id,scope_id,project_id,logical_path,logical_path_key,version,created_at,updated_at)
                            VALUES(?,?,?,?,?,1,?,?)""",
                            (parent_id, scope_id, row["project_id"], parent_path, parent_key, now, now))
                        self._file_event_tx(conn, "directory", parent_id, "create", scope_id=scope_id,
                            project_id=row["project_id"], logical_path=parent_path,
                            actor_id=actor_id, actor_name=actor_name)
                old_parts = old_path.split("/")
                for item in file_rows:
                    item_parts = item["logical_path"].split("/")
                    if [part.casefold() for part in item_parts[:len(old_parts)]] != [part.casefold() for part in old_parts]:
                        continue
                    moved_path = new_path + "/" + "/".join(item_parts[len(old_parts):])
                    moved_key = moved_path.casefold()
                    next_version = int(item["version"]) + 1
                    conn.execute("""UPDATE files SET logical_path=?,logical_path_key=?,version=?,updated_at=?
                        WHERE file_id=?""", (moved_path, moved_key, next_version, now, item["file_id"]))
                    conn.execute("UPDATE image_assets SET relative_path=?,updated_at=? WHERE id=?",
                                 (moved_path, now, item["file_id"]))
                    self._file_event_tx(conn, "file", item["file_id"], "move", file_id=item["file_id"],
                        scope_id=scope_id, project_id=row["project_id"], logical_path=moved_path,
                        previous_scope_id=scope_id, previous_project_id=row["project_id"],
                        previous_logical_path=item["logical_path"], revision_id=item["current_revision_id"],
                        version=next_version, actor_id=actor_id, actor_name=actor_name)
                root_directory_id = next((item["directory_id"] for item in directory_rows
                                           if item["logical_path_key"] == old_key), None)
                for item in directory_rows:
                    item_parts = item["logical_path"].split("/")
                    if [part.casefold() for part in item_parts[:len(old_parts)]] != [part.casefold() for part in old_parts]:
                        continue
                    moved_path = new_path + ("/" + "/".join(item_parts[len(old_parts):]) if len(item_parts) > len(old_parts) else "")
                    next_version = int(item["version"]) + 1
                    conn.execute("""UPDATE file_directories SET logical_path=?,logical_path_key=?,version=?,updated_at=?
                        WHERE directory_id=?""", (moved_path, moved_path.casefold(), next_version, now, item["directory_id"]))
                    self._file_event_tx(conn, "directory", item["directory_id"], "move",
                        scope_id=scope_id, project_id=row["project_id"], logical_path=moved_path,
                        previous_scope_id=scope_id, previous_project_id=row["project_id"],
                        previous_logical_path=item["logical_path"], version=next_version,
                        actor_id=actor_id, actor_name=actor_name)
                folder_rows = conn.execute("""SELECT * FROM asset_folders WHERE project_id=?
                    AND (relative_path=? OR substr(relative_path,1,?)=?)""",
                    (row["project_id"], old_path, len(old_path) + 1, old_path + "/")).fetchall()
                moved_folder_ids = set()
                for item in folder_rows:
                    suffix = item["relative_path"][len(old_path):].lstrip("/")
                    moved_path = new_path + ("/" + suffix if suffix else "")
                    conn.execute("UPDATE asset_folders SET relative_path=?,version=version+1 WHERE id=?",
                                 (moved_path, item["id"]))
                    moved_folder_ids.add(item["id"])
                if folder_id is None:
                    folder_id = _uid()
                    conn.execute("INSERT INTO asset_folders(id,project_id,relative_path) VALUES(?,?,?)",
                                 (folder_id, row["project_id"], new_path))
                elif folder_id not in moved_folder_ids:
                    conn.execute("UPDATE asset_folders SET relative_path=?,version=version+1 WHERE id=?",
                                 (new_path, folder_id))
                # Keep an explicit logical directory row for empty asset folders.
                if root_directory_id is None:
                    root_directory_id = _uid()
                    conn.execute("""INSERT INTO file_directories
                        (directory_id,scope_id,project_id,logical_path,logical_path_key,version,created_at,updated_at)
                        VALUES(?,?,?,?,?,1,?,?)""",
                        (root_directory_id, scope_id, row["project_id"], new_path, new_key, now, now))
                    self._file_event_tx(conn, "directory", root_directory_id, "create",
                        scope_id=scope_id, project_id=row["project_id"], logical_path=new_path,
                        actor_id=actor_id, actor_name=actor_name)
            data.update({"category": category, "name": name, "folder_id": folder_id,
                         "folder_path": new_path})
            versions = _dict(row["field_versions"])
            versions["category_name"] = int(versions.get("category_name", 1)) + 1
            new_version = int(row["version"]) + 1
            conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=? WHERE id=?",
                         (_json(data), _json(versions), new_version, row_id))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (now, file_id))
            self._event_tx(conn, row["project_id"], file_id, "row", row_id, ["category", "name", "folder_path"])
            self._file_event_tx(conn, "workbook", file_id, "metadata", project_id=row["project_id"],
                                version=new_version, actor_id=actor_id, actor_name=actor_name)
        self._signal_change()
        return self._asset_row_payload(file_id, row_id)

    def _asset_row_payload(self, file_id: str, row_id: str) -> dict[str, Any]:
        document = self.document(None, file_id)
        for sheet in document["sheets"]:
            for row in sheet["asset_rows"]:
                if row["id"] == row_id:
                    return row
        raise ApiFault(404, "NOT_FOUND", "资产记录不存在。")

    def _scene_row_payload(self, file_id: str, row_id: str) -> dict[str, Any]:
        document = self.document(None, file_id)
        for sheet in document["sheets"]:
            for row in sheet["scene_rows"]:
                if row["id"] == row_id:
                    return row
        raise ApiFault(404, "NOT_FOUND", "节拍记录不存在。")

    def add_sheet(self, actor: Any, file_id: str, sheet_name: str) -> dict[str, Any]:
        self._check_writable(actor)
        name = sheet_name.strip()
        if not name or len(name) > 31 or any(char in '\\/?*:[]' or ord(char) < 32 for char in name):
            raise ApiFault(422, "INVALID_SHEET_NAME", "工作表名称无效。")
        with self._transaction() as conn:
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            if conn.execute("SELECT 1 FROM sheets WHERE workbook_id=? AND display_name=? COLLATE NOCASE",
                            (file_id, name)).fetchone():
                raise ApiFault(409, "SHEET_EXISTS", f"工作表「{name}」已存在。")
            position = conn.execute("SELECT COALESCE(MAX(position),-1)+1 n FROM sheets WHERE workbook_id=?",
                                    (file_id,)).fetchone()["n"]
            sheet_id = _uid()
            kind = workbook["template_kind"]
            conn.execute("INSERT INTO sheets(id,workbook_id,display_name,kind,position) VALUES(?,?,?,?,?)",
                         (sheet_id, file_id, name, kind, position))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (_now(), file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "sheet", sheet_id, ["created"])
            result = {"ok": True, "sheet_name": name, "file_name": workbook["display_name"], "id": sheet_id}
        self._signal_change()
        return result

    def list_images(self, actor: Any, project_id: str, q: Any = None, category: Any = None,
                    cursor: Any = None, page_size: int = 40) -> dict[str, Any]:
        self._project(project_id)
        if type(page_size) is not int or not 1 <= page_size <= 100:
            raise ApiFault(422, "INVALID_PAGE_SIZE", "素材列表页大小无效。")
        if cursor is not None and (not isinstance(cursor, str) or not cursor or len(cursor) > 4096):
            raise ApiFault(422, "INVALID_CURSOR", "素材列表游标无效。")
        query = """SELECT ia.*,f.current_revision_id FROM image_assets ia
            JOIN files f ON f.file_id=ia.id WHERE ia.project_id=? AND ia.active=1
            AND f.current_revision_id IS NOT NULL AND f.deleted_at IS NULL AND f.media_kind='image'"""
        params: list[Any] = [project_id]
        if category:
            query += " AND relative_path LIKE ?"
            params.append(category + "/%")
        if q:
            query += " AND (display_name LIKE ? OR file_name LIKE ?)"
            params.extend(("%" + q + "%", "%" + q + "%"))
        if cursor is not None:
            query += " AND relative_path > ?"
            params.append(cursor)
        query += " ORDER BY relative_path LIMIT ?"
        params.append(page_size + 1)
        with self._reading() as conn:
            rows = conn.execute(query, params).fetchall()
        has_next_page = len(rows) > page_size
        rows = rows[:page_size]
        return {"items": [self._image_payload(row) for row in rows],
            "next_cursor": rows[-1]["relative_path"] if has_next_page and rows else None}

    def upload_image_stream(self, actor: Any, name: str, media_type: str, source: Any,
                            length: int, project_id: str | None,
                            folder_id: str | None = None,
                            folder_path: str | None = None) -> dict[str, Any]:
        del name, media_type, source, length, project_id, folder_id, folder_path
        self._check_writable(actor)
        raise ApiFault(409, "TRANSFER_REQUIRED", "请通过统一文件传输服务上传图片。")

    @staticmethod
    def _image_payload(image: sqlite3.Row) -> dict[str, Any]:
        return {"id": image["id"], "name": image["display_name"], "version": image["version"],
                "media_type": image["media_type"], "width": 0, "height": 0,
                "project_id": image["project_id"],
                "category": image["relative_path"].split("/", 1)[0],
                "file_id": image["id"], "current_revision_id": image["current_revision_id"],
                "logical_path": image["relative_path"],
                "variant": None}

    def image_metadata(self, actor: Any, image_id: str) -> dict[str, Any]:
        with self._reading() as conn:
            image = conn.execute("""SELECT ia.*,f.current_revision_id FROM image_assets ia
                JOIN files f ON f.file_id=ia.id WHERE ia.id=? AND ia.active=1
                AND f.current_revision_id IS NOT NULL AND f.deleted_at IS NULL""", (image_id,)).fetchone()
        if image is None:
            raise ApiFault(404, "NOT_FOUND", "图片不存在。")
        return self._image_payload(image)

    def image_content_stream(self, actor: Any, asset_id: str, thumbnail: bool):
        del asset_id, thumbnail
        raise ApiFault(409, "TRANSFER_REQUIRED", "请通过统一文件传输服务下载图片。")

    def rename_image(self, actor: Any, image_id: str, expected_version: int,
                     display_name: Any) -> dict[str, Any]:
        self._check_writable(actor)
        name = self._display_name(display_name, "图片显示名称", 200)
        with self._transaction() as conn:
            image = conn.execute("""SELECT ia.* FROM image_assets ia JOIN files f ON f.file_id=ia.id
                WHERE ia.id=? AND ia.active=1 AND f.current_revision_id IS NOT NULL AND f.deleted_at IS NULL""",
                (image_id,)).fetchone()
            if image is None:
                raise ApiFault(404, "NOT_FOUND", "图片不存在。")
            if int(image["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "图片显示名称已被其他人修改，请刷新后重试。")
            conn.execute("UPDATE image_assets SET display_name=?,version=version+1,updated_at=? WHERE id=?",
                         (name, _now(), image_id))
            self._event_tx(conn, image["project_id"], "", "image", image_id, ["display_name"])
        self._signal_change()
        return self.image_metadata(actor, image_id)

    def replace_images(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       selected_image_id: str | None, candidate_image_ids: list[str]) -> dict[str, Any]:
        with self._reading() as conn:
            row = conn.execute("SELECT * FROM workbook_rows WHERE id=? AND kind='asset_review' AND deleted_at IS NULL",
                               (row_id,)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "资产记录不存在。")
            field_versions = _dict(row["field_versions"])
            current = int(field_versions.get("image_selection", 1))
            row_version = int(row["version"])
        if expected_version == row_version:
            expected_version = current
        if expected_version != current:
            raise ApiFault(409, "CONFLICT", "该资产图片已被其他人修改。")
        values = [selected_image_id, *candidate_image_ids]
        with self._reading() as conn:
            project_row = conn.execute("""SELECT project_id FROM sheets s JOIN workbook_rows r ON r.sheet_id=s.id
                JOIN workbooks w ON w.id=s.workbook_id WHERE r.id=? AND r.deleted_at IS NULL""", (row_id,)).fetchone()
            if project_row is None:
                raise ApiFault(404, "NOT_FOUND", "资产记录不存在。")
            folder_id = _dict(row["data"]).get("folder_id")
            relink_images: list[str] = []
            for image_id in filter(None, values):
                img = conn.execute("""SELECT ia.* FROM image_assets ia JOIN files f ON f.file_id=ia.id
                    WHERE ia.id=? AND ia.project_id=? AND ia.active=1
                    AND f.current_revision_id IS NOT NULL AND f.deleted_at IS NULL""",
                    (image_id, project_row["project_id"])).fetchone()
                if not img:
                    raise ApiFault(422, "INVALID_IMAGE", "图片不属于当前项目或已不存在。")
                if folder_id and img["folder_id"] != folder_id:
                    relink_images.append(image_id)
        new_selection = {"selected_image_id": selected_image_id,
                         "candidate_image_ids": candidate_image_ids}
        self._update_row(actor, file_id, row_id, {"image_selection": current},
                         {"image_selection": new_selection}, "asset_review")
        with self._transaction() as conn:
            if folder_id and relink_images:
                for img_id in relink_images:
                    conn.execute("UPDATE image_assets SET folder_id=?, updated_at=? WHERE id=?",
                                 (folder_id, _now(), img_id))
        return self._asset_row_payload(file_id, row_id)
