"""The host half of the workbook contract: authentication, workbook reads and row edits.

The vendored module declares routes and JSON shapes and asks the host for everything else
through protocols. These are those protocols, backed by the project directory on the NAS.

**Scope, stated plainly.** This serves project/workbook reads and versioned edits to beat story
text, beat level/planning, and asset category/name. Other workflow methods without a host
implementation raise 503 rather than returning fabricated success.

**What is not enforced yet.** The project root is served as one shared space: every signed-in
employee sees the same projects. The filesystem permission of the service account is the only
boundary, and per-user ACL filtering remains a host integration requirement.
"""
from __future__ import annotations

import base64
import hashlib
import io
import os
import posixpath
import secrets
import sqlite3
import stat
import tempfile
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, BinaryIO, Mapping
from urllib.parse import unquote

from . import projector
from .projects_api import ApiFault, BinaryAsset

WORKBOOK_SUFFIX = ".xlsx"
NOT_IMPLEMENTED = "该功能尚未接入。"

# Names the router reserves for its own sub-routes. A file id that collided with one would be
# unreachable, so the encoder avoids producing them.
RESERVED_IDS = {"create", "import", "image-assets", "projects", "reviews"}
MAX_ROW_VERSION = 2**63 - 1


def _not_implemented() -> ApiFault:
    return ApiFault(503, "STORAGE_UNAVAILABLE", NOT_IMPLEMENTED)


class NasSecurity:
    """Turns a bearer token into the host's session principal."""

    def __init__(self, session, visitor_authenticate: Any = None) -> None:
        self._session = session
        self._visitor_authenticate = visitor_authenticate

    def authenticate(self, headers: Mapping[str, str]) -> Any:
        # The router lower-cases every header key before calling (projects_api.py), so the
        # lookup is for "authorization", not "Authorization".
        raw = headers.get("authorization") or ""
        if not raw.lower().startswith("bearer "):
            raise ApiFault(401, "SESSION_REQUIRED", "请先登录。")
        token = raw[7:].strip()
        if not token:
            raise ApiFault(401, "SESSION_REQUIRED", "请先登录。")
        try:
            principal = self._session(token)
        except Exception as error:  # noqa: BLE001 - the host raises its own ApiError type
            if self._visitor_authenticate and self._visitor_authenticate(token):
                return {
                    "authenticated": True,
                    "user": {
                        "id": 0,
                        "username": "访客",
                        "system_username": "my_visitor",
                        "group_type": "visitor",
                        "role": "visitor",
                        "can_write": False,
                    },
                    "role": "visitor",
                    "can_write": False,
                }
            # Translated rather than allowed to propagate: the router's catch-all turns an
            # unexpected exception into a 503, which would report a signed-out user as a
            # broken server.
            status = getattr(error, "status", 401)
            code = getattr(error, "code", None) or "SESSION_REQUIRED"
            message = getattr(error, "message", None) or str(error) or "登录状态已失效，请重新登录。"
            raise ApiFault(status, code, message) from error
        return principal


class _RowVersionTransaction:
    def __init__(self, connection: sqlite3.Connection) -> None:
        self.connection = connection
        self.committed = False

    def resolve(self, file_id: str, fingerprints: Mapping[str, str]) -> dict[str, int]:
        rows = self.connection.execute(
            "SELECT row_id, version, fingerprint FROM workbook_row_versions WHERE file_id = ?",
            (file_id,)).fetchall()
        existing = {row["row_id"]: row for row in rows}
        versions: dict[str, int] = {}
        for row_id, fingerprint in fingerprints.items():
            record = existing.get(row_id)
            if record is None:
                version = 1
                self.connection.execute(
                    "INSERT INTO workbook_row_versions(file_id,row_id,version,fingerprint) VALUES(?,?,?,?)",
                    (file_id, row_id, version, fingerprint))
            elif record["fingerprint"] != fingerprint:
                version = int(record["version"]) + 1
                if version > MAX_ROW_VERSION:
                    raise ApiFault(503, "VERSION_EXHAUSTED", "工作表版本已达到上限。")
                self.connection.execute(
                    "UPDATE workbook_row_versions SET version=?, fingerprint=? WHERE file_id=? AND row_id=?",
                    (version, fingerprint, file_id, row_id))
            else:
                version = int(record["version"])
            versions[row_id] = version
        return versions

    def check(self, file_id: str, row_id: str, expected_version: int, fingerprint: str) -> None:
        record = self.connection.execute(
            "SELECT version, fingerprint FROM workbook_row_versions WHERE file_id=? AND row_id=?",
            (file_id, row_id)).fetchone()
        if record is None:
            current = 1
            self.connection.execute(
                "INSERT INTO workbook_row_versions(file_id,row_id,version,fingerprint) VALUES(?,?,?,?)",
                (file_id, row_id, current, fingerprint))
        elif record["fingerprint"] != fingerprint:
            current = int(record["version"]) + 1
            if current > MAX_ROW_VERSION:
                raise ApiFault(503, "VERSION_EXHAUSTED", "工作表版本已达到上限。")
            self.connection.execute(
                "UPDATE workbook_row_versions SET version=?, fingerprint=? WHERE file_id=? AND row_id=?",
                (current, fingerprint, file_id, row_id))
            self.connection.commit()
            self.committed = True
            raise ApiFault(409, "CONFLICT", "该行已被修改，请重新加载并核对后再编辑。")
        else:
            current = int(record["version"])
        if expected_version != current:
            raise ApiFault(409, "CONFLICT", "该行已被修改，请重新加载并核对后再编辑。")

    def save(self, file_id: str, row_id: str, fingerprint: str) -> int:
        record = self.connection.execute(
            "SELECT version FROM workbook_row_versions WHERE file_id=? AND row_id=?",
            (file_id, row_id)).fetchone()
        if record is None:
            raise ApiFault(503, "VERSION_STATE_LOST", "工作簿版本状态不可用。")
        version = int(record["version"]) + 1
        if version > MAX_ROW_VERSION:
            raise ApiFault(503, "VERSION_EXHAUSTED", "工作表版本已达到上限。")
        self.connection.execute(
            "UPDATE workbook_row_versions SET version=?, fingerprint=? WHERE file_id=? AND row_id=?",
            (version, fingerprint, file_id, row_id))
        return version


