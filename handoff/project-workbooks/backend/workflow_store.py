"""Scene planning/origin transactions and same-transaction idempotency helpers."""
from __future__ import annotations

import hashlib
import json
import sqlite3
from collections.abc import Callable
from datetime import datetime, timezone
from typing import Any, BinaryIO, Protocol

from .projects_api import ApiFault, invalid, optional_text, origins_payload, review_version, valid_id
from .workflow_services import ExportTouch


class SceneAccess(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str,
                 row_id: str, *, write: bool) -> dict[str, Any]:
        """Check true scene-row existence, file/project ACL and independent edit ACL.

        Return full canonical source SceneRow (including all six legacy_notes,
        source_cell and scene/cast context). Never invent original script text.
        """
        ...


class OriginAccess(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str, row_id: str,
                 origin_file_id: str, origin_row_id: str) -> None:
        """Check readable real scene row in ANOTHER revision of SAME project/chain.

        Do not infer relation from beat numbers. Use this transaction connection.
        """
        ...


class SceneStore:
    def __init__(self, connect: Callable[[], sqlite3.Connection], access: SceneAccess,
                 origin_access: OriginAccess, export_touch: ExportTouch):
        self.connect, self.access, self.origin_access, self.export_touch = connect, access, origin_access, export_touch

    @staticmethod
    def register_row(connection: sqlite3.Connection, file_id: str, row_id: str,
                     label_level: str | None, breakdown_plan: str | None) -> None:
        if label_level not in (None, "红", "黄", "绿"):
            raise invalid()
        optional_text(breakdown_plan, 20000)
        connection.execute("INSERT OR IGNORE INTO pwb_scene_state(file_id,row_id,label_level,breakdown_plan) VALUES (?,?,?,?)",
                           (file_id, row_id, label_level, breakdown_plan))

    def read(self, actor: Any, file_id: str, row_id: str) -> dict[str, Any]:
        return self._operate(actor, file_id, row_id, None, None, None)

    def update_scene(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                     label_level: str | None, breakdown_plan: str | None) -> dict[str, Any]:
        if label_level not in (None, "红", "黄", "绿"):
            raise invalid()
        change = (label_level, optional_text(breakdown_plan, 20000))
        return self._operate(actor, file_id, row_id, review_version(expected_version), change, None)

    def update_origins(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       origins: list[dict[str, str]]) -> dict[str, Any]:
        return self._operate(actor, file_id, row_id, review_version(expected_version), None,
                             origins_payload(origins, file_id, row_id))

    def _operate(self, actor: Any, file_id: str, row_id: str, expected: int | None,
                 fields: tuple[str | None, str | None] | None,
                 origins: list[dict[str, str]] | None) -> dict[str, Any]:
        if not valid_id(file_id) or not valid_id(row_id):
            raise invalid()
        connection = None
        try:
            connection = self.connect()
            connection.execute("PRAGMA foreign_keys=ON")
            connection.execute("BEGIN IMMEDIATE" if expected is not None else "BEGIN")
            base = self.access(connection, actor, file_id, row_id, write=expected is not None)
            state = connection.execute("SELECT version,label_level,breakdown_plan FROM pwb_scene_state WHERE file_id=? AND row_id=?",
                                       (file_id, row_id)).fetchone()
            if state is None:
                raise ApiFault(404, "NOT_FOUND", "节拍行不存在。")
            version, level, plan = state
            if expected is not None:
                if expected != version:
                    raise ApiFault(409, "VERSION_CONFLICT", "节拍已更新，请重新加载。")
                if version == 2**63 - 1:
                    raise invalid("版本已达上限。")
                if origins is not None:
                    for origin in origins:
                        self.origin_access(connection, actor, file_id, row_id, origin["file_id"], origin["row_id"])
                        # Every edge in this feature lives in this ledger. UNION
                        # deduplicates visits, including legacy accidental cycles.
                        cycle = connection.execute(
                            "WITH RECURSIVE ancestors(file_id,row_id) AS (VALUES(?,?) UNION "
                            "SELECT o.origin_file_id,o.origin_row_id FROM pwb_scene_origins o JOIN ancestors a "
                            "ON o.file_id=a.file_id AND o.row_id=a.row_id) "
                            "SELECT 1 FROM ancestors WHERE file_id=? AND row_id=? LIMIT 1",
                            (origin["file_id"], origin["row_id"], file_id, row_id)).fetchone()
                        if cycle:
                            raise invalid("来源关联不能形成循环。")
                    connection.execute("DELETE FROM pwb_scene_origins WHERE file_id=? AND row_id=?", (file_id, row_id))
                    connection.executemany("INSERT INTO pwb_scene_origins VALUES (?,?,?,?,?)",
                        [(file_id, row_id, origin["file_id"], origin["row_id"], ordinal) for ordinal, origin in enumerate(origins)])
                if fields is not None:
                    level, plan = fields
                connection.execute("UPDATE pwb_scene_state SET version=version+1,label_level=?,breakdown_plan=? "
                                   "WHERE file_id=? AND row_id=? AND version=?", (level, plan, file_id, row_id, expected))
                version += 1
                self.export_touch(connection, actor, file_id)
            source_rows = [{"file_id": item[0], "row_id": item[1]} for item in connection.execute(
                "SELECT origin_file_id,origin_row_id FROM pwb_scene_origins WHERE file_id=? AND row_id=? ORDER BY ordinal",
                (file_id, row_id))]
            # The base projection owns original text/context/legacy columns; only
            # explicitly editable fields and origin links overlay that source.
            result = {**base, "id": row_id, "version": version, "label_level": level,
                      "breakdown_plan": plan, "origins": source_rows}
            connection.commit()
            return result
        except sqlite3.Error:
            if connection is not None:
                connection.rollback()
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "存储暂不可用，请稍后再试。") from None
        except BaseException:
            if connection is not None:
                connection.rollback()
            raise
        finally:
            if connection is not None:
                connection.close()


