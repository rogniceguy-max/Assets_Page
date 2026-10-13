"""Transactional review reference. No DB initialization or background work on import."""
from __future__ import annotations

import sqlite3
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Protocol
from uuid import uuid4

from .projects_api import ApiFault, invalid, json_response, optional_text, review_target, review_text, review_version, valid_id
from .workflow_services import ExportTouch


@dataclass(frozen=True)
class ReviewPrincipal:
    user_id: str
    user_name: str


@dataclass(frozen=True)
class ReviewPermissions:
    can_vote: bool
    can_comment: bool
    can_edit_own_comments: bool
    can_delete_own_comments: bool
    can_resolve_own_comments: bool = False
    can_resolve_all_comments: bool = False


class ReviewAccess(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str,
                 row_id: str, image_id: str | None, *, row_kind: str) -> ReviewPermissions:
        """Require original file/row read, image visibility and review-read ACL.

        Return independent review capabilities, NOT asset metadata-write permission.
        Query through this connection for transactional ACL consistency. For an
        old unlinked image, authorize the stable target/history without deleting it.
        Raise 403/404 ApiFault on denied read or nonexistent scope. No allow-all default.
        """
        ...


class CurrentTargets(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str,
                 row_id: str) -> list[str]:
        """Return current selected/candidate stable IDs in display order, same DB txn.

        Only real references from this row, never a global image catalog. Duplicates
        are allowed here and deduplicated by the store. Null general is added by it.
        """
        ...


