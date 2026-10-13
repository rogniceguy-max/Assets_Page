"""Login-free visitor access: revocable read-only sessions over a privileged reader.

The visitor never receives SMB credentials and the client never opens a second Windows
SMB identity — that would collide with the employee's own NAS connection. Reads go
through the root-side helper, which drops to the dedicated visitor identity before
touching the filesystem, so the UGOS ACL is what decides what is visible.

Session tokens and their expiry live in memory only: a restart revokes every visitor
session, and there is nothing to clean up on disk.
"""
from __future__ import annotations

import json
import secrets
import socket
import threading
import time
from pathlib import Path
from typing import Any

from .service import ApiError

MAX_BODY_BYTES = 8 * 1024
MAX_HELPER_MESSAGE_BYTES = 8 * 1024
MAX_READ_BYTES = 4 * 1024 * 1024

SESSION_TTL_SECONDS = 8 * 60 * 60
MAX_SESSIONS = 64

# The visitor sees exactly these three spaces, all read-only. Personal spaces are not
# part of the contract: they are absent here and the ACL denies them outright.
SPACES: tuple[dict[str, str], ...] = (
    {"id": "film", "title": "影视资源库"},
    {"id": "writing", "title": "文学创作库"},
    {"id": "public", "title": "公共素材"},
)
SPACE_IDS = frozenset(space["id"] for space in SPACES)


class VisitorUnavailable(RuntimeError):
    """Raised when the privileged reader cannot be reached at all."""


