# -*- coding: utf-8 -*-
"""SQLite persistence and password hashing for MuYing Hub."""

import base64
import hashlib
import hmac
import os
import sqlite3
import secrets
import unicodedata
from contextlib import closing
from typing import Optional

from config import DB_PATH, DATA_DIR


# Versioned/self-describing scrypt parameters. Keep verification bounded to
# this local baseline until production-format compatibility is confirmed.
SCRYPT_N = 16384
SCRYPT_R = 8
SCRYPT_P = 1
SCRYPT_DKLEN = 64
SCRYPT_SALT_BYTES = 16


def normalize_username(username: str) -> Optional[str]:
    """Trim outer whitespace and reject controls and path separators."""
    if not isinstance(username, str):
        return None
    try:
        username.encode("utf-8")
    except UnicodeEncodeError:
        return None
    if any(
        unicodedata.category(char) == "Cc" or char in {"/", "\\"}
        for char in username
    ):
        return None
    normalized = username.strip()
    return normalized or None


def _password_bytes(password: str) -> bytes:
    if not isinstance(password, str):
        raise TypeError("password must be a string")
    # Do not strip or otherwise normalize the password.
    return password.encode("utf-8")


def hash_password(password: str) -> str:
    """Return a self-describing scrypt verifier; never return/store plaintext."""
    salt = secrets.token_bytes(SCRYPT_SALT_BYTES)
    digest = hashlib.scrypt(
        _password_bytes(password),
        salt=salt,
        n=SCRYPT_N,
        r=SCRYPT_R,
        p=SCRYPT_P,
        dklen=SCRYPT_DKLEN,
    )
    salt_text = base64.b64encode(salt).decode("ascii")
    digest_text = base64.b64encode(digest).decode("ascii")
    return (
        f"scrypt$N={SCRYPT_N},r={SCRYPT_R},p={SCRYPT_P},dklen={SCRYPT_DKLEN}"
        f"${salt_text}${digest_text}"
    )


def verify_password(password: str, encoded: str) -> bool:
    """Verify a local scrypt record using a constant-time digest comparison."""
    if not isinstance(password, str) or not isinstance(encoded, str):
        return False

    try:
        algorithm, parameters, salt_text, digest_text = encoded.split("$")
        expected_parameters = (
            f"N={SCRYPT_N},r={SCRYPT_R},p={SCRYPT_P},dklen={SCRYPT_DKLEN}"
        )
        if algorithm != "scrypt" or parameters != expected_parameters:
            return False

        salt = base64.b64decode(salt_text, validate=True)
        expected_digest = base64.b64decode(digest_text, validate=True)
        if len(salt) != SCRYPT_SALT_BYTES or len(expected_digest) != SCRYPT_DKLEN:
            return False

        actual_digest = hashlib.scrypt(
            _password_bytes(password),
            salt=salt,
            n=SCRYPT_N,
            r=SCRYPT_R,
            p=SCRYPT_P,
            dklen=SCRYPT_DKLEN,
        )
        return hmac.compare_digest(actual_digest, expected_digest)
    except (ValueError, TypeError, UnicodeEncodeError):
        return False


def init_db() -> None:
    """Create the local application schema without storing raw passwords or IPs."""
    os.makedirs(DATA_DIR, exist_ok=True)
    with closing(sqlite3.connect(DB_PATH)) as conn:
        with conn:
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT UNIQUE NOT NULL,
                    linux_username TEXT UNIQUE NOT NULL,
                    display_name TEXT,
                    password_hash TEXT NOT NULL,
                    group_type TEXT NOT NULL,
                    register_ip TEXT, -- retained as an unused compatibility field
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    last_login TIMESTAMP,
                    status TEXT DEFAULT 'active'
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS download_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL,
                    client_ip TEXT,
                    downloaded_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    session_key TEXT PRIMARY KEY NOT NULL,
                    user_id INTEGER NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
                """
            )
            conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_sessions_user_id ON sessions(user_id)"
            )


def get_user_by_username(username: str) -> Optional[dict]:
    with closing(sqlite3.connect(DB_PATH)) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute(
            "SELECT * FROM users WHERE username = ?", (username,)
        ).fetchone()
        return dict(row) if row else None


def get_user_by_id(user_id: int) -> Optional[dict]:
    with closing(sqlite3.connect(DB_PATH)) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute(
            "SELECT * FROM users WHERE id = ?", (user_id,)
        ).fetchone()
        return dict(row) if row else None


def add_user(username: str, password: str, group_type: str) -> int:
    """Persist a username and scrypt verifier, returning its internal row ID."""
    username = normalize_username(username)
    if username is None:
        raise ValueError("invalid username")
    password_hash = hash_password(password)
    with closing(sqlite3.connect(DB_PATH)) as conn:
        with conn:
            cursor = conn.execute(
                """
                INSERT INTO users (
                    username, linux_username, display_name, password_hash, group_type
                ) VALUES (?, ?, ?, ?, ?)
                """,
                (username, username, username, password_hash, group_type),
            )
            user_id = cursor.lastrowid
    return int(user_id)


def remove_user(user_id: int) -> None:
    """Remove a just-created local account if its mock directory cannot be made."""
    with closing(sqlite3.connect(DB_PATH)) as conn:
        with conn:
            conn.execute("DELETE FROM sessions WHERE user_id = ?", (user_id,))
            conn.execute("DELETE FROM users WHERE id = ?", (user_id,))


def create_session(session_key: str, user_id: int) -> None:
    """Persist only the one-way hash of a browser session token."""
    with closing(sqlite3.connect(DB_PATH)) as conn:
        with conn:
            conn.execute(
                "INSERT INTO sessions (session_key, user_id) VALUES (?, ?)",
                (session_key, user_id),
            )


def get_session_user_id(session_key: str) -> Optional[int]:
    with closing(sqlite3.connect(DB_PATH)) as conn:
        row = conn.execute(
            "SELECT user_id FROM sessions WHERE session_key = ?", (session_key,)
        ).fetchone()
        return int(row[0]) if row else None


def delete_session(session_key: str) -> None:
    with closing(sqlite3.connect(DB_PATH)) as conn:
        with conn:
            conn.execute("DELETE FROM sessions WHERE session_key = ?", (session_key,))
