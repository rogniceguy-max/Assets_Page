"""Authentication and comment-review adapters used by the active SQLite workbook API."""
from __future__ import annotations

import secrets
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping

from .projects_api import ApiFault


class HostSecurity:
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


def _asset_component(value: Any, label: str) -> str | None:
    if value is None:
        return None
    if (not isinstance(value, str) or len(value) > 200 or len(value.encode("utf-8", "surrogatepass")) > 200
            or value in {".", ".."} or any(character in value for character in "/\\")
            or any(ord(character) < 32 or ord(character) == 127 or 0xD800 <= ord(character) <= 0xDFFF
                   for character in value)):
        raise ApiFault(422, "INVALID_REQUEST", f"{label}无效。")
    return value or None


def _project_directories(root: Path) -> list[Path]:
    try:
        entries = list(root.iterdir())
    except OSError as error:
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "项目目录暂时不可用。") from error
    directories = [entry for entry in entries
                   if not entry.name.startswith(".") and entry.is_dir() and not entry.is_symlink()]
    directories.sort(key=lambda entry: entry.name)
    return directories


class SqliteReviewServices:
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
                    updated_at TEXT NOT NULL
                )
            """)
            review_columns = {row[1] for row in conn.execute("PRAGMA table_info(project_reviews)")}
            if "resolved" in review_columns:
                conn.execute("UPDATE project_reviews SET resolved=0")
            if "resolution_note" in review_columns:
                conn.execute("UPDATE project_reviews SET resolution_note=NULL")
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

    @staticmethod
    def _require_writable(actor: Any) -> None:
        if not isinstance(actor, dict):
            return
        user = actor.get("user", {})
        if not isinstance(user, dict):
            user = {}
        role = actor.get("role") or user.get("role")
        can_write = actor.get("can_write", user.get("can_write", True))
        if role == "visitor" or user.get("group_type") == "visitor" or can_write is False:
            raise ApiFault(403, "FORBIDDEN", "访客账户只有只读权限。")

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
        self._require_writable(actor)
        user = actor.get("user", {}) if isinstance(actor, dict) else {}
        user_id = str(user.get("id", "0"))
        user_name = str(user.get("username", "user"))
        comment_id = secrets.token_hex(16)
        now = datetime.now(timezone.utc).isoformat()
        with self._connect() as conn:
            conn.execute("""
                INSERT INTO project_reviews (id, file_id, row_kind, row_id, image_id, user_id, user_name, text, version, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?)
            """, (comment_id, file_id, self.row_kind, row_id, image_id, user_id, user_name, text, now, now))
        return self.thread(actor, file_id, row_id, image_id)

    def edit_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                     expected_version: int, text: str) -> dict[str, Any]:
        self._require_writable(actor)
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
        self._require_writable(actor)
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

    def vote(self, actor: Any, file_id: str, row_id: str, image_id: str,
             verdict: str | None) -> dict[str, Any]:
        self._require_writable(actor)
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