class NasRowVersionStore:
    """Persists optimistic row versions outside user-visible workbooks."""

    def __init__(self, db_path: Path | str) -> None:
        self.db_path = Path(db_path)

    @contextmanager
    def transaction(self):
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.db_path, timeout=30)
        connection.row_factory = sqlite3.Row
        transaction = _RowVersionTransaction(connection)
        try:
            connection.execute("PRAGMA busy_timeout=30000")
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("""
                CREATE TABLE IF NOT EXISTS workbook_row_versions (
                    file_id TEXT NOT NULL,
                    row_id TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    fingerprint TEXT NOT NULL,
                    PRIMARY KEY(file_id, row_id)
                )
            """)
            connection.execute("BEGIN IMMEDIATE")
            yield transaction
            if not transaction.committed:
                connection.commit()
        except Exception:
            if not transaction.committed:
                connection.rollback()
            raise
        finally:
            connection.close()


def _row_version_store(root: Path, store: NasRowVersionStore | None) -> NasRowVersionStore:
    return store or NasRowVersionStore(root / ".project-workbook-row-versions.sqlite3")


def _load_workbook_snapshot(path: Path):
    import openpyxl
    projector._validate_archive(path)
    writable = None
    try:
        source = path.read_bytes()
        writable = openpyxl.load_workbook(io.BytesIO(source), data_only=False)
        projected = openpyxl.load_workbook(io.BytesIO(source), data_only=True, read_only=True)
        return writable, projected, hashlib.sha256(source).hexdigest()
    except Exception as error:
        if writable is not None:
            writable.close()
        raise ApiFault(503, "WORKBOOK_UNAVAILABLE", "工作簿无法读取。") from error


def _path_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _save_workbook_atomic(workbook: Any, path: Path, original_digest: str) -> None:
    temporary = None
    try:
        original_stat = path.stat()
        descriptor, temporary = tempfile.mkstemp(prefix=".pwb-", suffix=".xlsx", dir=path.parent)
        os.close(descriptor)
        workbook.save(temporary)
        if hasattr(os, "chown"):
            os.chown(temporary, original_stat.st_uid, original_stat.st_gid)
        os.chmod(temporary, stat.S_IMODE(original_stat.st_mode))
        with open(temporary, "r+b") as saved:
            saved.flush()
            os.fsync(saved.fileno())
        if _path_digest(path) != original_digest:
            raise ApiFault(409, "CONFLICT", "工作簿在保存期间已被修改，请重新加载并核对。")
        os.replace(temporary, path)
        temporary = None
        try:
            directory_fd = os.open(path.parent, os.O_RDONLY)
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
        except OSError:
            pass
    except ApiFault:
        raise
    except Exception as error:
        raise ApiFault(500, "SAVE_FAILED", "保存工作簿失败。") from error
    finally:
        if temporary is not None:
            try:
                os.unlink(temporary)
            except FileNotFoundError:
                pass


def _target_row(workbook: Any, row_id: str, expected_kind: str):
    try:
        sheet_id, raw_row = row_id.rsplit("#", 1)
        row_number = int(raw_row)
    except (ValueError, AttributeError):
        raise ApiFault(422, "INVALID_ROW_ID", "无效的行标识。") from None
    if not sheet_id or row_number < 1:
        raise ApiFault(422, "INVALID_ROW_ID", "无效的行标识。")
    worksheet = next((sheet for sheet in workbook.worksheets
                      if projector._safe_id(sheet.title) == sheet_id), None)
    if worksheet is None and sheet_id.startswith("s") and sheet_id[1:].isdigit():
        sheet_index = int(sheet_id[1:])
        if 0 <= sheet_index < len(workbook.worksheets):
            worksheet = workbook.worksheets[sheet_index]
    if worksheet is None:
        raise ApiFault(404, "NOT_FOUND", "工作表不存在。")
    classification = projector._classify_sheet(worksheet)
    if classification.kind != expected_kind:
        raise ApiFault(422, "INVALID_ROW", "该行不属于可编辑的工作表类型。")
    max_row = worksheet.max_row or classification.header_row
    if row_number <= classification.header_row or row_number > max(max_row + 1, classification.header_row + 1):
        raise ApiFault(404, "NOT_FOUND", "工作表行不存在。")
    row = list(worksheet.iter_rows(min_row=row_number, max_row=row_number))[0]
    return worksheet, classification, row_number, row


