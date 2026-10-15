"""SQLite source of truth for collaborative project workbooks.

The database is local to the V2 service host. WPF clients access it only through the
authenticated HTTP API. XLSX files are import/export formats; they are never mutated as the
live table store.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import sqlite3
import tempfile
import threading
import time
import uuid
from base64 import urlsafe_b64encode
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator, Mapping

from . import projector
from .nas_adapter import _asset_component, _project_directories
from .projects_api import ApiFault, BinaryAsset
from .xlsx_compat import XLSX_MEDIA_TYPE, export_document

SCHEMA_VERSION = 3
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


def _legacy_file_id(project_inode: str, name: str) -> str:
    raw = f"{project_inode}\x00{name}".encode("utf-8")
    return urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def _legacy_asset_id(project_inode: str, relative_path: str) -> str:
    raw = f"{project_inode}\x00{relative_path}".encode("utf-8")
    return urlsafe_b64encode(raw).decode("ascii").rstrip("=")


class SqliteProjectWorkbookStore:
    """Implements workbook and workflow APIs over one server-local SQLite database."""

    def __init__(self, root: Path, db_path: Path | str, provisioner: Any = None) -> None:
        self._root = Path(root)
        self._db_path = Path(db_path)
        self._provisioner = provisioner
        self._events = threading.Condition()
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(self._db_path, timeout=30, isolation_level=None)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA busy_timeout=30000")
        return conn

    @contextmanager
    def _reading(self) -> Iterator[sqlite3.Connection]:
        conn = self._connect()
        try:
            yield conn
        finally:
            conn.close()

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
        with self._reading() as conn:
            conn.execute("PRAGMA journal_mode=WAL")
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
                    revision_of TEXT,
                    revision_label TEXT,
                    legacy_file_id TEXT,
                    source_name TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(project_id, source_name)
                );
                CREATE INDEX IF NOT EXISTS idx_workbooks_project ON workbooks(project_id, position, display_name);
                CREATE TABLE IF NOT EXISTS sheets (
                    id TEXT PRIMARY KEY,
                    workbook_id TEXT NOT NULL REFERENCES workbooks(id) ON DELETE CASCADE,
                    display_name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    name_version INTEGER NOT NULL DEFAULT 1,
                    position INTEGER NOT NULL,
                    legacy_sheet_id TEXT,
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
                    legacy_row_id TEXT,
                    deleted_at TEXT,
                    deleted_by TEXT,
                    UNIQUE(sheet_id, position)
                );
                CREATE INDEX IF NOT EXISTS idx_rows_sheet ON workbook_rows(sheet_id, position);
                CREATE TABLE IF NOT EXISTS generic_columns (
                    id TEXT PRIMARY KEY,
                    sheet_id TEXT NOT NULL REFERENCES sheets(id) ON DELETE CASCADE,
                    key TEXT NOT NULL,
                    label TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    UNIQUE(sheet_id, key)
                );
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
                    legacy_image_id TEXT,
                    version INTEGER NOT NULL DEFAULT 1,
                    updated_at TEXT NOT NULL,
                    UNIQUE(project_id, relative_path)
                );
                CREATE INDEX IF NOT EXISTS idx_images_project ON image_assets(project_id, active, file_name);
                CREATE TABLE IF NOT EXISTS legacy_imports (
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    source_name TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    workbook_id TEXT NOT NULL REFERENCES workbooks(id),
                    imported_at TEXT NOT NULL,
                    PRIMARY KEY(project_id, source_name)
                );
                CREATE TABLE IF NOT EXISTS operation_results (
                    operation_id TEXT PRIMARY KEY,
                    result TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS filesystem_operations (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id),
                    workbook_id TEXT NOT NULL REFERENCES workbooks(id),
                    row_id TEXT NOT NULL REFERENCES workbook_rows(id),
                    source_path TEXT NOT NULL,
                    target_path TEXT NOT NULL,
                    folder_id TEXT REFERENCES asset_folders(id),
                    category TEXT,
                    name TEXT,
                    state TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS selection_history (
                    id TEXT PRIMARY KEY,
                    workbook_id TEXT NOT NULL REFERENCES workbooks(id) ON DELETE CASCADE,
                    row_id TEXT NOT NULL REFERENCES workbook_rows(id) ON DELETE CASCADE,
                    previous_image_id TEXT,
                    image_id TEXT,
                    user_name TEXT NOT NULL,
                    reason TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_fsop_pending_row ON filesystem_operations(row_id) WHERE state='pending';
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
                PRAGMA user_version=3;
            """)
            image_columns = {row["name"] for row in conn.execute("PRAGMA table_info(image_assets)")}
            if "display_name" not in image_columns:
                conn.execute("ALTER TABLE image_assets ADD COLUMN display_name TEXT NOT NULL DEFAULT ''")
                conn.execute("UPDATE image_assets SET display_name=file_name WHERE display_name='' ")
            if "version" not in image_columns:
                conn.execute("ALTER TABLE image_assets ADD COLUMN version INTEGER NOT NULL DEFAULT 1")
            workbook_columns = {row["name"] for row in conn.execute("PRAGMA table_info(workbooks)")}
            if "name_version" not in workbook_columns:
                conn.execute("ALTER TABLE workbooks ADD COLUMN name_version INTEGER NOT NULL DEFAULT 1")
            sheet_columns = {row["name"] for row in conn.execute("PRAGMA table_info(sheets)")}
            if "name_version" not in sheet_columns:
                conn.execute("ALTER TABLE sheets ADD COLUMN name_version INTEGER NOT NULL DEFAULT 1")
            row_columns = {row["name"] for row in conn.execute("PRAGMA table_info(workbook_rows)")}
            if "deleted_at" not in row_columns:
                conn.execute("ALTER TABLE workbook_rows ADD COLUMN deleted_at TEXT")
            if "deleted_by" not in row_columns:
                conn.execute("ALTER TABLE workbook_rows ADD COLUMN deleted_by TEXT")

    def _signal_change(self) -> None:
        with self._events:
            self._events.notify_all()

    @staticmethod
    def _directory_identity(path: Path) -> str:
        stat_result = path.stat()
        return f"{stat_result.st_dev}:{stat_result.st_ino}"

    def _register_directory(self, directory: Path, create_defaults: bool = False,
                            migrate_legacy: bool = True) -> str:
        identity = self._directory_identity(directory)
        now = _now()
        with self._transaction() as conn:
            row = conn.execute("SELECT id FROM projects WHERE directory_identity=?", (identity,)).fetchone()
            if row is None:
                project_id = _uid()
                conn.execute("INSERT INTO projects VALUES(?,?,?,?,?,?)",
                    (project_id, identity, directory.name, directory.name, now, now))
            else:
                project_id = row["id"]
                conn.execute("UPDATE projects SET display_name=?,directory_name=?,updated_at=? WHERE id=?",
                    (directory.name, directory.name, now, project_id))
            count = conn.execute("SELECT COUNT(*) AS n FROM workbooks WHERE project_id=?", (project_id,)).fetchone()["n"]
            if create_defaults and not count:
                self._create_workbook_tx(conn, project_id, "节拍表", "scene_beats", None)
                self._create_workbook_tx(conn, project_id, "资产表", "asset_review", None)
        if migrate_legacy:
            self._migrate_legacy_workbooks(project_id, directory)
        return project_id

    def projects(self, actor: Any) -> list[dict[str, str]]:
        try:
            directories = _project_directories(self._root)
            items = []
            for directory in directories:
                has_legacy_workbook = any(path.is_file() and not path.is_symlink()
                    and not path.name.startswith((".", "~$")) and path.suffix.lower() == ".xlsx"
                    for path in directory.iterdir())
                project_id = self._register_directory(directory,
                    create_defaults=not has_legacy_workbook, migrate_legacy=False)
                items.append({"id": project_id, "name": directory.name})
            return items
        except ApiFault:
            raise
        except Exception as error:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。") from error

    def register_created_project(self, name: str) -> dict[str, str]:
        directory = self._root / name
        try:
            if not directory.is_dir() or directory.is_symlink():
                raise ApiFault(503, "PROJECT_NOT_LISTED", "项目已创建，但目录暂不可用。")
            project_id = self._register_directory(directory, create_defaults=True)
            return {"id": project_id, "name": directory.name}
        except ApiFault:
            raise
        except Exception as error:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "项目数据库暂不可用。") from error

    def _project(self, project_id: str) -> tuple[sqlite3.Row, Path]:
        with self._reading() as conn:
            row = conn.execute("SELECT * FROM projects WHERE id=?", (project_id,)).fetchone()
        if row is None:
            # External projects can predate the catalog. Register them by directory identity.
            for directory in _project_directories(self._root):
                if directory.name == project_id or str(directory.stat().st_ino) == project_id:
                    stable_id = self._register_directory(directory)
                    return self._project(stable_id)
            raise ApiFault(404, "NOT_FOUND", "项目不存在。")
        directory = self._root / row["directory_name"]
        if not directory.is_dir() or directory.is_symlink():
            for candidate in _project_directories(self._root):
                if self._directory_identity(candidate) == row["directory_identity"]:
                    self._register_directory(candidate)
                    return self._project(project_id)
            raise ApiFault(404, "NOT_FOUND", "项目目录不存在。")
        return row, directory

    def _migrate_legacy_workbooks(self, project_id: str, directory: Path) -> None:
        """Import each pre-existing workbook once; the source file is retained as backup."""
        try:
            inode_id = str(directory.stat().st_ino)
            for path in sorted(directory.iterdir(), key=lambda item: item.name):
                if (not path.is_file() or path.is_symlink() or path.name.startswith((".", "~$"))
                        or path.suffix.lower() != ".xlsx"):
                    continue
                with self._reading() as conn:
                    imported = conn.execute(
                        """SELECT l.fingerprint, l.workbook_id,
                                  (SELECT COUNT(*) FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id WHERE s.workbook_id=l.workbook_id) AS row_count
                           FROM legacy_imports l WHERE l.project_id=? AND l.source_name=?""",
                        (project_id, path.name)).fetchone()
                if imported is not None and imported["row_count"] > 0:
                    continue
                if imported is not None and imported["row_count"] == 0:
                    with self._transaction() as conn:
                        conn.execute("DELETE FROM legacy_imports WHERE project_id=? AND source_name=?", (project_id, path.name))
                        conn.execute("DELETE FROM project_events WHERE workbook_id=?", (imported["workbook_id"],))
                        conn.execute("DELETE FROM workbooks WHERE id=?", (imported["workbook_id"],))
                if (directory / f"~${path.name}").exists():
                    raise ApiFault(409, "LEGACY_WORKBOOK_IN_USE",
                        f"旧工作簿「{path.name}」仍在 Excel 中打开；关闭后重新加载项目以完成迁移。")
                if path.stat().st_size > projector.MAX_FILE_BYTES:
                    raise ApiFault(413, "TOO_LARGE", f"旧工作簿「{path.name}」超过导入上限。")
                source = path.read_bytes()
                fingerprint = hashlib.sha256(source).hexdigest()
                with tempfile.NamedTemporaryFile(suffix=".xlsx", delete=False) as temp:
                    temp.write(source)
                    temp_path = Path(temp.name)
                try:
                    legacy_file_id = _legacy_file_id(inode_id, path.name)
                    document = projector.project_document(temp_path, file_id=legacy_file_id,
                        name=path.name, project_id=project_id, project_dir=directory)
                    imported = self._import_document(project_id, document, source_name=path.name,
                        source_fingerprint=fingerprint, legacy_file_id=legacy_file_id)
                    if self._provisioner is not None and hasattr(self._provisioner, "archive_legacy_workbook"):
                        try:
                            self._provisioner.archive_legacy_workbook(directory.name, path.name, fingerprint)
                        except Exception as archive_error:
                            if getattr(archive_error, "code", None) == "SOURCE_CHANGED":
                                with self._transaction() as conn:
                                    conn.execute("DELETE FROM legacy_imports WHERE project_id=? AND source_name=?",
                                                 (project_id, path.name))
                                    conn.execute("DELETE FROM project_events WHERE workbook_id=?", (imported["id"],))
                                    conn.execute("DELETE FROM workbooks WHERE id=?", (imported["id"],))
                                raise ApiFault(409, "LEGACY_SOURCE_CHANGED",
                                    "旧工作簿在迁移期间发生变化；数据库导入已撤销，请重新打开项目重试。") from archive_error
                finally:
                    temp_path.unlink(missing_ok=True)
        except ApiFault:
            raise
        except Exception as error:
            raise ApiFault(503, "WORKBOOK_MIGRATION_FAILED", "旧工作簿迁移到数据库失败。") from error

    def _import_document(self, project_id: str, document: Mapping[str, Any], *,
                         source_name: str | None, source_fingerprint: str | None,
                         legacy_file_id: str | None = None, revision_of: str | None = None,
                         revision_label: str | None = None,
                         operation_id: str | None = None) -> dict[str, Any]:
        file = document["file"]
        workbook_id = _uid()
        now = _now()
        kind = file.get("template_kind") or "generic"
        with self._transaction() as conn:
            if source_name is not None:
                existing = conn.execute("""SELECT w.* FROM legacy_imports l JOIN workbooks w ON w.id=l.workbook_id
                    WHERE l.project_id=? AND l.source_name=?""", (project_id, source_name)).fetchone()
                if existing:
                    return self._file_payload(existing)
            if operation_id:
                prior = conn.execute("SELECT result FROM operation_results WHERE operation_id=?",
                                     (operation_id,)).fetchone()
                if prior:
                    return _dict(prior["result"])
            conn.execute("""INSERT INTO workbooks
                (id,project_id,display_name,template_kind,version,position,revision_of,revision_label,
                 legacy_file_id,source_name,created_at,updated_at)
                VALUES(?,?,?,?,1,(SELECT COALESCE(MAX(position),-1)+1 FROM workbooks WHERE project_id=?),?,?,?,?,?,?)""",
                (workbook_id, project_id, file.get("name") or "工作簿", kind, project_id,
                 revision_of, revision_label, legacy_file_id, source_name, now, now))
            for sheet_position, source_sheet in enumerate(document.get("sheets", [])):
                sheet_id = _uid()
                sheet_kind = source_sheet.get("kind", "generic")
                conn.execute("INSERT INTO sheets(id,workbook_id,display_name,kind,position,legacy_sheet_id) VALUES(?,?,?,?,?,?)",
                    (sheet_id, workbook_id, source_sheet.get("name") or f"工作表{sheet_position + 1}",
                     sheet_kind, sheet_position, source_sheet.get("id")))
                columns = source_sheet.get("columns") or []
                for column_position, column in enumerate(columns):
                    conn.execute("INSERT INTO generic_columns(id,sheet_id,key,label,position) VALUES(?,?,?,?,?)",
                        (_uid(), sheet_id, column["key"], column["label"], column_position))
                for row_position, source_row in enumerate(self._sheet_rows(source_sheet)):
                    legacy_row_id = source_row.get("id")
                    data = {key: value for key, value in source_row.items()
                            if key not in {"id", "version", "field_versions", "can_edit",
                                           "candidate_images", "selected_image", "asset_folder_path"}}
                    field_versions = {key: 1 for key in data}
                    if sheet_kind == "asset_review":
                        relative_path = self._asset_relative_path(data.get("category"), data.get("name"))
                        folder_id = self._ensure_folder_tx(conn, project_id, relative_path)
                        data["folder_id"] = folder_id
                        data["folder_path"] = relative_path
                        field_versions["category_name"] = 1
                    conn.execute("""INSERT INTO workbook_rows
                        (id,sheet_id,kind,position,version,field_versions,data,legacy_row_id)
                        VALUES(?,?,?,?,1,?,?,?)""",
                        (_uid(), sheet_id, sheet_kind, row_position, _json(field_versions), _json(data), legacy_row_id))
            if source_name is not None and source_fingerprint is not None:
                conn.execute("INSERT INTO legacy_imports VALUES(?,?,?,?,?)",
                    (project_id, source_name, source_fingerprint, workbook_id, now))
            self._event_tx(conn, project_id, workbook_id, "workbook", workbook_id, ["created"])
            payload = {"id": workbook_id, "name": file.get("name") or "工作簿",
                       "template_kind": kind, "version": 1, "project_id": project_id,
                       "revision_of": revision_of, "revision_label": revision_label}
            if operation_id:
                conn.execute("INSERT INTO operation_results VALUES(?,?,?)",
                             (operation_id, _json(payload), now))
        self._signal_change()
        return payload

    @staticmethod
    def _sheet_rows(sheet: Mapping[str, Any]) -> list[dict[str, Any]]:
        if sheet.get("kind") == "scene_beats":
            return list(sheet.get("scene_rows") or [])
        if sheet.get("kind") == "asset_review":
            return list(sheet.get("asset_rows") or [])
        return list(sheet.get("generic_rows") or [])

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
        payload = {"id": workbook_id, "name": name, "template_kind": template_kind,
                   "version": 1, "name_version": 1, "project_id": project_id,
                   "revision_of": None, "revision_label": None}
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
                "project_id": row["project_id"], "revision_of": row["revision_of"],
                "revision_label": row["revision_label"]}

    def list_files(self, actor: Any, project_id: str | None = None) -> list[dict[str, Any]]:
        if project_id is None:
            raise ApiFault(422, "MISSING_PROJECT", "列出工作簿需要指定项目。")
        _, directory = self._project(project_id)
        self._migrate_legacy_workbooks(project_id, directory)
        with self._reading() as conn:
            rows = conn.execute("SELECT * FROM workbooks WHERE project_id=? ORDER BY position,display_name",
                                (project_id,)).fetchall()
        return [self._file_payload(row) for row in rows]

    def create(self, actor: Any, template_kind: str, project_id: str | None = None,
               operation_id: str | None = None, name: str | None = None) -> dict[str, Any]:
        if project_id is None or operation_id is None:
            raise ApiFault(422, "MISSING_PROJECT", "创建工作簿需要项目和操作标识。")
        self._project(project_id)
        with self._transaction() as conn:
            if name:
                cleaned = self._display_name(name, "工作簿名称")
                if cleaned.lower().endswith(".xlsx"):
                    cleaned = cleaned[:-5].strip()
                base = cleaned if cleaned else ("节拍表" if template_kind == "scene_beats" else "资产表")
            else:
                base = "节拍表" if template_kind == "scene_beats" else "资产表"
            final_name = base
            suffix = 2
            while conn.execute("SELECT 1 FROM workbooks WHERE project_id=? AND display_name=? COLLATE NOCASE",
                               (project_id, final_name)).fetchone():
                final_name = f"{base} {suffix}"
                suffix += 1
            result = self._create_workbook_tx(conn, project_id,
                final_name, template_kind, operation_id)
        self._signal_change()
        return result

    def import_xlsx(self, actor: Any, name: str, data: bytes) -> dict[str, Any]:
        raise ApiFault(422, "MISSING_PROJECT", "导入工作簿需要指定项目。")

    def original(self, actor: Any, file_id: str) -> BinaryAsset:
        stream = self.original_stream(actor, file_id)
        try:
            return BinaryAsset(stream.stream.read(), stream.media_type, stream.name)
        finally:
            stream.stream.close()

    def list_images_legacy(self, actor: Any) -> list[dict[str, Any]]:
        raise ApiFault(422, "MISSING_PROJECT", "素材检索需要指定项目。")

    def upload_image(self, actor: Any, name: str, media_type: str, data: bytes) -> dict[str, Any]:
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "图片上传服务尚未接入。")

    def image_content(self, actor: Any, asset_id: str, thumbnail: bool) -> BinaryAsset:
        stream = self.image_content_stream(actor, asset_id, thumbnail)
        try:
            return BinaryAsset(stream.stream.read(), stream.media_type, stream.name)
        finally:
            stream.stream.close()

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
        name = self._display_name(display_name, "工作簿名")
        with self._transaction() as conn:
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            if int(workbook["name_version"]) != expected_name_version:
                raise ApiFault(409, "CONFLICT", "工作簿名已被其他人修改，请刷新后重试。")
            duplicate = conn.execute("SELECT 1 FROM workbooks WHERE project_id=? AND id<>? AND display_name=? COLLATE NOCASE",
                                     (workbook["project_id"], file_id, name)).fetchone()
            if duplicate:
                raise ApiFault(409, "NAME_TAKEN", "同一项目中已有同名工作簿。")
            conn.execute("UPDATE workbooks SET display_name=?,name_version=name_version+1,version=version+1,updated_at=? WHERE id=?",
                         (name, _now(), file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "workbook", file_id, ["name"])
        self._signal_change()
        with self._reading() as conn:
            return self._file_payload(conn.execute("SELECT * FROM workbooks WHERE id=?", (file_id,)).fetchone())

    def rename_sheet(self, actor: Any, file_id: str, sheet_id: str, expected_name_version: int,
                     display_name: Any) -> dict[str, Any]:
        name = self._display_name(display_name, "工作表名称", 31)
        if any(char in "\\/?*:[]'" for char in name):
            raise ApiFault(422, "INVALID_SHEET_NAME", "工作表名称不能包含 \\ / ? * : [ ] 或首尾单引号。")
        with self._transaction() as conn:
            sheet = conn.execute("SELECT * FROM sheets WHERE id=? AND workbook_id=?", (sheet_id, file_id)).fetchone()
            workbook = conn.execute("SELECT project_id FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if sheet is None or workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作表不存在。")
            if int(sheet["name_version"]) != expected_name_version:
                raise ApiFault(409, "CONFLICT", "工作表名称已被其他人修改，请刷新后重试。")
            duplicate = conn.execute("SELECT 1 FROM sheets WHERE workbook_id=? AND id<>? AND display_name=? COLLATE NOCASE",
                                     (file_id, sheet_id, name)).fetchone()
            if duplicate:
                raise ApiFault(409, "SHEET_EXISTS", "同一工作簿中已有同名工作表。")
            conn.execute("UPDATE sheets SET display_name=?,name_version=name_version+1 WHERE id=?", (name, sheet_id))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (_now(), file_id))
            self._event_tx(conn, workbook["project_id"], file_id, "sheet", sheet_id, ["name"])
        self._signal_change()
        with self._reading() as conn:
            name_version = conn.execute("SELECT name_version FROM sheets WHERE id=?", (sheet_id,)).fetchone()["name_version"]
        return {"id": sheet_id, "name": name, "name_version": name_version}

    def add_scene_row(self, actor: Any, file_id: str, sheet_id: str) -> dict[str, Any]:
        return self._add_row(file_id, sheet_id, "scene_beats")

    def add_asset_row(self, actor: Any, file_id: str, sheet_id: str) -> dict[str, Any]:
        return self._add_row(file_id, sheet_id, "asset_review")

    def delete_row(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                   kind: str) -> dict[str, Any]:
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
            if conn.execute("SELECT 1 FROM filesystem_operations WHERE row_id=? AND state='pending'",
                            (row_id,)).fetchone():
                raise ApiFault(409, "CONFLICT", "该资产目录正在移动，请稍后再删除。")
            conn.execute("""UPDATE workbook_rows SET deleted_at=?,deleted_by=?,version=version+1
                WHERE id=? AND version=? AND deleted_at IS NULL""",
                (deleted_at, deleted_by, row_id, expected_version))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?",
                         (deleted_at, file_id))
            self._event_tx(conn, row["project_id"], file_id, "row", row_id, ["deleted"])
        self._signal_change()
        return {"ok": True, "id": row_id, "deleted_at": deleted_at}

    def assert_active_row(self, file_id: str, row_id: str) -> None:
        with self._reading() as conn:
            row = conn.execute("""SELECT 1 FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                WHERE r.id=? AND s.workbook_id=? AND r.deleted_at IS NULL""", (row_id, file_id)).fetchone()
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
                data = {"scene_number": None, "beat_number": None, "story_content": "",
                        "label_level": None, "breakdown_plan": None,
                        "scene_context": None, "cast_context": None, "origins": []}
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
        self._recover_pending_moves(file_id=file_id)
        with self._reading() as conn:
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            sheets = conn.execute("SELECT * FROM sheets WHERE workbook_id=? ORDER BY position",
                                  (file_id,)).fetchall()
            columns_by_sheet: dict[str, list[dict[str, Any]]] = {}
            for sheet in sheets:
                columns_by_sheet[sheet["id"]] = [dict(row) for row in conn.execute(
                    "SELECT key,label FROM generic_columns WHERE sheet_id=? ORDER BY position", (sheet["id"],))]
            rows_by_sheet: dict[str, list[sqlite3.Row]] = {sheet["id"]: conn.execute(
                "SELECT * FROM workbook_rows WHERE sheet_id=? AND deleted_at IS NULL ORDER BY position",
                (sheet["id"],)).fetchall()
                for sheet in sheets}
            folder_paths = {row["id"]: row["relative_path"] for row in conn.execute(
                "SELECT id,relative_path FROM asset_folders WHERE project_id=?", (workbook["project_id"],))}
        _, project_dir = self._project(workbook["project_id"])
        projected_sheets = []
        for sheet in sheets:
            projected = {"id": sheet["id"], "name": sheet["display_name"],
                         "name_version": sheet["name_version"], "kind": sheet["kind"],
                         "asset_rows": [], "scene_rows": [], "columns": columns_by_sheet[sheet["id"]],
                         "generic_rows": []}
            for stored in rows_by_sheet[sheet["id"]]:
                data = _dict(stored["data"])
                item = {"id": stored["id"], "version": stored["version"],
                        "field_versions": _dict(stored["field_versions"]), **data}
                if stored["kind"] == "asset_review":
                    folder_path = folder_paths.get(data.get("folder_id"), data.get("folder_path")
                        or self._asset_relative_path(data.get("category"), data.get("name")))
                    images = self._images_for_folder(workbook["project_id"], project_dir, data.get("folder_id"), folder_path,
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
                    item["can_edit"] = True
                    projected["asset_rows"].append(item)
                elif stored["kind"] == "scene_beats":
                    item["can_edit"] = True
                    projected["scene_rows"].append(item)
                else:
                    projected["generic_rows"].append(item)
            projected_sheets.append(projected)
        return {"file": self._file_payload(workbook), "sheets": projected_sheets,
                "fidelity": "template", "notices": []}

    @staticmethod
    def _event_tx(conn: sqlite3.Connection, project_id: str, workbook_id: str,
                  entity_kind: str, entity_id: str, fields: list[str]) -> int:
        cursor = conn.execute("""INSERT INTO project_events
            (project_id,workbook_id,entity_kind,entity_id,changed_fields,created_at)
            VALUES(?,?,?,?,?,?)""",
            (project_id, workbook_id, entity_kind, entity_id, _json(fields), _now()))
        return int(cursor.lastrowid)

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

    def _images_for_folder(self, project_id: str, project_dir: Path, folder_id: str | None,
                           relative_path: str, asset_name: str | None,
                           variant: str | None) -> list[dict[str, Any]]:
        relative = Path(relative_path)
        if relative.is_absolute() or any(part in {"", ".", ".."} for part in relative.parts):
            return []
        folder = (project_dir / relative).resolve()
        try:
            folder.relative_to(project_dir.resolve())
        except ValueError:
            return []
        if not folder.is_dir():
            return []
        entries = [entry for entry in sorted(folder.iterdir(), key=lambda item: item.name)
                   if entry.is_file() and entry.suffix.lower() in IMAGE_SUFFIXES and not entry.is_symlink()]
        rel_paths = [(entry, (relative / entry.name).as_posix()) for entry in entries]
        with self._reading() as conn:
            if folder_id:
                existing_rows = conn.execute("SELECT * FROM image_assets WHERE folder_id=?", (folder_id,)).fetchall()
            else:
                prefix = relative.as_posix() + "/%"
                existing_rows = conn.execute("SELECT * FROM image_assets WHERE project_id=? AND relative_path LIKE ?",
                                             (project_id, prefix)).fetchall()
        existing = {row["relative_path"]: row for row in existing_rows}
        active_existing = {path: row for path, row in existing.items() if row["active"]}
        active_paths = {path for _, path in rel_paths}
        missing = len(active_existing) != len(active_paths) or any(path not in active_existing for path in active_paths)
        if missing:
            with self._transaction() as conn:
                for entry, rel_path in rel_paths:
                    image = existing.get(rel_path)
                    if image is None:
                        image_id = _uid()
                        media_type = "image/png" if entry.suffix.lower() == ".png" else "image/webp" if entry.suffix.lower() == ".webp" else "image/jpeg"
                        legacy_image_id = _legacy_asset_id(str(project_dir.stat().st_ino), rel_path)
                        conn.execute("""INSERT INTO image_assets
                            (id,project_id,folder_id,relative_path,file_name,display_name,media_type,active,legacy_image_id,updated_at)
                            VALUES(?,?,?,?,?,?,?,1,?,?)""",
                            (image_id, project_id, folder_id, rel_path, entry.name, entry.name,
                             media_type, legacy_image_id, _now()))
                    else:
                        conn.execute("UPDATE image_assets SET active=1,folder_id=?,file_name=?,updated_at=? WHERE id=?",
                                     (folder_id, entry.name, _now(), image["id"]))
                for old_path, image in existing.items():
                    if old_path not in active_paths:
                        conn.execute("UPDATE image_assets SET active=0,updated_at=? WHERE id=?",
                                     (_now(), image["id"]))
            with self._reading() as conn:
                if folder_id:
                    existing_rows = conn.execute("SELECT * FROM image_assets WHERE folder_id=? AND active=1",
                                                 (folder_id,)).fetchall()
                else:
                    existing_rows = conn.execute("SELECT * FROM image_assets WHERE project_id=? AND relative_path LIKE ? AND active=1",
                                                 (project_id, relative.as_posix() + "/%")).fetchall()
            existing = {row["relative_path"]: row for row in existing_rows}
        found = [{"id": existing[path]["id"], "name": existing[path]["display_name"],
                  "version": existing[path]["version"], "media_type": existing[path]["media_type"],
                  "width": 0, "height": 0, "project_id": project_id,
                  "category": relative.parts[0] if relative.parts else None,
                  "variant": variant, "revision_label": None}
                 for entry, path in rel_paths if path in existing]
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

    def _finalize_folder_move(self, operation: sqlite3.Row) -> None:
        with self._transaction() as conn:
            current_op = conn.execute("SELECT * FROM filesystem_operations WHERE id=?", (operation["id"],)).fetchone()
            if current_op is None or current_op["state"] != "pending":
                return
            row = conn.execute("SELECT * FROM workbook_rows WHERE id=?", (current_op["row_id"],)).fetchone()
            if row is None:
                conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=?", (operation["id"],))
                return
            data = _dict(row["data"])
            versions = _dict(row["field_versions"])
            folder_id = current_op["folder_id"]
            data.update({"category": current_op["category"], "name": current_op["name"],
                         "folder_id": folder_id})
            data["folder_path"] = current_op["target_path"]
            versions["category_name"] = int(versions.get("category_name", 1)) + 1
            conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=version+1 WHERE id=?",
                         (_json(data), _json(versions), row["id"]))
            conn.execute("UPDATE asset_folders SET relative_path=?,version=version+1 WHERE id=?",
                         (current_op["target_path"], folder_id))
            conn.execute("""UPDATE image_assets SET folder_id=?,relative_path=? || substr(relative_path,?),updated_at=?
                WHERE project_id=? AND substr(relative_path,1,?)=? || '/'""",
                (folder_id, current_op["target_path"], len(current_op["source_path"]) + 1,
                 _now(), current_op["project_id"], len(current_op["source_path"]) + 1, current_op["source_path"]))
            conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?",
                         (_now(), current_op["workbook_id"]))
            conn.execute("UPDATE filesystem_operations SET state='complete' WHERE id=?", (operation["id"],))
            self._event_tx(conn, current_op["project_id"], current_op["workbook_id"], "row",
                           current_op["row_id"], ["category", "name", "images"])
        self._signal_change()

    def _recover_pending_moves(self, *, file_id: str | None = None, row_id: str | None = None) -> None:
        query = "SELECT * FROM filesystem_operations WHERE state='pending'"
        params: list[Any] = []
        if file_id is not None:
            query += " AND workbook_id=?"; params.append(file_id)
        if row_id is not None:
            query += " AND row_id=?"; params.append(row_id)
        with self._reading() as conn:
            operations = conn.execute(query, params).fetchall()
        for operation in operations:
            _, project_dir = self._project(operation["project_id"])
            source = (project_dir / Path(operation["source_path"])).resolve()
            target = (project_dir / Path(operation["target_path"])).resolve()
            try:
                source.relative_to(project_dir.resolve()); target.relative_to(project_dir.resolve())
            except ValueError:
                raise ApiFault(503, "ASSET_MOVE_PENDING", "素材目录操作需要管理员检查。") from None
            if target.is_dir() and not source.exists():
                self._finalize_folder_move(operation)
            elif source.exists():
                if target.exists():
                    with self._transaction() as conn:
                        conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=? AND state='pending'",
                                     (operation["id"],))
                else:
                    age = datetime.now(timezone.utc) - datetime.fromisoformat(operation["created_at"])
                    if age.total_seconds() > 120:
                        with self._transaction() as conn:
                            conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=? AND state='pending'",
                                         (operation["id"],))
            else:
                raise ApiFault(503, "ASSET_MOVE_PENDING", "素材目录暂时无法定位，数据库记录未更改。")

    def _update_row(self, actor: Any, file_id: str, row_id: str, expected_versions: Mapping[str, int],
                    changes: Mapping[str, Any], kind: str) -> dict[str, Any]:
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

    def update_scene(self, actor: Any, file_id: str, row_id: str, expected_version: Any,
                     label_level: Any, breakdown_plan: Any) -> dict[str, Any]:
        if isinstance(expected_version, Mapping):
            versions = expected_version
        else:
            with self._reading() as conn:
                row = conn.execute("SELECT version,field_versions FROM workbook_rows WHERE id=?", (row_id,)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "节拍记录不存在。")
            if expected_version != row["version"]:
                raise ApiFault(409, "CONFLICT", "节拍已被其他人修改，请重新加载后再保存。")
            current_fields = _dict(row["field_versions"])
            versions = {"label_level": current_fields.get("label_level", 1),
                        "breakdown_plan": current_fields.get("breakdown_plan", 1)}
        return self._update_row(actor, file_id, row_id, versions,
            {"label_level": label_level, "breakdown_plan": breakdown_plan}, "scene_beats")

    def update_asset_row(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                         category: Any, name: Any) -> dict[str, Any]:
        self._recover_pending_moves(row_id=row_id)
        category = _asset_component(category, "类别")
        name = _asset_component(name, "名称")
        with self._transaction() as conn:
            row = conn.execute("""SELECT r.*,s.workbook_id,w.project_id,p.directory_name
                FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                JOIN workbooks w ON w.id=s.workbook_id JOIN projects p ON p.id=w.project_id
                WHERE r.id=? AND s.workbook_id=? AND r.kind='asset_review' AND r.deleted_at IS NULL""",
                (row_id, file_id)).fetchone()
            if row is None:
                raise ApiFault(404, "NOT_FOUND", "资产记录不存在。")
            current_fields = _dict(row["field_versions"])
            current_identity_version = int(current_fields.get("category_name", 1))
            if current_identity_version != expected_version and int(row["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "该资产已被其他人修改，请核对后再保存。")
            pending = conn.execute("SELECT 1 FROM filesystem_operations WHERE row_id=? AND state='pending'",
                                   (row_id,)).fetchone()
            if pending:
                raise ApiFault(409, "CONFLICT", "该资产目录正在移动，请稍后重试。")
            data = _dict(row["data"])
            folder_id = data.get("folder_id")
            folder = conn.execute("SELECT relative_path FROM asset_folders WHERE id=?", (folder_id,)).fetchone()
            old_path = folder["relative_path"] if folder else data.get("folder_path") or self._asset_relative_path(data.get("category"), data.get("name"))
            new_path = self._asset_relative_path(category, name)
            target_exists = conn.execute("SELECT 1 FROM asset_folders WHERE project_id=? AND relative_path=? AND id<>?",
                                         (row["project_id"], new_path, folder_id or "")).fetchone()
            if target_exists:
                raise ApiFault(409, "ASSET_FOLDER_EXISTS", "目标类别/名称已经关联其他素材目录。")
            if folder_id:
                related_rows = [_dict(item["data"]).get("folder_id") for item in conn.execute(
                    "SELECT data FROM workbook_rows WHERE kind='asset_review'")]
                if sum(1 for value in related_rows if value == folder_id) > 1:
                    raise ApiFault(409, "ASSET_FOLDER_SHARED", "该素材目录关联了多条资产记录，请先整理重复项。")
            if old_path == new_path:
                data.update({"category": category, "name": name})
                versions = _dict(row["field_versions"])
                versions["category_name"] = int(versions.get("category_name", 1)) + 1
                new_version = int(row["version"]) + 1
                conn.execute("UPDATE workbook_rows SET data=?,field_versions=?,version=? WHERE id=?",
                             (_json(data), _json(versions), new_version, row_id))
                conn.execute("UPDATE workbooks SET version=version+1,updated_at=? WHERE id=?", (_now(), file_id))
                self._event_tx(conn, row["project_id"], file_id, "row", row_id, ["category", "name"])
                move_id = None
            else:
                move_id = _uid()
                conn.execute("INSERT OR IGNORE INTO asset_folders(id,project_id,relative_path) VALUES(?,?,?)",
                             (folder_id or _uid(), row["project_id"], old_path))
                if folder_id is None:
                    folder_id = conn.execute("SELECT id FROM asset_folders WHERE project_id=? AND relative_path=?",
                                             (row["project_id"], old_path)).fetchone()["id"]
                conn.execute("INSERT INTO filesystem_operations(id,project_id,workbook_id,row_id,source_path,target_path,folder_id,category,name,state,created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                             (move_id, row["project_id"], file_id, row_id, old_path, new_path,
                              folder_id, category, name, "pending", _now()))
        if move_id is not None:
            _, project_dir = self._project(row["project_id"])
            source_dir = project_dir / Path(old_path)
            target_dir = project_dir / Path(new_path)
            if target_dir.exists() and old_path != new_path:
                with self._transaction() as conn:
                    conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=?", (move_id,))
                raise ApiFault(409, "ASSET_FOLDER_EXISTS", "目标素材目录已存在，未覆盖任何文件。")
            if self._provisioner is None:
                with self._transaction() as conn:
                    conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=?", (move_id,))
                raise ApiFault(503, "STORAGE_UNAVAILABLE", "资产目录移动服务尚未配置。")
            try:
                self._provisioner.move_asset_folder(project_dir.name, old_path, new_path)
            except Exception as error:
                code = getattr(error, "code", None)
                if code in {"ASSET_FOLDER_EXISTS", "ASSET_MOVE_FAILED", "INVALID_ASSET_PATH"}:
                    with self._transaction() as conn:
                        conn.execute("UPDATE filesystem_operations SET state='failed' WHERE id=? AND state='pending'",
                                     (move_id,))
                    raise ApiFault(409 if code == "ASSET_FOLDER_EXISTS" else 503,
                        code, str(error)) from error
                # A timeout can happen after the helper moved the directory; inspect both paths
                # before deciding whether to report success or keep the operation pending.
                self._recover_pending_moves(row_id=row_id)
                with self._reading() as conn:
                    state = conn.execute("SELECT state FROM filesystem_operations WHERE id=?", (move_id,)).fetchone()
                if state and state["state"] == "complete":
                    return self._asset_row_payload(file_id, row_id)
                raise ApiFault(503, "ASSET_MOVE_PENDING", "资产目录状态待确认，请重新加载后重试。") from error
            self._recover_pending_moves(row_id=row_id)
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

    def import_xlsx_stream(self, actor: Any, name: str, source: Any, length: int,
                           project_id: str | None, operation_id: str | None,
                           revision_of: str | None) -> dict[str, Any]:
        if not project_id:
            raise ApiFault(422, "MISSING_PROJECT", "导入需要指定项目。")
        _, project_dir = self._project(project_id)
        if not name.lower().endswith(".xlsx") or Path(name).name != name:
            raise ApiFault(415, "UNSUPPORTED_TYPE", "项目只接收 .xlsx 文件。")
        if revision_of:
            with self._reading() as conn:
                exists = conn.execute("SELECT 1 FROM workbooks WHERE id=? AND project_id=?",
                                      (revision_of, project_id)).fetchone()
            if not exists:
                raise ApiFault(404, "NOT_FOUND", "原工作簿不存在。")
        data = source.read(length) if length >= 0 else source.read()
        if not data:
            raise ApiFault(422, "INVALID_WORKBOOK", "工作簿为空。")
        with tempfile.NamedTemporaryFile(suffix=".xlsx", delete=False) as temp:
            temp.write(data)
            temp_path = Path(temp.name)
        _, project_dir = self._project(project_id) if project_id else (None, None)
        try:
            projected = projector.project_document(temp_path, file_id="import", name=name,
                project_id=project_id, project_dir=project_dir)
        except Exception as error:
            raise ApiFault(422, "INVALID_WORKBOOK", "无法读取这个工作簿。") from error
        finally:
            temp_path.unlink(missing_ok=True)
        fingerprint = hashlib.sha256(data).hexdigest()
        label = name
        return self._import_document(project_id, projected, source_name=None,
            source_fingerprint=None, revision_of=revision_of,
            revision_label=label if revision_of else None, operation_id=operation_id)

    def list_revisions(self, actor: Any, file_id: str) -> list[dict[str, Any]]:
        with self._reading() as conn:
            exists = conn.execute("SELECT 1 FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if exists is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            rows = conn.execute("SELECT * FROM workbooks WHERE revision_of=? ORDER BY created_at DESC",
                                (file_id,)).fetchall()
        return [self._file_payload(row) for row in rows]

    def revisions(self, actor: Any, file_id: str) -> list[dict[str, Any]]:
        return self.list_revisions(actor, file_id)

    def _export_snapshot(self, file_id: str, expected_version: int | None = None) -> dict[str, Any]:
        """Read all exportable fields from one SQLite snapshot."""
        with self._reading() as conn:
            conn.execute("BEGIN")
            workbook = conn.execute("SELECT * FROM workbooks WHERE id=?", (file_id,)).fetchone()
            if workbook is None:
                raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
            if expected_version is not None and int(workbook["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "工作簿已更新，请重新加载后再导出。")
            sheets = conn.execute("SELECT * FROM sheets WHERE workbook_id=? ORDER BY position",
                                  (file_id,)).fetchall()
            result_sheets = []
            for sheet in sheets:
                columns = [dict(row) for row in conn.execute(
                    "SELECT key,label FROM generic_columns WHERE sheet_id=? ORDER BY position",
                    (sheet["id"],))]
                stored_rows = conn.execute("SELECT * FROM workbook_rows WHERE sheet_id=? AND deleted_at IS NULL ORDER BY position",
                                           (sheet["id"],)).fetchall()
                row_data = [{**_dict(row["data"]), "id": row["id"]} for row in stored_rows]
                result_sheets.append({
                    "id": sheet["id"], "name": sheet["display_name"], "kind": sheet["kind"],
                    "columns": columns,
                    "scene_rows": row_data if sheet["kind"] == "scene_beats" else [],
                    "asset_rows": row_data if sheet["kind"] == "asset_review" else [],
                    "generic_rows": row_data if sheet["kind"] == "generic" else [],
                })
            return {"file": self._file_payload(workbook), "sheets": result_sheets}

    def _export_xlsx(self, file_id: str, expected_version: int | None = None) -> bytes:
        return export_document(self._export_snapshot(file_id, expected_version))

    def original_stream(self, actor: Any, file_id: str):
        from .workflow_services import StreamAsset
        with self._reading() as conn:
            row = conn.execute("SELECT project_id,source_name FROM workbooks WHERE id=?", (file_id,)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")
        source_name = row["source_name"]
        if not source_name or Path(source_name).name != source_name:
            raise ApiFault(404, "ORIGINAL_UNAVAILABLE", "此工作簿没有保留原始 XLSX；请导出当前快照。")
        with self._reading() as conn:
            legacy = conn.execute("SELECT fingerprint FROM legacy_imports WHERE project_id=? AND source_name=?",
                                  (row["project_id"], source_name)).fetchone()
        if legacy is None:
            raise ApiFault(404, "ORIGINAL_UNAVAILABLE", "此工作簿没有可验证的原始 XLSX；请导出当前快照。")
        _, project_dir = self._project(row["project_id"])
        fingerprint = legacy["fingerprint"]
        archived_dir = project_dir / ".legacy-workbooks"
        candidates = [archived_dir / source_name,
                      archived_dir / f"{Path(source_name).stem}.{fingerprint[:12]}{Path(source_name).suffix}",
                      project_dir / source_name]
        original_changed = False
        for target in candidates:
            if not target.is_file() or target.is_symlink():
                continue
            try:
                target.resolve().relative_to(project_dir.resolve())
            except ValueError:
                continue
            source = target.open("rb")
            try:
                size = os.fstat(source.fileno()).st_size
                if size > projector.MAX_FILE_BYTES:
                    raise ApiFault(413, "TOO_LARGE", "原始工作簿超过下载上限。")
                digest = hashlib.sha256()
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(chunk)
                if digest.hexdigest() != fingerprint:
                    original_changed = True
                    source.close()
                    continue
                source.seek(0)
                return StreamAsset(source, size, XLSX_MEDIA_TYPE, source_name)
            except Exception:
                source.close()
                raise
        if original_changed:
            raise ApiFault(409, "ORIGINAL_CHANGED", "归档的原始工作簿与导入版本不一致。")
        raise ApiFault(404, "ORIGINAL_UNAVAILABLE", "未找到归档的原始工作簿；请导出当前快照。")

    def export_current(self, actor: Any, file_id: str, expected_version: int):
        from .workflow_services import StreamAsset
        snapshot = self._export_snapshot(file_id, expected_version)
        name = snapshot["file"]["name"]
        if not name.lower().endswith(".xlsx"):
            name += ".xlsx"
        data = export_document(snapshot)
        return StreamAsset(io.BytesIO(data), len(data),
            XLSX_MEDIA_TYPE, name)

    def list_images(self, actor: Any, project_id: str | None = None, q: Any = None, category: Any = None,
                    cursor: Any = None, page_size: int = 40) -> dict[str, Any] | list[dict[str, Any]]:
        if project_id is None:
            return self.list_images_legacy(actor)
        _, project_dir = self._project(project_id)
        for category_dir in sorted(project_dir.iterdir(), key=lambda item: item.name):
            if not category_dir.is_dir() or category_dir.is_symlink() or category_dir.name.startswith("."):
                continue
            if category and category_dir.name != category:
                continue
            for asset_dir in sorted(category_dir.iterdir(), key=lambda item: item.name):
                if not asset_dir.is_dir() or asset_dir.is_symlink() or asset_dir.name.startswith("."):
                    continue
                relative = f"{category_dir.name}/{asset_dir.name}"
                with self._transaction() as conn:
                    folder_id = self._ensure_folder_tx(conn, project_id, relative)
                self._images_for_folder(project_id, project_dir, folder_id, relative, None, asset_dir.name)
        query = "SELECT * FROM image_assets WHERE project_id=? AND active=1"
        params: list[Any] = [project_id]
        if category:
            query += " AND relative_path LIKE ?"
            params.append(category + "/%")
        if q:
            query += " AND (display_name LIKE ? OR file_name LIKE ?)"
            params.extend(("%" + q + "%", "%" + q + "%"))
        query += " ORDER BY relative_path LIMIT ?"
        params.append(page_size)
        with self._reading() as conn:
            rows = conn.execute(query, params).fetchall()
        return {"items": [{"id": row["id"], "name": row["display_name"], "version": row["version"],
            "media_type": row["media_type"], "width": 0, "height": 0,
            "project_id": project_id, "category": row["relative_path"].split("/", 1)[0],
            "variant": None, "revision_label": None} for row in rows], "next_cursor": None}

    def upload_image_stream(self, actor: Any, name: str, media_type: str, source: Any,
                            length: int, project_id: str | None) -> dict[str, Any]:
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "图片上传服务尚未接入。")

    @staticmethod
    def _image_payload(image: sqlite3.Row) -> dict[str, Any]:
        return {"id": image["id"], "name": image["display_name"], "version": image["version"],
                "media_type": image["media_type"], "width": 0, "height": 0,
                "project_id": image["project_id"],
                "category": image["relative_path"].split("/", 1)[0],
                "variant": None, "revision_label": None}

    def image_metadata(self, actor: Any, image_id: str) -> dict[str, Any]:
        with self._reading() as conn:
            image = conn.execute("SELECT * FROM image_assets WHERE id=? AND active=1", (image_id,)).fetchone()
        if image is None:
            raise ApiFault(404, "NOT_FOUND", "图片不存在。")
        return self._image_payload(image)

    def image_content_stream(self, actor: Any, asset_id: str, thumbnail: bool):
        from .workflow_services import StreamAsset
        with self._reading() as conn:
            image = conn.execute("SELECT * FROM image_assets WHERE id=? AND active=1", (asset_id,)).fetchone()
            if image is None:
                raise ApiFault(404, "NOT_FOUND", "素材不存在。")
        _, project_dir = self._project(image["project_id"])
        target = (project_dir / Path(image["relative_path"])).resolve()
        try:
            target.relative_to(project_dir.resolve())
        except ValueError:
            raise ApiFault(404, "NOT_FOUND", "素材不存在。") from None
        if not target.is_file() or target.is_symlink():
            raise ApiFault(404, "NOT_FOUND", "素材文件不存在。")
        return StreamAsset(open(target, "rb"), target.stat().st_size,
                           image["media_type"], image["file_name"])

    def rename_image(self, actor: Any, image_id: str, expected_version: int,
                     display_name: Any) -> dict[str, Any]:
        name = self._display_name(display_name, "图片显示名称", 200)
        with self._transaction() as conn:
            image = conn.execute("SELECT * FROM image_assets WHERE id=? AND active=1", (image_id,)).fetchone()
            if image is None:
                raise ApiFault(404, "NOT_FOUND", "图片不存在。")
            if int(image["version"]) != expected_version:
                raise ApiFault(409, "CONFLICT", "图片显示名称已被其他人修改，请刷新后重试。")
            conn.execute("UPDATE image_assets SET display_name=?,version=version+1,updated_at=? WHERE id=?",
                         (name, _now(), image_id))
            self._event_tx(conn, image["project_id"], "", "image", image_id, ["display_name"])
        self._signal_change()
        return self.image_metadata(actor, image_id)

    def update_origins(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       origins: list[dict[str, str]]) -> dict[str, Any]:
        with self._reading() as conn:
            row = conn.execute("SELECT * FROM workbook_rows WHERE id=? AND deleted_at IS NULL", (row_id,)).fetchone()
        if row is None:
            raise ApiFault(404, "NOT_FOUND", "节拍记录不存在。")
        fields = _dict(row["field_versions"])
        expected = fields.get("origins", 1) if row["version"] == expected_version else expected_version
        if expected != expected_version:
            raise ApiFault(409, "CONFLICT", "节拍来源已变更，请重新加载后再保存。")
        for origin in origins:
            with self._reading() as conn:
                exists = conn.execute("""SELECT 1 FROM workbook_rows r JOIN sheets s ON s.id=r.sheet_id
                    WHERE r.id=? AND s.workbook_id=? AND r.kind='scene_beats' AND r.deleted_at IS NULL""",
                    (origin["row_id"], origin["file_id"])).fetchone()
            if not exists:
                raise ApiFault(422, "INVALID_ORIGIN", "来源必须指向现存节拍行。")
        result = self._update_row(actor, file_id, row_id, {"origins": expected_version},
                                  {"origins": origins}, "scene_beats")
        return result

    def replace_images(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       selected_image_id: str | None, candidate_image_ids: list[str],
                       selection_reason: str | None = None) -> dict[str, Any]:
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
            if conn.execute("SELECT 1 FROM filesystem_operations WHERE row_id=? AND state='pending'", (row_id,)).fetchone():
                raise ApiFault(409, "CONFLICT", "该资产目录正在移动，请稍后重试。")
            for image_id in filter(None, values):
                if not conn.execute("SELECT 1 FROM image_assets WHERE id=? AND project_id=? AND active=1 AND folder_id=?",
                                    (image_id, project_row["project_id"], folder_id)).fetchone():
                    raise ApiFault(422, "INVALID_IMAGE", "图片不属于当前项目或已不存在。")
        old_selection = _dict(row["data"]).get("image_selection") or {}
        new_selection = {"selected_image_id": selected_image_id,
                         "candidate_image_ids": candidate_image_ids,
                         "selection_reason": selection_reason}
        self._update_row(actor, file_id, row_id, {"image_selection": current},
                         {"image_selection": new_selection}, "asset_review")
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        with self._transaction() as conn:
            conn.execute("""INSERT INTO selection_history
                (id,workbook_id,row_id,previous_image_id,image_id,user_name,reason,created_at)
                VALUES(?,?,?,?,?,?,?,?)""",
                (_uid(), file_id, row_id, old_selection.get("selected_image_id"), selected_image_id,
                 str(user.get("username") or user.get("name") or "同事"), selection_reason, _now()))
        return self._asset_row_payload(file_id, row_id)

    def selection_history(self, actor: Any, file_id: str, row_id: str) -> list[dict[str, Any]]:
        self.assert_active_row(file_id, row_id)
        with self._reading() as conn:
            rows = conn.execute("""SELECT * FROM selection_history WHERE workbook_id=? AND row_id=?
                ORDER BY created_at""", (file_id, row_id)).fetchall()
        return [{"id": row["id"], "previous_image_id": row["previous_image_id"],
                 "image_id": row["image_id"], "user_name": row["user_name"],
                 "created_at": row["created_at"], "reason": row["reason"]} for row in rows]

    def legacy_review_ids(self, file_id: str, row_id: str, image_id: str | None = None) -> tuple[str, str, str | None]:
        with self._reading() as conn:
            workbook = conn.execute("SELECT legacy_file_id FROM workbooks WHERE id=?", (file_id,)).fetchone()
            row = conn.execute("SELECT legacy_row_id FROM workbook_rows WHERE id=?", (row_id,)).fetchone()
            image = conn.execute("SELECT legacy_image_id FROM image_assets WHERE id=?", (image_id,)).fetchone() if image_id else None
        return ((workbook["legacy_file_id"] if workbook and workbook["legacy_file_id"] else file_id),
                (row["legacy_row_id"] if row and row["legacy_row_id"] else row_id),
                (image["legacy_image_id"] if image and image["legacy_image_id"] else image_id))

    def stable_image_id(self, legacy_image_id: str | None) -> str | None:
        if legacy_image_id is None:
            return None
        with self._reading() as conn:
            row = conn.execute("SELECT id FROM image_assets WHERE legacy_image_id=?", (legacy_image_id,)).fetchone()
        return row["id"] if row else legacy_image_id


class StableReviewServices:
    """Keeps existing review threads attached while workbook IDs migrate to stable UUIDs."""

    def __init__(self, inner: Any, store: SqliteProjectWorkbookStore) -> None:
        self._inner = inner
        self._store = store

    def _ids(self, file_id: str, row_id: str, image_id: str | None = None):
        self._store.assert_active_row(file_id, row_id)
        return self._store.legacy_review_ids(file_id, row_id, image_id)

    def summaries(self, actor: Any, file_id: str, row_id: str):
        legacy_file, legacy_row, _ = self._ids(file_id, row_id)
        rows = self._inner.summaries(actor, legacy_file, legacy_row)
        return [{**row, "image_id": self._store.stable_image_id(row.get("image_id"))} for row in rows]

    def thread(self, actor: Any, file_id: str, row_id: str, image_id: str | None):
        legacy_file, legacy_row, legacy_image = self._ids(file_id, row_id, image_id)
        result = self._inner.thread(actor, legacy_file, legacy_row, legacy_image)
        result["image_id"] = self._store.stable_image_id(result.get("image_id"))
        return result

    def vote(self, actor: Any, file_id: str, row_id: str, image_id: str, verdict: str | None):
        legacy_file, legacy_row, legacy_image = self._ids(file_id, row_id, image_id)
        return self._inner.vote(actor, legacy_file, legacy_row, legacy_image or image_id, verdict)

    def add_comment(self, actor: Any, file_id: str, row_id: str, image_id: str | None, text: str):
        legacy_file, legacy_row, legacy_image = self._ids(file_id, row_id, image_id)
        return self._inner.add_comment(actor, legacy_file, legacy_row, legacy_image, text)

    def edit_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                     expected_version: int, text: str):
        legacy_file, legacy_row, _ = self._ids(file_id, row_id)
        return self._inner.edit_comment(actor, legacy_file, legacy_row, comment_id, expected_version, text)

    def delete_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str, expected_version: int):
        legacy_file, legacy_row, _ = self._ids(file_id, row_id)
        return self._inner.delete_comment(actor, legacy_file, legacy_row, comment_id, expected_version)

    def resolve_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                        expected_version: int, resolved: bool, resolution_note: str | None):
        legacy_file, legacy_row, _ = self._ids(file_id, row_id)
        return self._inner.resolve_comment(actor, legacy_file, legacy_row, comment_id,
                                           expected_version, resolved, resolution_note)
