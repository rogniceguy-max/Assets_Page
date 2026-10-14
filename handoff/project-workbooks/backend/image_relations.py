"""SQLite reference for selected/candidate links; no automatic DB/schema writes."""
from __future__ import annotations

import sqlite3
from collections.abc import Callable
from typing import Any, Protocol

from .projects_api import ApiFault, validate_image_change
from .workflow_services import ExportTouch


class RowAccess(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str,
                 row_id: str, *, write: bool) -> dict[str, Any]:
        """Check host file + row ACL/existence; return category/name/appearance.

        Query through the supplied connection so authorization and mutation share
        one transaction. Raise safe ApiFault on denial. No allow-all default.
        """
        ...


class ImageAccess(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any,
                 image_id: str, *, file_id: str, row_id: str, write: bool) -> dict[str, Any]:
        """Check existence, visibility and linkability; return canonical ImageAsset.

        With write=True require linkability into the target file/project; with
        write=False require visibility (including historical immutable versions).
        Query through supplied transaction. Byte decode validation happens on upload.
        """
        ...


class ImageRelations:
    def __init__(self, connect: Callable[[], sqlite3.Connection],
                 row_access: RowAccess, image_access: ImageAccess,
                 export_touch: ExportTouch):
        self.connect, self.row_access, self.image_access = connect, row_access, image_access
        self.export_touch = export_touch

    @staticmethod
    def register_row(connection: sqlite3.Connection, file_id: str, row_id: str) -> None:
        """Host calls inside successful create/import transaction for real asset rows.

        Not an HTTP endpoint. Do not register invented rows or use INSERT OR REPLACE,
        which would erase relationships/version during repeated indexing.
        """
        connection.execute("INSERT OR IGNORE INTO pwb_asset_row_state(file_id,row_id) VALUES (?,?)",
                           (file_id, row_id))

    def read(self, actor: Any, file_id: str, row_id: str) -> dict[str, Any]:
        return self._operation(actor, file_id, row_id, None)

    def replace(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                selected_image_id: str | None, candidate_image_ids: list[str]) -> dict[str, Any]:
        change = validate_image_change({"expected_version": expected_version,
                                       "selected_image_id": selected_image_id,
                                       "candidate_image_ids": candidate_image_ids})
        return self._operation(actor, file_id, row_id, change)

    def _operation(self, actor: Any, file_id: str, row_id: str,
                   change: tuple[int, str | None, list[str]] | None) -> dict[str, Any]:
        connection = self.connect()
        try:
            connection.execute("PRAGMA foreign_keys=ON")
            connection.execute("BEGIN IMMEDIATE" if change is not None else "BEGIN")
            base = self.row_access(connection, actor, file_id, row_id, write=change is not None)
            state = connection.execute(
                "SELECT version,selected_image_id FROM pwb_asset_row_state WHERE file_id=? AND row_id=?",
                (file_id, row_id)).fetchone()
            if state is None:
                raise ApiFault(404, "NOT_FOUND", "表格行不存在。")
            version, selected_id = state
            if change is not None:
                expected, selected_id, candidate_ids = change
                if version != expected:
                    raise ApiFault(409, "VERSION_CONFLICT", "内容已更新，请重新加载后选择。")
            else:
                candidate_ids = [item[0] for item in connection.execute(
                    "SELECT image_id FROM pwb_asset_row_candidates WHERE file_id=? AND row_id=? ORDER BY ordinal",
                    (file_id, row_id))]
            # Resolve every requested asset before altering any link. Permission
            # revocation or missing images fail the operation; never silently omit.
            resolved: dict[str, dict[str, Any]] = {}
            for image_id in ([selected_id] if selected_id is not None else []) + candidate_ids:
                if image_id not in resolved:
                    resolved[image_id] = self.image_access(connection, actor, image_id,
                        file_id=file_id, row_id=row_id, write=change is not None)
            if change is not None:
                cursor = connection.execute(
                    "UPDATE pwb_asset_row_state SET selected_image_id=?,version=version+1 "
                    "WHERE file_id=? AND row_id=? AND version=?",
                    (selected_id, file_id, row_id, version))
                if cursor.rowcount != 1:
                    raise ApiFault(409, "VERSION_CONFLICT", "内容已更新，请重新加载后选择。")
                connection.execute("DELETE FROM pwb_asset_row_candidates WHERE file_id=? AND row_id=?",
                                   (file_id, row_id))
                connection.executemany(
                    "INSERT INTO pwb_asset_row_candidates(file_id,row_id,image_id,ordinal) VALUES (?,?,?,?)",
                    [(file_id, row_id, image_id, index) for index, image_id in enumerate(candidate_ids)])
                version += 1
                self.export_touch(connection, actor, file_id)
            result = {"id": row_id, "version": version,
                      "category": base.get("category"), "name": base.get("name"),
                      "appearance": base.get("appearance"),
                      "variant": base.get("variant"), "can_edit": base.get("can_edit") is True,
                      "selected_image": resolved[selected_id] if selected_id is not None else None,
                      "candidate_images": [resolved[image_id] for image_id in candidate_ids]}
            connection.commit()
            return result
        except sqlite3.Error:
            connection.rollback()
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "存储暂不可用，请稍后再试。") from None
        except BaseException:
            connection.rollback()
            raise
        finally:
            connection.close()