def _projected_row(worksheet: Any, classification: Any, row_id: str, version: int,
                   kind: str, project_id: str, project_dir: Path) -> dict[str, Any]:
    fingerprints: dict[str, str] = {}
    if kind == "scene_beats":
        projected = projector._scene_sheet(projector._safe_id(worksheet.title), worksheet.title,
            worksheet, classification, [], fingerprints)["scene_rows"]
    else:
        projected = projector._asset_sheet(projector._safe_id(worksheet.title), worksheet.title,
            worksheet, classification, project_dir=project_dir, project_id=project_id,
            row_fingerprints=fingerprints)["asset_rows"]
    _, raw_row = row_id.rsplit("#", 1)
    canonical_id = f"{projector._safe_id(worksheet.title)}#{raw_row}"
    result = next((row for row in projected if row["id"] in {row_id, canonical_id}), None)
    if result is None:
        raise ApiFault(404, "NOT_FOUND", "工作表行不存在。")
    result["id"] = row_id
    result["version"] = version
    return result


def _asset_component(value: Any, label: str) -> str | None:
    if value is None:
        return None
    if (not isinstance(value, str) or len(value) > 200 or len(value.encode("utf-8", "surrogatepass")) > 200
            or value in {".", ".."} or any(character in value for character in "/\\")
            or any(ord(character) < 32 or ord(character) == 127 or 0xD800 <= ord(character) <= 0xDFFF
                   for character in value)):
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")
    return value or None


class NasWorkbookServices:
    """The ``WorkbookServices`` protocol backed by the shared project directory."""

    def __init__(self, root: Path, row_versions: NasRowVersionStore | None = None) -> None:
        self._root = root
        self._row_versions = _row_version_store(root, row_versions)

    def document(self, actor: Any, file_id: str) -> dict[str, Any]:
        project_id, name = _decode_file_id(file_id)
        path = _resolve_file(self._root, project_id, name)
        with self._row_versions.transaction() as versions:
            return projector.project_document(path, file_id=file_id, name=name, project_id=project_id,
                row_version_resolver=lambda fingerprints: versions.resolve(file_id, fingerprints))

    # --- remaining legacy and media endpoints -------------------------------------------
    def list_files(self, actor: Any) -> list[dict[str, Any]]:
        # The legacy no-project branch. The client always sends project_id, so this is
        # unreachable from the app; answering it would be inventing a second file listing.
        raise _not_implemented()

    def create(self, actor: Any, template_kind: str) -> dict[str, Any]:
        raise _not_implemented()

    def import_xlsx(self, actor: Any, name: str, data: bytes) -> dict[str, Any]:
        raise _not_implemented()

    def original(self, actor: Any, file_id: str) -> BinaryAsset:
        raise _not_implemented()

    def list_images(self, actor: Any) -> list[dict[str, Any]]:
        raise _not_implemented()

    def upload_image(self, actor: Any, name: str, media_type: str, data: bytes) -> dict[str, Any]:
        raise _not_implemented()

    def image_content(self, actor: Any, asset_id: str, thumbnail: bool) -> BinaryAsset:
        project_id, rel_path = _decode_asset_id(asset_id)
        directory = _resolve_project(self._root, project_id)
        target = (directory / rel_path).resolve()
        try:
            target.relative_to(directory.resolve())
        except ValueError:
            raise ApiFault(404, "NOT_FOUND", "素材不存在。")
        if not target.is_file():
            raise ApiFault(404, "NOT_FOUND", "素材文件不存在。")
        suffix = target.suffix.lower()
        media_type = "image/png" if suffix == ".png" else "image/webp" if suffix == ".webp" else "image/jpeg"
        return BinaryAsset(data=target.read_bytes(), media_type=media_type, name=target.name)

    def replace_images(self, actor: Any, file_id: str, row_id: str, **kwargs: Any) -> dict[str, Any]:
        raise _not_implemented()


