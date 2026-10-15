"""Anonymous feedback: separate SQLite store, no submitter identity or raw uploads."""
from __future__ import annotations

import base64
import binascii
import io
import re
import secrets
import sqlite3
import threading
import time
import warnings
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path

from .service import ApiError

MAX_IMAGE_BYTES = 2 * 1024 * 1024
MAX_TOTAL_BYTES = 6 * 1024 * 1024
MAX_ENCODED_BYTES = 6 * 1024 * 1024
MAX_BODY_BYTES = 9 * 1024 * 1024
MAX_STORAGE_BYTES = 1024 * 1024 * 1024
STATUSES = {
    "problem": ("pending", "in_progress", "resolved"),
    "suggestion": ("evaluating", "planned", "accepted", "declined"),
}
CATEGORIES = {"general", "upload", "download", "permission", "display", "interaction"}
ID_PATTERN = re.compile(r"^[a-f0-9]{32}$")
# A conservative pre-publication check, not a guarantee that prose/pixels are anonymous.
SENSITIVE = re.compile(
    r"(?i)(?:bearer\s+\S+|(?:password|passwd|token|secret|密码|令牌)\s*[:=：]\s*\S+|"
    r"[a-z]:[\\/]|\\\\[^\s]+|/(?:volume\d+|home|users|etc|var)/|"
    r"[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b(?:\d{1,3}\.){3}\d{1,3}\b|"
    r"https?://[^\s]+)"
)


def _invalid(message="反馈内容无效，请检查后重试。"):
    return ApiError(400, "FEEDBACK_INVALID", message)


def _text(value, maximum, minimum=1):
    if not isinstance(value, str):
        raise _invalid()
    value = value.strip()
    if not minimum <= len(value) <= maximum or any(ord(c) < 32 and c not in "\n\t" for c in value):
        raise _invalid()
    return value


def sanitize_image(payload):
    if not isinstance(payload, dict) or set(payload) != {"mime", "data"}:
        raise _invalid("截图格式无效。")
    if not isinstance(payload["mime"], str) or payload["mime"] not in {"image/png", "image/jpeg"} or not isinstance(payload["data"], str):
        raise _invalid("仅支持 PNG 和 JPEG 截图。")
    if len(payload["data"]) > ((MAX_IMAGE_BYTES + 2) // 3) * 4:
        raise ApiError(413, "IMAGE_TOO_LARGE", "每张截图最多 2 MiB。")
    try:
        raw = base64.b64decode(payload["data"], validate=True)
    except (ValueError, binascii.Error):
        raise _invalid("截图编码无效。") from None
    if not raw or len(raw) > MAX_IMAGE_BYTES:
        raise ApiError(413, "IMAGE_TOO_LARGE", "每张截图最多 2 MiB。")
    expected = "PNG" if payload["mime"] == "image/png" else "JPEG"
    if not (raw.startswith(b"\x89PNG\r\n\x1a\n") if expected == "PNG" else raw.startswith(b"\xff\xd8\xff")):
        raise _invalid("截图内容与格式不一致。")
    try:
        from PIL import Image, ImageOps
    except ImportError:
        raise ApiError(503, "IMAGE_SERVICE_UNAVAILABLE", "截图处理暂不可用，请稍后重试。") from None
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(raw)) as image:
                w, h = image.size
                if image.format != expected or getattr(image, "n_frames", 1) != 1 or w > 4096 or h > 4096 or w * h > 12_000_000:
                    raise ValueError("dimensions/format")
                image.verify()
            with Image.open(io.BytesIO(raw)) as image:
                image.load()
                image = ImageOps.exif_transpose(image).convert("RGBA" if expected == "PNG" else "RGB")
                # Fresh pixels, fresh encoder: discard EXIF, ICC, text and all original names.
                clean = Image.frombytes(image.mode, image.size, image.tobytes())
                output = io.BytesIO()
                clean.save(output, format="PNG")
                encoded = output.getvalue()
                if len(encoded) > MAX_ENCODED_BYTES:
                    raise ValueError("encoded size")
                return len(raw), encoded, clean.width, clean.height
    except Exception as error:
        if isinstance(error, ApiError):
            raise
        raise _invalid("截图损坏、尺寸过大或无法安全处理。") from None


