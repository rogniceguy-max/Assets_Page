"""SQLite state for application users and long-lived sessions."""

from __future__ import annotations

import hashlib
import secrets
import sqlite3
from contextlib import closing
from pathlib import Path

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


def _create_users_table(conn: sqlite3.Connection, name: str = "users") -> None:
    conn.execute(f"""CREATE TABLE {name} (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT NOT NULL,
        username_key TEXT NOT NULL UNIQUE,
        password_hash TEXT NOT NULL,
        group_type TEXT NOT NULL CHECK (group_type IN ('film','writing')),
        status TEXT NOT NULL CHECK (status IN ('active','disabled')),
        created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
    )""")


def init_db(db_path: Path) -> None:
    """Create the current schema and migrate away per-user OS/Samba provisioning data.

    The old application account and session rows can be discarded. If the previous schema
    contains operating-system provisioning columns, those two auth tables are rebuilt cleanly.
    """
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = connect(db_path)
    try:
        conn.execute("PRAGMA journal_mode = WAL")
        # Rebuilding users must not cascade-delete the existing sessions table. The schema
        # transaction is atomic, and foreign keys are re-enabled before this connection closes.
        conn.execute("PRAGMA foreign_keys = OFF")
        conn.execute("BEGIN IMMEDIATE")
        conn.execute("""CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL)""")
        if conn.execute("SELECT 1 FROM schema_version LIMIT 1").fetchone() is None:
            conn.execute("INSERT INTO schema_version(version) VALUES(3)")

        users_exists = conn.execute("""SELECT 1 FROM sqlite_master
            WHERE type='table' AND name='users'""").fetchone() is not None
        if not users_exists:
            _create_users_table(conn)
        else:
            columns = {row["name"] for row in conn.execute("PRAGMA table_info(users)")}
            legacy_columns = {"system_username", "provisioning_token"} & columns
            if legacy_columns:
                conn.execute("DROP TABLE IF EXISTS sessions")
                conn.execute("DROP TABLE users")
                _create_users_table(conn)

        conn.execute("""CREATE TABLE IF NOT EXISTS sessions (
            token_hash TEXT PRIMARY KEY NOT NULL,
            user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        )""")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_v2_sessions_user_id ON sessions(user_id)")
        conn.execute("UPDATE schema_version SET version=3")
        conn.commit()
        conn.execute("PRAGMA foreign_keys = ON")
        violations = conn.execute("PRAGMA foreign_key_check").fetchall()
        if violations:
            raise RuntimeError("identity database migration left invalid foreign keys")
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def create_user_and_session(db_path: Path, username: str, password_hash: str,
                            group_type: str) -> tuple[int, str]:
    """Create a usable application identity and its first session in one transaction."""
    token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token.encode("ascii")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        cursor = conn.execute("""INSERT INTO users
            (username,username_key,password_hash,group_type,status)
            VALUES(?,?,?,?,'active')""",
            (username, username_key(username), password_hash, group_type))
        user_id = int(cursor.lastrowid)
        conn.execute("INSERT INTO sessions(token_hash,user_id) VALUES(?,?)", (token_hash, user_id))
    return user_id, token


def get_user_by_username(db_path: Path, username: str) -> dict[str, object] | None:
    with closing(connect(db_path)) as conn:
        row = conn.execute(
            "SELECT * FROM users WHERE username_key=? AND status='active'",
            (username_key(username),),
        ).fetchone()
    return dict(row) if row else None


def get_user_by_id(db_path: Path, user_id: int) -> dict[str, object] | None:
    with closing(connect(db_path)) as conn:
        row = conn.execute(
            "SELECT * FROM users WHERE id=? AND status='active'", (user_id,)
        ).fetchone()
    return dict(row) if row else None


def create_session(db_path: Path, user_id: int) -> str:
    token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token.encode("ascii")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        conn.execute("INSERT INTO sessions(token_hash,user_id) VALUES(?,?)", (token_hash, user_id))
    return token


def get_session_user_id(db_path: Path, token: str) -> int | None:
    if not isinstance(token, str) or len(token) > 256:
        return None
    token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    with closing(connect(db_path)) as conn:
        row = conn.execute("SELECT user_id FROM sessions WHERE token_hash=?", (token_hash,)).fetchone()
    return int(row[0]) if row else None


def revoke_session(db_path: Path, token: str) -> None:
    token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    with closing(connect(db_path)) as conn, conn:
        conn.execute("DELETE FROM sessions WHERE token_hash=?", (token_hash,))