class NasWorkflowServices:
    """The ``WorkflowServices`` protocol."""

    def __init__(self, root: Path, provisioner: Any = None,
                 row_versions: NasRowVersionStore | None = None) -> None:
        self._root = root
        self._provisioner = provisioner
        self._row_versions = _row_version_store(root, row_versions)

    def projects(self, actor: Any) -> list[dict[str, Any]]:
        return [{"id": str(entry.stat().st_ino), "name": entry.name}
                for entry in _project_directories(self._root)]

    def list_files(self, actor: Any, project_id: str) -> list[dict[str, Any]]:
        directory = _resolve_project(self._root, project_id)
        files = []
        for path in _workbook_files(directory):
            files.append({
                "id": _encode_file_id(project_id, path.name),
                "name": path.name,
                # Decided by reading the workbook, never by its filename: a file named
                # 场景节拍表.xlsx that holds something else must not be advertised as a beat sheet.
                "template_kind": projector.classify(path),
                "version": 1,
                "project_id": project_id,
                "revision_of": None,
                "revision_label": None,
            })
        files.sort(key=lambda item: item["name"])
        return files

    def add_sheet(self, actor: Any, file_id: str, sheet_name: str) -> dict[str, Any]:
        if self._provisioner is None:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "新增工作表服务暂未配置。")
        project_id, file_name = _decode_file_id(file_id)
        directory = _resolve_project(self._root, project_id)
        try:
            return self._provisioner.add_sheet(directory.name, file_name, sheet_name)
        except Exception as error:
            code = getattr(error, "code", "ADD_SHEET_FAILED")
            status = 409 if code == "SHEET_EXISTS" else 422 if code in ("INVALID_SHEET_NAME", "INVALID_FILENAME") else 500
            raise ApiFault(status, code, str(error)) from error

    # --- creating, importing and other host-backed workflows ------------------------------
    def create(self, actor: Any, template_kind: str, project_id: str, operation_id: str, name: str | None = None) -> dict:
        from backend.project_helper import (
            _template_bytes, BEAT_HEADERS, ASSET_HEADERS,
            BEAT_SHEET_NAME, ASSET_SHEET_NAME, FILE_MODE, _write_atomic,
            _ownership, _apply_ownership
        )
        directory = _resolve_project(self._root, project_id)
        if name:
            cleaned = name.strip()
            if cleaned.lower().endswith(".xlsx"):
                cleaned = cleaned[:-5].strip()
            base_name = cleaned if cleaned else ("节拍表" if template_kind == "scene_beats" else "资产表")
        elif template_kind == "scene_beats":
            base_name = "节拍表"
        elif template_kind == "asset_review":
            base_name = "资产表"
        else:
            raise ApiFault(422, "INVALID_TEMPLATE", "请选择一种内置模板。")

        if template_kind == "scene_beats":
            data = _template_bytes(BEAT_HEADERS, BEAT_SHEET_NAME)
        else:
            data = _template_bytes(ASSET_HEADERS, ASSET_SHEET_NAME)

        target_name = f"{base_name}.xlsx"
        idx = 2
        while (directory / target_name).exists():
            target_name = f"{base_name}_{idx}.xlsx"
            idx += 1

        target_file = directory / target_name
        _write_atomic(target_file, data, FILE_MODE)
        try:
            uid, gid = _ownership()
            _apply_ownership(target_file, uid, gid)
        except Exception:
            pass

        return {
            "id": _encode_file_id(project_id, target_file.name),
            "name": target_file.name,
            "template_kind": template_kind,
            "version": 1,
            "project_id": project_id,
            "revision_of": None,
            "revision_label": None,
        }

    def import_xlsx_stream(self, actor: Any, name: str, source: BinaryIO, length: int,
                           project_id: Any, operation_id: Any, revision_of: Any) -> dict:
        from backend.project_helper import (
            _validate_name, FILE_MODE, _write_atomic,
            _ownership, _apply_ownership
        )
        if project_id is None:
            raise ApiFault(422, "MISSING_PROJECT", "导入需要指定项目。")
        directory = _resolve_project(self._root, project_id)
        try:
            safe_name = _validate_name(name)
        except Exception as error:
            raise ApiFault(422, "INVALID_FILENAME", str(error)) from error
        if not safe_name.lower().endswith(WORKBOOK_SUFFIX):
            raise ApiFault(415, "UNSUPPORTED_TYPE", "项目只接收 .xlsx 文件。")

        target_name = safe_name
        if (directory / target_name).exists() and revision_of is None:
            stem = safe_name[:-5]
            idx = 2
            while (directory / f"{stem}_{idx}.xlsx").exists():
                idx += 1
            target_name = f"{stem}_{idx}.xlsx"

        data = source.read(length) if length >= 0 else source.read()
        if not data:
            raise ApiFault(422, "INVALID_WORKBOOK", "表格文件为空。")

        target_file = directory / target_name
        _write_atomic(target_file, data, FILE_MODE)
        try:
            uid, gid = _ownership()
            _apply_ownership(target_file, uid, gid)
        except Exception:
            pass

        kind = projector.classify(target_file)
        return {
            "id": _encode_file_id(project_id, target_file.name),
            "name": target_file.name,
            "template_kind": kind,
            "version": 1,
            "project_id": project_id,
            "revision_of": revision_of,
            "revision_label": None,
        }

    def list_images(self, actor: Any, project_id: str, q: Any, category: Any,
                    cursor: Any, page_size: int) -> dict:
        directory = _resolve_project(self._root, project_id)
        items = []
        for cat_dir in sorted(directory.iterdir(), key=lambda e: e.name):
            if cat_dir.is_dir() and not cat_dir.name.startswith(".") and (category is None or cat_dir.name == category):
                for item_dir in sorted(cat_dir.iterdir(), key=lambda e: e.name):
                    if item_dir.is_dir() and not item_dir.name.startswith("."):
                        if q is not None and q not in item_dir.name:
                            continue
                        for img in sorted(item_dir.iterdir(), key=lambda e: e.name):
                            if img.is_file() and img.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
                                rel_path = f"{cat_dir.name}/{item_dir.name}/{img.name}"
                                items.append({
                                    "id": _encode_asset_id(project_id, rel_path),
                                    "name": img.name,
                                    "media_type": "image/png" if img.suffix.lower() == ".png" else "image/jpeg",
                                    "width": 0,
                                    "height": 0,
                                    "project_id": project_id,
                                    "category": cat_dir.name,
                                    "variant": item_dir.name,
                                    "revision_label": None
                                })
        return {"items": items[:page_size], "next_cursor": None}

    def upload_image_stream(self, actor: Any, name: str, media_type: str, source: BinaryIO,
                            length: int, project_id: Any) -> dict:
        raise _not_implemented()

    def image_content_stream(self, actor: Any, asset_id: str, thumbnail: bool):
        from .workflow_services import StreamAsset
        project_id, rel_path = _decode_asset_id(asset_id)
        directory = _resolve_project(self._root, project_id)
        target = (directory / rel_path).resolve()
        try:
            target.relative_to(directory.resolve())
        except ValueError:
            raise ApiFault(404, "NOT_FOUND", "素材不存在。")
        if not target.is_file():
            raise ApiFault(404, "NOT_FOUND", "素材文件不存在。")
        suffix = target.suffix.lower()
        media_type = "image/png" if suffix == ".png" else "image/webp" if suffix == ".webp" else "image/jpeg"
        return StreamAsset(stream=open(target, "rb"), length=target.stat().st_size, media_type=media_type, name=target.name)

    def original_stream(self, actor: Any, file_id: str):
        raise _not_implemented()

    def revisions(self, actor: Any, file_id: str) -> list[dict]:
        raise _not_implemented()

    def export_current(self, actor: Any, file_id: str, expected_version: int):
        raise _not_implemented()

    def update_scene(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                     label_level: Any, breakdown_plan: Any) -> dict:
        return self._update_scene_fields(actor, file_id, row_id, expected_version,
            {"label_level": label_level, "breakdown_plan": breakdown_plan})

    def update_story_content(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                             story_content: Any) -> dict:
        return self._update_scene_fields(actor, file_id, row_id, expected_version,
            {"story_content": story_content})

    def _update_scene_fields(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                             changes: Mapping[str, Any]) -> dict:
        project_id, file_name = _decode_file_id(file_id)
        path = _resolve_file(self._root, project_id, file_name)
        with self._row_versions.transaction() as versions:
            workbook, projected_workbook, original_digest = _load_workbook_snapshot(path)
            try:
                worksheet, classification, row_number, _ = _target_row(workbook, row_id, "scene_beats")
                projected_ws, projected_classification, _, projected_row = _target_row(projected_workbook, row_id, "scene_beats")
                versions.check(file_id, row_id, expected_version,
                    projector.row_fingerprint(row_id, projected_row))
                fields = classification.fields
                replacements = {}
                for field, requested_value in changes.items():
                    column = fields.get(field)
                    if column is None:
                        label = "故事内容" if field == "story_content" else "该节拍字段"
                        raise ApiFault(422, "INVALID_ROW", f"该工作表没有{label}列。")
                    value = requested_value or None
                    worksheet.cell(row=row_number, column=column + 1, value=value)
                    replacements[column] = value
                fingerprint = projector.row_fingerprint(row_id, projected_row, replacements)
                _save_workbook_atomic(workbook, path, original_digest)
                version = versions.save(file_id, row_id, fingerprint)
                result = _projected_row(projected_ws, projected_classification, row_id, version,
                    "scene_beats", project_id, path.parent)
                for field, requested_value in changes.items():
                    result[field] = requested_value or None
                return result
            finally:
                workbook.close()
                projected_workbook.close()

    def update_asset_row(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                         category: Any, name: Any) -> dict:
        project_id, file_name = _decode_file_id(file_id)
        path = _resolve_file(self._root, project_id, file_name)
        category = _asset_component(category, "类别")
        name = _asset_component(name, "名称")
        with self._row_versions.transaction() as versions:
            workbook, projected_workbook, original_digest = _load_workbook_snapshot(path)
            try:
                worksheet, classification, row_number, _ = _target_row(workbook, row_id, "asset_review")
                projected_ws, projected_classification, _, projected_row = _target_row(projected_workbook, row_id, "asset_review")
                versions.check(file_id, row_id, expected_version,
                    projector.row_fingerprint(row_id, projected_row))
                category_col = classification.fields.get("category")
                name_col = classification.fields.get("name")
                if category_col is None or name_col is None:
                    raise ApiFault(422, "INVALID_ROW", "该工作表没有类别和名称列。")
                worksheet.cell(row=row_number, column=category_col + 1, value=category)
                worksheet.cell(row=row_number, column=name_col + 1, value=name)
                fingerprint = projector.row_fingerprint(row_id, projected_row,
                    {category_col: category, name_col: name})
                _save_workbook_atomic(workbook, path, original_digest)
                version = versions.save(file_id, row_id, fingerprint)
                result = _projected_row(projected_ws, projected_classification, row_id, version,
                    "asset_review", project_id, path.parent)
                result["category"], result["name"] = category, name
                selected, candidates = projector._asset_images(path.parent, project_id, category, name,
                    result.get("variant"))
                result["selected_image"], result["candidate_images"] = selected, candidates
                return result
            finally:
                workbook.close()
                projected_workbook.close()

    def update_origins(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       origins: list[dict[str, str]]) -> dict:
        raise _not_implemented()

    def replace_images(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       selected_image_id: Any, candidate_image_ids: list[str],
                       selection_reason: Any) -> dict:
        raise _not_implemented()

    def selection_history(self, actor: Any, file_id: str, row_id: str) -> list[dict]:
        raise _not_implemented()


