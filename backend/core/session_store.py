# -*- coding: utf-8 -*-
"""Persistent browser sessions for the local mock service."""

import base64
import hashlib
import hmac
import secrets
from dataclasses import dataclass
from typing import Optional, Tuple

from database import create_session, delete_session, get_session_user_id


SESSION_COOKIE_NAME = "muying_session"
# The application itself does not expire sessions. This long-lived persistent
# cookie survives browser restarts; browsers may still apply their own limits.
SESSION_COOKIE_MAX_AGE_SECONDS = 2_147_483_647
CSRF_DERIVATION_CONTEXT = b"muying-drive-csrf-v1"


@dataclass(frozen=True)
class SessionContext:
    user_id: int
    csrf_token: str
    session_key: str


class PersistentSessionStore:
    """Store hashed session tokens in SQLite until the user logs out."""

    @staticmethod
    def _key(token: str) -> str:
        return hashlib.sha256(token.encode("ascii")).hexdigest()

    @classmethod
    def session_key_for_token(cls, token: str) -> Optional[str]:
        if not isinstance(token, str) or not token:
            return None
        try:
            return cls._key(token)
        except UnicodeEncodeError:
            return None

    @staticmethod
    def _csrf_token(token: str) -> str:
        digest = hmac.new(
            token.encode("ascii"), CSRF_DERIVATION_CONTEXT, hashlib.sha256
        ).digest()
        return base64.urlsafe_b64encode(digest).rstrip(b"=").decode("ascii")

    def create(self, user_id: int) -> Tuple[str, str]:
        token = secrets.token_urlsafe(32)
        session_key = self._key(token)
        csrf_token = self._csrf_token(token)
        create_session(session_key, user_id)
        return token, csrf_token

    def get(self, token: str) -> Optional[SessionContext]:
        session_key = self.session_key_for_token(token)
        if session_key is None:
            return None
        user_id = get_session_user_id(session_key)
        if user_id is None:
            return None
        return SessionContext(
            user_id=user_id,
            csrf_token=self._csrf_token(token),
            session_key=session_key,
        )

    def revoke(self, token: str) -> None:
        session_key = self.session_key_for_token(token)
        if session_key is not None:
            delete_session(session_key)


SESSION_STORE = PersistentSessionStore()