class FeedbackService:
    def __init__(self, authenticate, root: Path, maintainers=(), visitor_authenticate=None):
        self.authenticate = authenticate
        # Optional second authority: a login-free tour session. Such a caller may read
        # public feedback and post anonymously, and never reaches a maintenance action,
        # because those require manage=True which only an employee session can satisfy.
        self.visitor_authenticate = visitor_authenticate
        self.root = root
        self.maintainers = frozenset(maintainers)
        self._lock = threading.Lock()
        self._recent = []  # Global ten-minute window; never keyed by user, IP or session.

    def authorize(self, token, manage=False):
        if not token:
            raise ApiError(401, "SESSION_REQUIRED", "请先登录。")
        try:
            session = self.authenticate(token)
        except ApiError:
            if not manage and self.visitor_authenticate is not None and self.visitor_authenticate(token):
                return False
            raise
        allowed = session["user"]["id"] in self.maintainers
        if manage and not allowed:
            raise ApiError(403, "FEEDBACK_FORBIDDEN", "仅授权维护人员可执行此操作。")
        return session["user"]["id"] if manage else allowed

    @contextmanager
    def connect(self, audit=False):
        db = None
        try:
            self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
            db = sqlite3.connect(self.root / ("maintenance.db" if audit else "feedback.db"), timeout=10)
            db.row_factory = sqlite3.Row
            if audit:
                db.execute("CREATE TABLE IF NOT EXISTS operations (time TEXT, maintainer INTEGER, feedback_id TEXT, status TEXT, visibility TEXT)")
            else:
                if db.execute("PRAGMA user_version").fetchone()[0] > 1:
                    raise ApiError(503, "FEEDBACK_SCHEMA_UNSUPPORTED", "反馈数据版本暂不支持，请联系维护人员。")
                db.executescript("""
                    CREATE TABLE IF NOT EXISTS feedback (
                        feedback_id TEXT PRIMARY KEY, kind TEXT NOT NULL, category TEXT NOT NULL,
                        title TEXT NOT NULL, description TEXT NOT NULL, status TEXT NOT NULL,
                        created_at TEXT NOT NULL, updated_at TEXT NOT NULL, visibility TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS attachments (
                        attachment_id TEXT PRIMARY KEY, feedback_id TEXT NOT NULL REFERENCES feedback,
                        width INTEGER NOT NULL, height INTEGER NOT NULL, data BLOB NOT NULL);
                    CREATE INDEX IF NOT EXISTS feedback_updated ON feedback(visibility, updated_at DESC);
                    CREATE INDEX IF NOT EXISTS attachment_feedback ON attachments(feedback_id);
                    PRAGMA user_version=1;
                """)
            with db:
                yield db
        except (sqlite3.Error, OSError):
            raise ApiError(503, "FEEDBACK_STORAGE_UNAVAILABLE", "反馈服务暂时不可用，请稍后重试。") from None
        finally:
            if db is not None:
                db.close()

    @staticmethod
    def now():
        return datetime.now(timezone.utc).isoformat(timespec="microseconds")

    def capabilities(self, token):
        return {"can_manage": self.authorize(token), "images_require_review": True}

    def _record(self, db, row):
        value = {k: row[k] for k in ("feedback_id", "kind", "category", "title", "description", "status", "created_at", "updated_at")}
        value["attachments"] = [dict(attachment_id=a["attachment_id"], mime="image/png", width=a["width"], height=a["height"], size=a["size"])
                                for a in db.execute("SELECT attachment_id,width,height,length(data) AS size FROM attachments WHERE feedback_id=?", (row["feedback_id"],))]
        return value

    def list(self, token, params):
        self.authorize(token)
        if set(params) - {"kind", "q", "page", "scope"} or any(len(v) != 1 for v in params.values()):
            raise _invalid()
        kind, query, scope = (params.get(k, [d])[0] for k, d in (("kind", "all"), ("q", ""), ("scope", "public")))
        if kind not in {"all", *STATUSES} or scope not in {"public", "pending", "hidden"} or len(query) > 100:
            raise _invalid()
        if scope != "public":
            self.authorize(token, manage=True)
        try:
            page = int(params.get("page", ["1"])[0])
            if not 1 <= page <= 10000:
                raise ValueError()
        except ValueError:
            raise _invalid() from None
        where, values = "visibility=?", [scope]
        if kind != "all":
            where += " AND kind=?"
            values.append(kind)
        if query:
            where += " AND (instr(lower(title),lower(?))>0 OR instr(lower(description),lower(?))>0)"
            values += [query, query]
        with self.connect() as db:
            rows = db.execute(f"SELECT * FROM feedback WHERE {where} ORDER BY updated_at DESC, feedback_id DESC LIMIT 21 OFFSET ?", (*values, (page - 1) * 20)).fetchall()
            return {"items": [self._record(db, row) for row in rows[:20]], "page": page, "has_more": len(rows) > 20}

    def _row(self, db, feedback_id):
        if not isinstance(feedback_id, str) or not ID_PATTERN.fullmatch(feedback_id):
            raise ApiError(404, "FEEDBACK_NOT_FOUND", "反馈不存在或暂未公开。")
        row = db.execute("SELECT * FROM feedback WHERE feedback_id=?", (feedback_id,)).fetchone()
        if row is None:
            raise ApiError(404, "FEEDBACK_NOT_FOUND", "反馈不存在或暂未公开。")
        return row

    def detail(self, token, feedback_id):
        manage = self.authorize(token)
        with self.connect() as db:
            row = self._row(db, feedback_id)
            if row["visibility"] != "public" and not manage:
                raise ApiError(404, "FEEDBACK_NOT_FOUND", "反馈不存在或暂未公开。")
            return self._record(db, row)

    def create(self, token, payload):
        self.authorize(token)
        if set(payload) != {"kind", "category", "title", "description", "images", "privacy_confirmed"}:
            raise _invalid("提交字段无效；反馈不可包含提交人信息。")
        kind, category = payload["kind"], payload["category"]
        if not isinstance(kind, str) or kind not in STATUSES or not isinstance(category, str) or category not in CATEGORIES or payload["privacy_confirmed"] is not True:
            raise _invalid()
        title, description = _text(payload["title"], 120), _text(payload["description"], 4000)
        if SENSITIVE.search(title + "\n" + description):
            raise _invalid("请删除账号、凭据、邮箱、服务器地址和内部路径后再提交。")
        images = payload["images"]
        if not isinstance(images, list) or len(images) > 4:
            raise _invalid("最多上传四张截图。")
        with self._lock:
            now = time.monotonic()
            self._recent = [t for t in self._recent if now - t < 600]
            if len(self._recent) >= 30:
                raise ApiError(429, "FEEDBACK_RATE_LIMIT", "反馈提交较频繁，请稍后重试。")
            self._recent.append(now)
        # Reject total size before allocating or decoding images.
        if sum(len(i.get("data", "")) if isinstance(i, dict) and isinstance(i.get("data"), str) else MAX_BODY_BYTES for i in images) > ((MAX_TOTAL_BYTES + 2) // 3) * 4 + 8:
            raise ApiError(413, "IMAGES_TOO_LARGE", "截图总大小最多 6 MiB。")
        cleaned = [sanitize_image(image) for image in images]
        if sum(i[0] for i in cleaned) > MAX_TOTAL_BYTES or sum(len(i[1]) for i in cleaned) > MAX_TOTAL_BYTES:
            raise ApiError(413, "IMAGES_TOO_LARGE", "截图总大小过大，请缩小图片。")
        feedback_id, stamp = secrets.token_hex(16), self.now()
        visibility = "pending" if cleaned else "public"
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            size = db.execute("SELECT coalesce(sum(length(data)),0) FROM attachments").fetchone()[0]
            count = db.execute("SELECT count(*) FROM feedback").fetchone()[0]
            if size + sum(len(i[1]) for i in cleaned) > MAX_STORAGE_BYTES or count >= 50000:
                raise ApiError(507, "FEEDBACK_QUOTA", "反馈存储空间不足，请联系维护人员。")
            db.execute("INSERT INTO feedback VALUES (?,?,?,?,?,?,?,?,?)", (feedback_id, kind, category, title, description, STATUSES[kind][0], stamp, stamp, visibility))
            for _, image, w, h in cleaned:
                db.execute("INSERT INTO attachments VALUES (?,?,?,?,?)", (secrets.token_hex(16), feedback_id, w, h, image))
            # Return confirmation without echoing potentially identifying image bytes.
        return {"feedback_id": feedback_id, "pending_review": visibility == "pending"}

    def attachment(self, token, attachment_id):
        manage = self.authorize(token)
        if not ID_PATTERN.fullmatch(attachment_id):
            raise ApiError(404, "ATTACHMENT_NOT_FOUND", "截图不可用。")
        with self.connect() as db:
            row = db.execute("SELECT a.data,f.visibility FROM attachments a JOIN feedback f ON f.feedback_id=a.feedback_id WHERE a.attachment_id=?", (attachment_id,)).fetchone()
            if row is None or (row["visibility"] != "public" and not manage):
                raise ApiError(404, "ATTACHMENT_NOT_FOUND", "截图不可用。")
            return bytes(row["data"])

    def update(self, token, feedback_id, payload):
        maintainer = self.authorize(token, manage=True)
        if set(payload) != {"status", "visibility"}:
            raise _invalid()
        with self.connect() as db:
            # SQLite's rollback journal transaction spans both databases on the
            # same local filesystem: status and isolated audit commit together.
            db.execute("ATTACH DATABASE ? AS maintenance", (str(self.root / "maintenance.db"),))
            db.execute("CREATE TABLE IF NOT EXISTS maintenance.operations (time TEXT, maintainer INTEGER, feedback_id TEXT, status TEXT, visibility TEXT)")
            db.execute("BEGIN IMMEDIATE")
            row = self._row(db, feedback_id)
            if not isinstance(payload["status"], str) or not isinstance(payload["visibility"], str) or payload["status"] not in STATUSES[row["kind"]] or payload["visibility"] not in {"public", "hidden", "pending"}:
                raise _invalid()
            stamp = self.now()
            db.execute("INSERT INTO maintenance.operations VALUES (?,?,?,?,?)", (stamp, maintainer, feedback_id, payload["status"], payload["visibility"]))
            db.execute("UPDATE feedback SET status=?,visibility=?,updated_at=? WHERE feedback_id=?", (payload["status"], payload["visibility"], stamp, feedback_id))
            return self._record(db, self._row(db, feedback_id))