# --------------------------------------------------------------------------------------------
# path handling
# --------------------------------------------------------------------------------------------


def _project_directories(root: Path) -> list[Path]:
    try:
        entries = list(root.iterdir())
    except OSError as error:
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "项目目录暂时不可用。") from error
    directories = [entry for entry in entries
                   if not entry.name.startswith(".") and entry.is_dir() and not entry.is_symlink()]
    directories.sort(key=lambda entry: entry.name)
    return directories


def _resolve_project(root: Path, project_id: str) -> Path:
    if not project_id.isdigit():
        raise ApiFault(404, "NOT_FOUND", "项目不存在。")
    for directory in _project_directories(root):
        try:
            if str(directory.stat().st_ino) == project_id:
                return directory
        except OSError:
            continue
    raise ApiFault(404, "NOT_FOUND", "项目不存在。")


def _workbook_files(directory: Path) -> list[Path]:
    files = []
    for entry in directory.iterdir():
        name = entry.name
        if name.startswith(".") or name.startswith("~$"):
            # "~$" is Excel's own lock file for an open workbook; listing it as a document
            # would offer the user a file that is not the workbook.
            continue
        if not name.lower().endswith(WORKBOOK_SUFFIX):
            continue
        if entry.is_symlink() or not entry.is_file():
            continue
        try:
            if entry.stat().st_size <= 0:
                continue
        except OSError:
            continue
        files.append(entry)
    return files


