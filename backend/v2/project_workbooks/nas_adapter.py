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
        raise _not_implemented()

    def replace_images(self, actor: Any, file_id: str, row_id: str, **kwargs: Any) -> dict[str, Any]:
        raise _not_implemented()


class NasWorkflowServices:
    """The ``WorkflowServices`` protocol: this cut answers ``projects`` and ``list_files``."""

    def __init__(self, root: Path) -> None:
        self._root = root

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

    # --- creating, importing and everything downstream: a later cut ----------------------
    def create(self, actor: Any, template_kind: str, project_id: str, operation_id: str) -> dict:
        raise _not_implemented()

    def import_xlsx_stream(self, actor: Any, name: str, source: BinaryIO, length: int,
                           project_id: Any, operation_id: Any, revision_of: Any) -> dict:
        raise _not_implemented()

    def list_images(self, actor: Any, project_id: str, q: Any, category: Any,
                    cursor: Any, page_size: int) -> dict:
        raise _not_implemented()

    def upload_image_stream(self, actor: Any, name: str, media_type: str, source: BinaryIO,
                            length: int, project_id: Any) -> dict:
        raise _not_implemented()

    def image_content_stream(self, actor: Any, asset_id: str, thumbnail: bool):
        raise _not_implemented()

    def original_stream(self, actor: Any, file_id: str):
        raise _not_implemented()

    def revisions(self, actor: Any, file_id: str) -> list[dict]:
        raise _not_implemented()

    def export_current(self, actor: Any, file_id: str, expected_version: int):
        raise _not_implemented()

    def update_scene(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                     label_level: Any, breakdown_plan: Any) -> dict:
        raise _not_implemented()

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