class VisitorReader:
    """Client for the root-side visitor helper over its Unix socket."""

    def __init__(self, socket_path: Path | None, timeout_seconds: float = 30):
        self.socket_path = socket_path
        self.timeout_seconds = timeout_seconds

    def list(self, space: str, relative_path: str) -> dict[str, Any]:
        header, payload = self._call({"space": space, "action": "list", "relativePath": relative_path})
        try:
            return json.loads(payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise VisitorUnavailable("参观通道返回无效结果。") from None

    def read(self, space: str, relative_path: str, offset: int, length: int) -> tuple[bytes, int]:
        header, payload = self._call({
            "space": space, "action": "read", "relativePath": relative_path,
            "offset": offset, "length": length,
        })
        size = header.get("size")
        return payload, size if isinstance(size, int) and size >= 0 else len(payload)

    def _call(self, payload: dict[str, Any]) -> tuple[dict[str, Any], bytes]:
        if self.socket_path is None:
            raise VisitorUnavailable("参观通道尚未配置。")

        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        if len(encoded) > MAX_HELPER_MESSAGE_BYTES:
            raise ApiError(400, "VISITOR_REQUEST_TOO_LARGE", "参观请求过长。")

        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                channel.settimeout(self.timeout_seconds)
                channel.connect(str(self.socket_path))
                channel.sendall(encoded)
                stream = channel.makefile("rb")
                line = stream.readline(MAX_HELPER_MESSAGE_BYTES + 1)
                if not line or len(line) > MAX_HELPER_MESSAGE_BYTES:
                    raise VisitorUnavailable("参观通道返回无效结果。")
                header = json.loads(line.decode("utf-8"))
                if not isinstance(header, dict):
                    raise VisitorUnavailable("参观通道返回无效结果。")
                if header.get("ok") is not True:
                    raise ApiError(
                        _http_status_for(header.get("code")),
                        header.get("code") if isinstance(header.get("code"), str) else "VISITOR_FAILED",
                        header.get("message") if isinstance(header.get("message"), str) else "参观请求失败。",
                    )
                length = header.get("length")
                if not isinstance(length, int) or length < 0 or length > MAX_READ_BYTES + MAX_HELPER_MESSAGE_BYTES:
                    raise VisitorUnavailable("参观通道返回无效结果。")
                body = stream.read(length) if length else b""
                if len(body) != length:
                    raise VisitorUnavailable("参观通道返回不完整。")
                return header, body
        except (OSError, TimeoutError, ValueError):
            raise VisitorUnavailable("参观通道暂时不可用。") from None


def _http_status_for(code: Any) -> int:
    return {
        "NOT_FOUND": 404,
        "ACCESS_DENIED": 403,
        "INVALID_PATH": 400,
        "INVALID_RANGE": 400,
        "UNKNOWN_SPACE": 404,
        "NOT_A_FILE": 400,
    }.get(code, 503)


class VisitorService:
    """Sessions and read operations for the login-free tour."""

    def __init__(self, reader: VisitorReader, ttl_seconds: int = SESSION_TTL_SECONDS):
        self._reader = reader
        self._ttl = ttl_seconds
        self._lock = threading.Lock()
        self._sessions: dict[str, float] = {}

    def start_session(self) -> dict[str, Any]:
        now = time.monotonic()
        with self._lock:
            self._sessions = {token: expiry for token, expiry in self._sessions.items() if expiry > now}
            if len(self._sessions) >= MAX_SESSIONS:
                raise ApiError(503, "VISITOR_BUSY", "参观人数已满，请稍后再试。")
            token = secrets.token_urlsafe(32)
            self._sessions[token] = now + self._ttl
        return {
            "role": "visitor",
            "session_token": token,
            "expires_in": self._ttl,
            "spaces": [dict(space) for space in SPACES],
            "capabilities": {"read": True, "write": False},
        }

    def authorize(self, token: str | None) -> None:
        if not token:
            raise ApiError(401, "VISITOR_SESSION_REQUIRED", "请先进入参观模式。")
        with self._lock:
            expiry = self._sessions.get(token)
            if expiry is None or expiry <= time.monotonic():
                self._sessions.pop(token, None)
                raise ApiError(401, "VISITOR_SESSION_INVALID", "参观会话已失效，请重新进入。")

    def has_session(self, token: str | None) -> bool:
        """True when the token names a live visitor session. Never raises."""
        if not token:
            return False
        with self._lock:
            expiry = self._sessions.get(token)
            return expiry is not None and expiry > time.monotonic()

    def end_session(self, token: str | None) -> None:
        if not token:
            return
        with self._lock:
            self._sessions.pop(token, None)

    def list(self, token: str | None, space: Any, relative_path: Any) -> dict[str, Any]:
        self.authorize(token)
        space_id = self._require_space(space)
        payload = self._reader.list(space_id, _require_relative_path(relative_path))
        entries = payload.get("entries")
        return {
            "space": space_id,
            "path": _require_relative_path(relative_path),
            "entries": entries if isinstance(entries, list) else [],
            "truncated": bool(payload.get("truncated")),
        }

    def read(self, token: str | None, space: Any, relative_path: Any, offset: int, length: int) -> tuple[bytes, int]:
        self.authorize(token)
        space_id = self._require_space(space)
        return self._reader.read(space_id, _require_relative_path(relative_path), offset, length)

    @staticmethod
    def _require_space(space: Any) -> str:
        if not isinstance(space, str) or space not in SPACE_IDS:
            raise ApiError(404, "VISITOR_SPACE_UNKNOWN", "参观空间不存在。")
        return space


def _require_relative_path(value: Any) -> str:
    if value is None:
        return ""
    if not isinstance(value, str) or len(value) > 1024:
        raise ApiError(400, "VISITOR_PATH_INVALID", "参观路径无效。")
    if "\x00" in value or any(ord(character) < 32 for character in value):
        raise ApiError(400, "VISITOR_PATH_INVALID", "参观路径无效。")
    if value.startswith("/") or ":" in value:
        raise ApiError(400, "VISITOR_PATH_INVALID", "参观路径无效。")
    # The helper re-checks everything; this keeps obviously bad input from reaching it.
    if any(segment == ".." for segment in value.replace("\\", "/").split("/")):
        raise ApiError(400, "VISITOR_PATH_INVALID", "参观路径无效。")
    return value.strip("/")