def _resolve_file(root: Path, project_id: str, name: str) -> Path:
    directory = _resolve_project(root, project_id)
    # The name is re-checked against the directory listing rather than joined onto it, so a
    # crafted id cannot name a file outside the project even if the encoder is ever loosened.
    for path in _workbook_files(directory):
        if path.name == name:
            return path
    raise ApiFault(404, "NOT_FOUND", "工作簿不存在。")


def _encode_file_id(project_id: str, name: str) -> str:
    raw = f"{project_id}\x00{name}".encode("utf-8")
    encoded = base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")
    # The project id leads, so a collision with a reserved route name is impossible unless the
    # id itself produced one — but the guard costs nothing and the router would 404 silently.
    return f"w{encoded}" if encoded in RESERVED_IDS else encoded


def _decode_file_id(file_id: str) -> tuple[str, str]:
    if not file_id or len(file_id) > 512 or any(character in file_id for character in "\\/\x00"):
        raise ApiFault(422, "INVALID_REQUEST", "工作簿标识无效。")
    candidate = file_id[1:] if file_id.startswith("w") and len(file_id) > 1 else file_id
    try:
        padded = candidate + "=" * (-len(candidate) % 4)
        decoded = base64.urlsafe_b64decode(padded).decode("utf-8")
    except Exception as error:  # noqa: BLE001 - any decode failure is the same to the caller
        raise ApiFault(422, "INVALID_REQUEST", "工作簿标识无效。") from error

    project_id, separator, name = decoded.partition("\x00")
    if not separator or not project_id.isdigit() or not name or posixpath.basename(name) != name:
        raise ApiFault(422, "INVALID_REQUEST", "工作簿标识无效。")
    return project_id, name


def _encode_asset_id(project_id: str, rel_path: str) -> str:
    raw = f"{project_id}\x00{rel_path}".encode("utf-8")
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def _decode_asset_id(asset_id: str) -> tuple[str, str]:
    if not asset_id or len(asset_id) > 1024:
        raise ApiFault(422, "INVALID_REQUEST", "素材标识无效。")
    try:
        padded = asset_id + "=" * (-len(asset_id) % 4)
        decoded = base64.urlsafe_b64decode(padded).decode("utf-8")
    except Exception as error:
        raise ApiFault(422, "INVALID_REQUEST", "素材标识无效。") from error

    project_id, separator, rel_path = decoded.partition("\x00")
    if not separator or not project_id.isdigit() or not rel_path:
        raise ApiFault(422, "INVALID_REQUEST", "素材标识无效。")
    return project_id, rel_path