class ReviewStore:
    """Implements ReviewServices. All dependencies required and explicitly supplied."""
    def __init__(self, connect: Callable[[], sqlite3.Connection],
                 principal: Callable[[Any], ReviewPrincipal], access: ReviewAccess,
                 targets: CurrentTargets, export_touch: ExportTouch, *, row_kind: str = "asset"):
        if row_kind not in {"asset", "scene"}:
            raise ValueError("row_kind must be asset or scene")
        self.connect, self.principal, self.access, self.targets = connect, principal, access, targets
        self.row_kind, self.export_touch = row_kind, export_touch

    def _access(self, connection: sqlite3.Connection, actor: Any, file_id: str,
                row_id: str, image_id: str | None) -> ReviewPermissions:
        if self.row_kind == "scene" and image_id is not None:
            raise invalid("节拍讨论不接受图片目标。")
        return self.access(connection, actor, file_id, row_id, image_id, row_kind=self.row_kind)

    def _transaction(self, actor: Any, file_id: str, row_id: str, write: bool,
                     operation: Callable[[sqlite3.Connection, ReviewPrincipal], Any]) -> Any:
        if not valid_id(file_id) or not valid_id(row_id):
            raise invalid()
        identity = self.principal(actor)  # Only the authenticated host principal supplies identity.
        if (not valid_id(identity.user_id) or not isinstance(identity.user_name, str)
                or not identity.user_name.strip() or len(identity.user_name) > 256
                or any(0xD800 <= ord(c) <= 0xDFFF for c in identity.user_name)):
            raise ApiFault(401, "SESSION_REQUIRED", "请重新登录。")
        connection = None
        try:
            connection = self.connect()
            connection.execute("BEGIN IMMEDIATE" if write else "BEGIN")
            result = operation(connection, identity)
            if write:
                # Reject a known oversized complete thread before committing a
                # mutation the client could never receive under this contract.
                json_response(200, result)
                self.export_touch(connection, actor, file_id)
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

    @staticmethod
    def _require(allowed: bool) -> None:
        if allowed is not True:
            raise ApiFault(403, "FORBIDDEN", "没有执行此操作的权限。")

    def summaries(self, actor: Any, file_id: str, row_id: str) -> list[dict[str, Any]]:
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> list[dict[str, Any]]:
            self._access(connection, actor, file_id, row_id, None)
            targets: list[str | None] = [None]
            for image_id in (self.targets(connection, actor, file_id, row_id) if self.row_kind == "asset" else []):
                review_target(image_id, allow_general=False)
                if image_id not in targets:
                    self._access(connection, actor, file_id, row_id, image_id)
                    targets.append(image_id)
            # Three grouped queries for the whole row, not three queries per thumbnail.
            counts = {(image, verdict): count for image, verdict, count in connection.execute(
                "SELECT image_id,verdict,COUNT(*) FROM pwb_review_votes_v3 "
                "WHERE file_id=? AND row_id=? AND row_kind=? GROUP BY image_id,verdict", (file_id, row_id, self.row_kind))}
            comments = dict(connection.execute(
                "SELECT scope_image_id,COUNT(*) FROM pwb_review_comments_v3 "
                "WHERE file_id=? AND row_id=? AND row_kind=? GROUP BY scope_image_id", (file_id, row_id, self.row_kind)))
            mine = dict(connection.execute(
                "SELECT image_id,verdict FROM pwb_review_votes_v3 WHERE file_id=? AND row_id=? AND row_kind=? AND user_id=?",
                (file_id, row_id, self.row_kind, identity.user_id)))
            return [{"image_id": image_id, "good": counts.get((image_id, "good"), 0),
                     "bad": counts.get((image_id, "bad"), 0),
                     "comment_count": comments.get(image_id or "", 0),
                     "my_verdict": mine.get(image_id)} for image_id in targets]
        return self._transaction(actor, file_id, row_id, False, operation)

    def _thread(self, connection: sqlite3.Connection, identity: ReviewPrincipal, file_id: str,
                row_id: str, image_id: str | None, permissions: ReviewPermissions) -> dict[str, Any]:
        counts = dict(connection.execute(
            "SELECT verdict,COUNT(*) FROM pwb_review_votes_v3 WHERE file_id=? AND row_id=? AND row_kind=? "
            "AND image_id=? GROUP BY verdict", (file_id, row_id, self.row_kind, image_id or "")))
        mine = connection.execute(
            "SELECT verdict FROM pwb_review_votes_v3 WHERE file_id=? AND row_id=? AND row_kind=? AND image_id=? AND user_id=?",
            (file_id, row_id, self.row_kind, image_id or "", identity.user_id)).fetchone()
        comments = []
        for item in connection.execute(
            "SELECT id,user_id,user_name,text,version,created_at,updated_at,resolved,resolution_note FROM pwb_review_comments_v3 "
            "WHERE file_id=? AND row_id=? AND row_kind=? AND scope_image_id=? ORDER BY created_at,id",
            (file_id, row_id, self.row_kind, image_id or "")):
            comment_id, user_id, user_name, text, version, created_at, updated_at, resolved, note = item
            own = user_id == identity.user_id
            comments.append({"id": comment_id, "user_id": user_id, "user_name": user_name,
                             "text": text, "version": version, "created_at": created_at,
                             "updated_at": updated_at,
                             "can_edit": own and permissions.can_edit_own_comments is True,
                             "can_delete": own and permissions.can_delete_own_comments is True,
                             "resolved": bool(resolved), "resolution_note": note,
                             "can_resolve": self._can_resolve(permissions, own)})
        return {"image_id": image_id, "good": counts.get("good", 0), "bad": counts.get("bad", 0),
                "my_verdict": mine[0] if mine else None, "comment_count": len(comments), "comments": comments}

    def thread(self, actor: Any, file_id: str, row_id: str, image_id: str | None) -> dict[str, Any]:
        image_id = review_target(image_id)
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> dict[str, Any]:
            permissions = self._access(connection, actor, file_id, row_id, image_id)
            return self._thread(connection, identity, file_id, row_id, image_id, permissions)
        return self._transaction(actor, file_id, row_id, False, operation)

    def vote(self, actor: Any, file_id: str, row_id: str, image_id: str,
             verdict: str | None) -> dict[str, Any]:
        image_id = review_target(image_id, allow_general=False)
        if verdict not in ("good", "bad", None):
            raise invalid()
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> dict[str, Any]:
            permissions = self._access(connection, actor, file_id, row_id, image_id)
            self._require(permissions.can_vote)
            if verdict is None:
                connection.execute(
                    "DELETE FROM pwb_review_votes_v3 WHERE file_id=? AND row_id=? AND row_kind=? AND image_id=? AND user_id=?",
                    (file_id, row_id, self.row_kind, image_id, identity.user_id))
            else:
                connection.execute(
                    "INSERT INTO pwb_review_votes_v3(file_id,row_id,row_kind,image_id,user_id,verdict) VALUES (?,?,?,?,?,?) "
                    "ON CONFLICT(file_id,row_id,row_kind,image_id,user_id) DO UPDATE SET verdict=excluded.verdict",
                    (file_id, row_id, self.row_kind, image_id, identity.user_id, verdict))
            return self._thread(connection, identity, file_id, row_id, image_id, permissions)
        return self._transaction(actor, file_id, row_id, True, operation)

    def add_comment(self, actor: Any, file_id: str, row_id: str, image_id: str | None,
                    text: str) -> dict[str, Any]:
        image_id, text = review_target(image_id), review_text(text)
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> dict[str, Any]:
            permissions = self._access(connection, actor, file_id, row_id, image_id)
            self._require(permissions.can_comment)
            timestamp = datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")
            connection.execute(
                "INSERT INTO pwb_review_comments_v3(id,file_id,row_id,row_kind,scope_image_id,user_id,user_name,text,"
                "created_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,?)",
                (uuid4().hex, file_id, row_id, self.row_kind, image_id or "", identity.user_id, identity.user_name,
                 text, timestamp, timestamp))
            return self._thread(connection, identity, file_id, row_id, image_id, permissions)
        return self._transaction(actor, file_id, row_id, True, operation)

    def edit_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                     expected_version: int, text: str) -> dict[str, Any]:
        return self._change_comment(actor, file_id, row_id, comment_id, expected_version, review_text(text))

    def delete_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                       expected_version: int) -> dict[str, Any]:
        return self._change_comment(actor, file_id, row_id, comment_id, expected_version, None)

    def _change_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                        expected_version: int, text: str | None) -> dict[str, Any]:
        if not valid_id(comment_id):
            raise invalid()
        expected_version = review_version(expected_version)
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> dict[str, Any]:
            record = connection.execute(
                "SELECT scope_image_id,user_id,version FROM pwb_review_comments_v3 WHERE id=? AND file_id=? AND row_id=? AND row_kind=?",
                (comment_id, file_id, row_id, self.row_kind)).fetchone()
            if record is None:
                raise ApiFault(404, "NOT_FOUND", "评论不存在。")
            scope_image_id, author_id, version = record
            image_id = scope_image_id or None
            permissions = self._access(connection, actor, file_id, row_id, image_id)
            self._require(author_id == identity.user_id)
            self._require(permissions.can_delete_own_comments if text is None else permissions.can_edit_own_comments)
            if version != expected_version:
                raise ApiFault(409, "VERSION_CONFLICT", "评论已更新，请重新加载后再修改。")
            if text is None:
                connection.execute("DELETE FROM pwb_review_comments_v3 WHERE id=? AND version=?",
                                   (comment_id, expected_version))
            else:
                if version == 2**63 - 1:
                    raise invalid("评论版本已达上限。")
                timestamp = datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")
                connection.execute(
                    "UPDATE pwb_review_comments_v3 SET text=?,version=version+1,updated_at=?,resolved=0,resolution_note=NULL WHERE id=? AND version=?",
                    (text, timestamp, comment_id, expected_version))
            return self._thread(connection, identity, file_id, row_id, image_id, permissions)
        return self._transaction(actor, file_id, row_id, True, operation)

    @staticmethod
    def _can_resolve(permissions: ReviewPermissions, own: bool) -> bool:
        return (permissions.can_resolve_all_comments is True
                or (own and permissions.can_resolve_own_comments is True))

    def resolve_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                        expected_version: int, resolved: bool,
                        resolution_note: str | None) -> dict[str, Any]:
        if not valid_id(comment_id) or type(resolved) is not bool:
            raise invalid()
        expected_version = review_version(expected_version)
        note = optional_text(resolution_note, 4000)
        note = note.strip() if note is not None else None
        if resolved and not note:
            raise invalid("已解决需要填写说明。")
        def operation(connection: sqlite3.Connection, identity: ReviewPrincipal) -> dict[str, Any]:
            record = connection.execute("SELECT scope_image_id,user_id,version FROM pwb_review_comments_v3 "
                "WHERE id=? AND file_id=? AND row_id=? AND row_kind=?", (comment_id, file_id, row_id, self.row_kind)).fetchone()
            if record is None:
                raise ApiFault(404, "NOT_FOUND", "评论不存在。")
            scope, author_id, version = record
            permissions = self._access(connection, actor, file_id, row_id, scope or None)
            self._require(self._can_resolve(permissions, author_id == identity.user_id))
            if version != expected_version:
                raise ApiFault(409, "VERSION_CONFLICT", "评论已更新，请重新加载。")
            if version == 2**63 - 1:
                raise invalid("评论版本已达上限。")
            timestamp = datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")
            connection.execute("UPDATE pwb_review_comments_v3 SET resolved=?,resolution_note=?,version=version+1,updated_at=? "
                               "WHERE id=? AND version=?", (int(resolved), note or None, timestamp, comment_id, version))
            return self._thread(connection, identity, file_id, row_id, scope or None, permissions)
        return self._transaction(actor, file_id, row_id, True, operation)