def operation_fingerprint(metadata: dict[str, Any], source: BinaryIO | None = None,
                          length: int = 0, upload_limit: int = 64 * 1024 * 1024) -> str:
    """Bounded SHA-256 of canonical metadata + exact bytes, restores stream position.

    Include kind/project/template/filename/revision/length in metadata. HTTP spool is
    seekable; caller owns it. Never log this metadata or any source contents.
    """
    digest = hashlib.sha256(json.dumps(metadata, sort_keys=True, ensure_ascii=False,
                                       separators=(",", ":")).encode("utf-8") + b"\x00")
    if source is None:
        return digest.hexdigest()
    if length < 0 or length > upload_limit:
        raise ApiFault(413, "TOO_LARGE", "文件超过上传限制。")
    position = source.tell()
    try:
        source.seek(0)
        remaining = length
        while remaining:
            chunk = source.read(min(65536, remaining))
            if not chunk:
                raise invalid("上传未完成。")
            digest.update(chunk)
            remaining -= len(chunk)
        if source.read(1):
            raise invalid("文件长度不一致。")
    finally:
        source.seek(position)
    return digest.hexdigest()


def idempotent_result(connection: sqlite3.Connection, user_id: str, operation_id: str,
                      kind: str, fingerprint: str, work: Callable[[], dict[str, Any]]) -> dict[str, Any]:
    """Use INSIDE host BEGIN IMMEDIATE with current session/project/revision ACL checked.

    `work` persists business metadata on SAME connection and returns ProjectFile.
    This helper neither starts nor commits a transaction. Immutable staged blobs
    need host crash-safe publication; SQLite cannot roll back NAS writes.
    """
    if not connection.in_transaction or not valid_id(user_id) or not valid_id(operation_id) or kind not in {"create", "import"}:
        raise invalid("操作上下文无效。")
    stored = connection.execute("SELECT fingerprint,response_json FROM pwb_operations "
                                "WHERE user_id=? AND operation_id=? AND operation_kind=?",
                                (user_id, operation_id, kind)).fetchone()
    if stored is not None:
        if stored[0] != fingerprint:
            raise ApiFault(409, "VERSION_CONFLICT", "同一操作标识不能用于不同内容。")
        return json.loads(stored[1])
    result = work()
    encoded = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
    connection.execute("INSERT INTO pwb_operations VALUES (?,?,?,?,?,?)", (user_id, operation_id, kind, fingerprint,
        encoded, datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")))
    return result
