"""Ephemeral read-only visitor sessions for the SQLite-backed application APIs."""

from __future__ import annotations

import secrets
import threading
import time

from .service import ApiError

SESSION_TTL_SECONDS = 8 * 60 * 60
MAX_SESSIONS = 64


class VisitorService:
    """Issues login-free, read-only tokens; file and project reads use normal APIs."""

    def __init__(self, ttl_seconds: int = SESSION_TTL_SECONDS) -> None:
        if type(ttl_seconds) is not int or ttl_seconds <= 0:
            raise ValueError("ttl_seconds must be positive")
        self._ttl = ttl_seconds
        self._lock = threading.Lock()
        self._sessions: dict[str, float] = {}

    def start_session(self) -> dict[str, object]:
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
            "capabilities": {"read": True, "write": False},
        }

    def authorize(self, token: str | None) -> None:
        if not self.has_session(token):
            raise ApiError(401, "VISITOR_SESSION_INVALID", "参观会话已失效，请重新进入。")

    def has_session(self, token: str | None) -> bool:
        """True when the token names a live visitor session. Never raises."""
        if not token:
            return False
        now = time.monotonic()
        with self._lock:
            expiry = self._sessions.get(token)
            if expiry is None or expiry <= now:
                self._sessions.pop(token, None)
                return False
            return True

    def end_session(self, token: str | None) -> None:
        if not token:
            return
        with self._lock:
            self._sessions.pop(token, None)