class NasReviewServices:
    """SQLite-backed review and comment service for project workbooks."""

    def __init__(self, db_path: Path | str, row_kind: str = "scene-rows") -> None:
        self.db_path = Path(db_path)
        self.row_kind = row_kind
        self._init_db()

    def _connect(self):
        from contextlib import contextmanager
        @contextmanager
        def _ctx():
            conn = sqlite3.connect(self.db_path)
            try:
                yield conn
                conn.commit()
            finally:
                conn.close()
        return _ctx()

    def _init_db(self) -> None:
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS project_reviews (
                    id TEXT PRIMARY KEY,
                    file_id TEXT NOT NULL,
                    row_kind TEXT NOT NULL,
                    row_id TEXT NOT NULL,
                    image_id TEXT,
                    user_id TEXT NOT NULL,
                    user_name TEXT NOT NULL,
                    text TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    resolved INTEGER NOT NULL DEFAULT 0,
                    resolution_note TEXT
                )
            """)
            conn.execute("""
                CREATE INDEX IF NOT EXISTS idx_project_reviews_lookup
                ON project_reviews (file_id, row_kind, row_id, image_id)
            """)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS project_review_votes (
                    file_id TEXT NOT NULL,
                    row_id TEXT NOT NULL,
                    image_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    verdict TEXT NOT NULL,
                    PRIMARY KEY (file_id, row_id, image_id, user_id)
                )
            """)

    def summaries(self, actor: Any, file_id: str, row_id: str) -> list[dict[str, Any]]:
        user_id = str(actor.get("user", {}).get("id", "")) if isinstance(actor, dict) else ""
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            rows = conn.execute("""
                SELECT pr.image_id, COUNT(*) as comment_count,
                       (SELECT text FROM project_reviews pr2
                        WHERE pr2.file_id = pr.file_id AND pr2.row_kind = pr.row_kind AND pr2.row_id = pr.row_id
                          AND ((pr2.image_id IS NULL AND pr.image_id IS NULL) OR pr2.image_id = pr.image_id)
                        ORDER BY pr2.created_at DESC, pr2.rowid DESC LIMIT 1) as latest_comment,
                       (SELECT user_name FROM project_reviews pr3
                        WHERE pr3.file_id = pr.file_id AND pr3.row_kind = pr.row_kind AND pr3.row_id = pr.row_id
                          AND ((pr3.image_id IS NULL AND pr.image_id IS NULL) OR pr3.image_id = pr.image_id)
                        ORDER BY pr3.created_at DESC, pr3.rowid DESC LIMIT 1) as latest_user
                FROM project_reviews pr
                WHERE pr.file_id = ? AND pr.row_kind = ? AND pr.row_id = ?
                GROUP BY pr.image_id
            """, (file_id, self.row_kind, row_id)).fetchall()

            vote_rows = conn.execute("""
                SELECT image_id,
                       SUM(CASE WHEN verdict = 'good' THEN 1 ELSE 0 END) as good,
                       SUM(CASE WHEN verdict = 'bad' THEN 1 ELSE 0 END) as bad,
                       MAX(CASE WHEN user_id = ? THEN verdict ELSE NULL END) as my_verdict
                FROM project_review_votes
                WHERE file_id = ? AND row_id = ?
                GROUP BY image_id
            """, (user_id, file_id, row_id)).fetchall()

            votes_by_img = {r["image_id"]: r for r in vote_rows}
            result = []
            seen_images = set()
            for r in rows:
                img_id = r["image_id"]
                seen_images.add(img_id)
                v = votes_by_img.get(img_id)
                result.append({
                    "image_id": img_id,
                    "good": v["good"] if v else 0,
                    "bad": v["bad"] if v else 0,
                    "comment_count": r["comment_count"],
                    "my_verdict": v["my_verdict"] if v else None,
                    "latest_comment": r["latest_comment"],
                    "latest_user": r["latest_user"],
                })
            for img_id, v in votes_by_img.items():
                if img_id not in seen_images:
                    result.append({
                        "image_id": img_id,
                        "good": v["good"],
                        "bad": v["bad"],
                        "comment_count": 0,
                        "my_verdict": v["my_verdict"],
                        "latest_comment": None,
                        "latest_user": None,
                    })
            if not result:
                result.append({
                    "image_id": None,
                    "good": 0,
                    "bad": 0,
                    "comment_count": 0,
                    "my_verdict": None,
                    "latest_comment": None,
                    "latest_user": None,
                })
            return result

    def thread(self, actor: Any, file_id: str, row_id: str, image_id: str | None) -> dict[str, Any]:
        current_user_id = str(actor.get("user", {}).get("id", "")) if isinstance(actor, dict) else ""
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            if image_id is None:
                rows = conn.execute("""
                    SELECT * FROM project_reviews
                    WHERE file_id = ? AND row_kind = ? AND row_id = ? AND image_id IS NULL
                    ORDER BY created_at ASC
                """, (file_id, self.row_kind, row_id)).fetchall()
            else:
                rows = conn.execute("""
                    SELECT * FROM project_reviews
                    WHERE file_id = ? AND row_kind = ? AND row_id = ? AND image_id = ?
                    ORDER BY created_at ASC
                """, (file_id, self.row_kind, row_id, image_id)).fetchall()

            comments = []
            for r in rows:
                u_id = str(r["user_id"])
                can_edit = (u_id == current_user_id)
                can_delete = can_edit
                can_resolve = True
                comments.append({
                    "id": r["id"],
                    "user_id": u_id,
                    "user_name": r["user_name"],
                    "text": r["text"],
                    "version": r["version"],
                    "created_at": r["created_at"],
                    "updated_at": r["updated_at"],
                    "can_edit": can_edit,
                    "can_delete": can_delete,
                    "resolved": bool(r["resolved"]),
                    "resolution_note": r["resolution_note"],
                    "can_resolve": can_resolve,
                })

            good = bad = 0
            my_verdict = None
            if image_id is not None:
                v_rows = conn.execute("""
                    SELECT verdict, user_id FROM project_review_votes
                    WHERE file_id = ? AND row_id = ? AND image_id = ?
                """, (file_id, row_id, image_id)).fetchall()
                for v in v_rows:
                    if v["verdict"] == "good": good += 1
                    elif v["verdict"] == "bad": bad += 1
                    if str(v["user_id"]) == current_user_id:
                        my_verdict = v["verdict"]

            return {
                "image_id": image_id,
                "good": good,
                "bad": bad,
                "comment_count": len(comments),
                "my_verdict": my_verdict,
                "comments": comments,
            }

    def add_comment(self, actor: Any, file_id: str, row_id: str, image_id: str | None,
                    text: str) -> dict[str, Any]:
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        user_id = str(user.get("id", "0"))
        user_name = str(user.get("username", "user"))
        comment_id = secrets.token_hex(16)
        now = datetime.now(timezone.utc).isoformat()
        with self._connect() as conn:
            conn.execute("""
                INSERT INTO project_reviews (id, file_id, row_kind, row_id, image_id, user_id, user_name, text, version, created_at, updated_at, resolved, resolution_note)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, 0, NULL)
            """, (comment_id, file_id, self.row_kind, row_id, image_id, user_id, user_name, text, now, now))
        return self.thread(actor, file_id, row_id, image_id)

    def edit_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                     expected_version: int, text: str) -> dict[str, Any]:
        user_id = str(actor.get("user", {}).get("id", "")) if isinstance(actor, dict) else ""
        now = datetime.now(timezone.utc).isoformat()
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            row = conn.execute("SELECT * FROM project_reviews WHERE id = ?", (comment_id,)).fetchone()
            if not row:
                raise ApiFault(404, "NOT_FOUND", "批注不存在。")
            if row["version"] != expected_version:
                raise ApiFault(409, "CONFLICT", "批注已被修改，请重新加载。")
            if str(row["user_id"]) != user_id:
                raise ApiFault(403, "FORBIDDEN", "只能编辑自己的批注。")
            conn.execute("""
                UPDATE project_reviews
                SET text = ?, version = version + 1, updated_at = ?
                WHERE id = ?
            """, (text, now, comment_id))
            image_id = row["image_id"]
        return self.thread(actor, file_id, row_id, image_id)

    def delete_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                       expected_version: int) -> dict[str, Any]:
        user_id = str(actor.get("user", {}).get("id", "")) if isinstance(actor, dict) else ""
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            row = conn.execute("SELECT * FROM project_reviews WHERE id = ?", (comment_id,)).fetchone()
            if not row:
                raise ApiFault(404, "NOT_FOUND", "批注不存在。")
            if row["version"] != expected_version:
                raise ApiFault(409, "CONFLICT", "批注已被修改，请重新加载。")
            if str(row["user_id"]) != user_id:
                raise ApiFault(403, "FORBIDDEN", "只能删除自己的批注。")
            conn.execute("DELETE FROM project_reviews WHERE id = ?", (comment_id,))
            image_id = row["image_id"]
        return self.thread(actor, file_id, row_id, image_id)

    def resolve_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                        expected_version: int, resolved: bool,
                        resolution_note: str | None) -> dict[str, Any]:
        now = datetime.now(timezone.utc).isoformat()
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            row = conn.execute("SELECT * FROM project_reviews WHERE id = ?", (comment_id,)).fetchone()
            if not row:
                raise ApiFault(404, "NOT_FOUND", "批注不存在。")
            if row["version"] != expected_version:
                raise ApiFault(409, "CONFLICT", "批注已被修改，请重新加载。")
            conn.execute("""
                UPDATE project_reviews
                SET resolved = ?, resolution_note = ?, version = version + 1, updated_at = ?
                WHERE id = ?
            """, (1 if resolved else 0, resolution_note, now, comment_id))
            image_id = row["image_id"]
        return self.thread(actor, file_id, row_id, image_id)

    def vote(self, actor: Any, file_id: str, row_id: str, image_id: str,
             verdict: str | None) -> dict[str, Any]:
        user_id = str(actor.get("user", {}).get("id", "")) if isinstance(actor, dict) else ""
        with self._connect() as conn:
            if verdict is None:
                conn.execute("""
                    DELETE FROM project_review_votes
                    WHERE file_id = ? AND row_id = ? AND image_id = ? AND user_id = ?
                """, (file_id, row_id, image_id, user_id))
            else:
                conn.execute("""
                    INSERT INTO project_review_votes (file_id, row_id, image_id, user_id, verdict)
                    VALUES (?, ?, ?, ?, ?)
                    ON CONFLICT(file_id, row_id, image_id, user_id) DO UPDATE SET verdict = excluded.verdict
                """, (file_id, row_id, image_id, user_id, verdict))
        return self.thread(actor, file_id, row_id, image_id)


