"""SQLite state for the parallel V2 identity and long-lived sessions."""

from __future__ import annotations

import hashlib
import secrets
import sqlite3
from contextlib import closing
from pathlib import Path
from typing import Any

from .identity import username_key


def connect(db_path: Path) -> sqlite3.Connection:
    conn = sqlite3.connect(db_path, timeout=30)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    conn.execute("PRAGMA busy_timeout = 30000")
    conn.execute("PRAGMA synchronous = NORMAL")
    conn.execute("PRAGMA cache_size = -4000")
    conn.execute("PRAGMA temp_store = MEMORY")
    return conn


def init_db(db_path: Path) -> None:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    with closing(connect(db_path)) as conn, conn:
        conn.execute("PRAGMA journal_mode = WAL")
        conn.execute("PRAGMA synchronous = NORMAL")
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );
            INSERT INTO schema_version(version)
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);

            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL,
                username_key TEXT NOT NULL UNIQUE,
                system_username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                group_type TEXT NOT NULL CHECK (group_type IN ('film', 'writing')),
                provisioning_token TEXT,
                status TEXT NOT NULL CHECK (status IN ('provisioning', 'active', 'disabled')),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );

            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY NOT NULL,
                user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_v2_sessions_user_id ON sessions(user_id);
            """
        )
        columns = {row[1] for row in conn.execute("PRAGMA table_info(users)")}
        if "provisioning_token" not in columns:
            conn.execute("ALTER TABLE users ADD COLUMN provisioning_token TEXT")
        conn.execute("UPDATE schema_version SET version=2")


def reserve_user(
    db_path: Path,
    username: str,
    system_username: str,
    password_hash: str,
    group_type: str,
    provisioning_token: str,
) -> int:
    with closing(connect(db_path)) as conn, conn:
        cursor = conn.execute(
            """INSERT INTO users(
                   username, username_key, system_username, password_hash, group_type,
                   provisioning_token, status
               ) VALUES (?, ?, ?, ?, ?, ?, 'provisioning')""",
            (username, username_key(username), system_username, password_hash, group_type, provisioning_token),
        )
        return int(cursor.lastrowid)


def activate_user(db_path: Path, user_id: int) -> None:
    with closing(connect(db_path)) as conn, conn:
        cursor = conn.execute(
            "UPDATE users SET status='active', provisioning_token=NULL WHERE id=? AND status='provisioning'",
            (user_id,),
        )
        if cursor.rowcount != 1:
            raise RuntimeError("V2 account activation state is invalid")


def activate_user_and_create_session(db_path: Path, user_id: int) -> str:
    """Atomically make a provisioned account active and create its first session."""
    token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token.encode("ascii")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        cursor = conn.execute(
            "UPDATE users SET status='active', provisioning_token=NULL WHERE id=? AND status='provisioning'",
            (user_id,),
        )
        if cursor.rowcount != 1:
            raise RuntimeError("V2 account activation state is invalid")
        conn.execute(
            "INSERT INTO sessions(token_hash, user_id) VALUES (?, ?)",
            (token_hash, user_id),
        )
    return token


def remove_user(db_path: Path, user_id: int) -> None:
    with closing(connect(db_path)) as conn, conn:
        conn.execute("DELETE FROM users WHERE id=? AND status='provisioning'", (user_id,))


def list_provisioning_users(db_path: Path) -> list[dict[str, Any]]:
    with closing(connect(db_path)) as conn:
        rows = conn.execute(
            "SELECT id, username, system_username, group_type, provisioning_token FROM users WHERE status='provisioning'"
        ).fetchall()
    return [dict(row) for row in rows]


def get_user_by_username(db_path: Path, username: str) -> dict[str, Any] | None:
    with closing(connect(db_path)) as conn:
        row = conn.execute(
            "SELECT * FROM users WHERE username_key=? AND status='active'",
            (username_key(username),),
        ).fetchone()
    return dict(row) if row else None


def get_user_by_id(db_path: Path, user_id: int) -> dict[str, Any] | None:
    with closing(connect(db_path)) as conn:
        row = conn.execute(
            "SELECT * FROM users WHERE id=? AND status='active'", (user_id,)
        ).fetchone()
    return dict(row) if row else None


def create_session(db_path: Path, user_id: int) -> str:
    token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token.encode("ascii")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        conn.execute(
            "INSERT INTO sessions(token_hash, user_id) VALUES (?, ?)",
            (token_hash, user_id),
        )
    return token


def get_session_user_id(db_path: Path, token: str) -> int | None:
    if not isinstance(token, str) or len(token) > 256:
        return None
    token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    with closing(connect(db_path)) as conn:
        row = conn.execute(
            "SELECT user_id FROM sessions WHERE token_hash=?", (token_hash,)
        ).fetchone()
    return int(row[0]) if row else None


def revoke_session(db_path: Path, token: str) -> None:
    token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        conn.execute("DELETE FROM sessions WHERE token_hash=?", (token_hash,))
