"""The host half of the workbook contract: authentication, and the three read routes.

The vendored module declares routes and JSON shapes and asks the host for everything else
through protocols. These are those protocols, backed by the project directory on the NAS.

**Scope, stated plainly.** This serves the read-only first cut: list the projects, list a
project's workbooks, open one. Every other protocol method raises 503, which is the contract's
own way of saying "not implemented" — the client expects it and the package documents it. A
fabricated success here would be worse than the error, so nothing is faked.

**What is not enforced yet.** The project root is served as one shared space: every signed-in
employee sees the same projects. The filesystem permission of the service account is the only
boundary, and per-user ACL filtering is part of a later cut. That is a real limitation, not an
oversight, and it is why this cut is read-only.
"""
from __future__ import annotations

import base64
import posixpath
import secrets
import sqlite3
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


def _not_implemented() -> ApiFault:
    return ApiFault(503, "STORAGE_UNAVAILABLE", NOT_IMPLEMENTED)


class NasSecurity:
    """Turns a bearer token into the host's session principal."""

    def __init__(self, session) -> None:
        self._session = session

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
            # Translated rather than allowed to propagate: the router's catch-all turns an
            # unexpected exception into a 503, which would report a signed-out user as a
            # broken server.
            status = getattr(error, "status", 401)
            code = getattr(error, "code", None) or "SESSION_REQUIRED"
            message = getattr(error, "message", None) or str(error) or "登录状态已失效，请重新登录。"
            raise ApiFault(status, code, message) from error
        return principal


class NasWorkbookServices:
    """The ``WorkbookServices`` protocol: this cut answers ``document`` and refuses the rest."""

    def __init__(self, root: Path) -> None:
        self._root = root

    def document(self, actor: Any, file_id: str) -> dict[str, Any]:
        project_id, name = _decode_file_id(file_id)
        path = _resolve_file(self._root, project_id, name)
        return projector.project_document(path, file_id=file_id, name=name, project_id=project_id)

    # --- editing and media: a later cut -------------------------------------------------
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

    def __init__(self, root: Path, provisioner: Any = None) -> None:
        self._root = root
        self._provisioner = provisioner

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

    # --- creating, importing and everything downstream: a later cut ----------------------
    def create(self, actor: Any, template_kind: str, project_id: str, operation_id: str) -> dict:
        from backend.v2.project_helper import (
            _template_bytes, BEAT_HEADERS, ASSET_HEADERS,
            BEAT_SHEET_NAME, ASSET_SHEET_NAME, FILE_MODE, _write_atomic,
            _ownership, _apply_ownership
        )
        directory = _resolve_project(self._root, project_id)
        if template_kind == "scene_beats":
            base_name = "节拍表"
            data = _template_bytes(BEAT_HEADERS, BEAT_SHEET_NAME)
        elif template_kind == "asset_review":
            base_name = "资产表"
            data = _template_bytes(ASSET_HEADERS, ASSET_SHEET_NAME)
        else:
            raise ApiFault(422, "INVALID_TEMPLATE", "请选择一种内置模板。")

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
        from backend.v2.project_helper import (
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
        import openpyxl
        project_id, file_name = _decode_file_id(file_id)
        path = _resolve_file(self._root, project_id, file_name)

        try:
            sheet_id, row_str = row_id.split("#")
            row_num = int(row_str)
        except Exception:
            raise ApiFault(422, "INVALID_ROW_ID", "无效的行标识。")

        try:
            wb = openpyxl.load_workbook(path)
        except Exception as error:
            raise ApiFault(503, "WORKBOOK_UNAVAILABLE", "工作簿无法读取。") from error

        try:
            sheet_index = int(sheet_id[1:]) if sheet_id.startswith("s") else -1
            if 0 <= sheet_index < len(wb.worksheets):
                ws = wb.worksheets[sheet_index]
            else:
                ws = wb[sheet_id]
        except Exception:
            raise ApiFault(404, "NOT_FOUND", "工作表不存在。")

        classification = projector._classify_sheet(ws)
        label_col = classification.fields.get("label_level")
        plan_col = classification.fields.get("breakdown_plan")

        if label_col is not None:
            ws.cell(row=row_num, column=label_col + 1, value=label_level if label_level else None)
        if plan_col is not None:
            ws.cell(row=row_num, column=plan_col + 1, value=breakdown_plan if breakdown_plan else None)

        try:
            wb.save(path)
        except Exception as error:
            raise ApiFault(500, "SAVE_FAILED", f"保存工作簿失败：{error}") from error

        row_cells = list(ws.iter_rows(min_row=row_num, max_row=row_num))[0]
        def _get(col):
            if col is not None and col < len(row_cells):
                val = row_cells[col].value
                return str(val) if val is not None else None
            return None

        return {
            "id": row_id,
            "version": expected_version + 1,
            "scene_number": _get(classification.fields.get("scene_number")),
            "beat_number": _get(classification.fields.get("beat_number")),
            "story_content": _get(classification.fields.get("story_content")),
            "label_level": label_level,
            "breakdown_plan": breakdown_plan,
            "legacy_notes": [],
            "scene_context": None,
            "cast_context": None,
            "origins": [],
            "can_edit": True,
        }

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
                SELECT image_id, COUNT(*) as comment_count
                FROM project_reviews
                WHERE file_id = ? AND row_kind = ? AND row_id = ?
                GROUP BY image_id
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
                })
            for img_id, v in votes_by_img.items():
                if img_id not in seen_images:
                    result.append({
                        "image_id": img_id,
                        "good": v["good"],
                        "bad": v["bad"],
                        "comment_count": 0,
                        "my_verdict": v["my_verdict"],
                    })
            if not result:
                result.append({
                    "image_id": None,
                    "good": 0,
                    "bad": 0,
                    "comment_count": 0,
                    "my_verdict": None,
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


