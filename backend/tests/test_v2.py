from __future__ import annotations

import hashlib
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from backend import database
from backend.identity import normalize_username
from backend.passwords import hash_password, verify_password
from backend.service import ApiError, V2Application


class IdentityTests(unittest.TestCase):
    def test_username_trims_outer_space_and_preserves_internal_space(self):
        self.assertEqual(normalize_username("  王  小明  "), "王  小明")

    def test_username_normalizes_unicode_and_rejects_controls_or_separators(self):
        self.assertEqual(normalize_username("e\u0301"), "é")
        for value in ("", "   ", "a/b", "a\\b", "a\x00b", "a\n b"):
            with self.subTest(value=repr(value)):
                self.assertIsNone(normalize_username(value))


class PasswordTests(unittest.TestCase):
    def test_scrypt_verifier_round_trip_and_exact_whitespace(self):
        encoded = hash_password("  12345678  ")
        self.assertTrue(verify_password("  12345678  ", encoded))
        self.assertFalse(verify_password("12345678", encoded))
        self.assertFalse(verify_password("wrong-pass", encoded))


class V2ServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.db_path = Path(self.temp_dir.name) / "v2.db"
        self.app = V2Application(self.db_path)

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_register_login_persistent_session_and_manual_logout(self):
        registered = self.app.register({
            "username": "  王 小明 ", "password": "long-pass-123", "group_type": "film"})
        token = registered["session_token"]
        self.assertEqual("王 小明", registered["user"]["username"])
        self.assertEqual(1, registered["user"]["id"])
        self.assertEqual("personal:1", registered["user"]["spaces"]["personal"])
        self.assertEqual("department:film", registered["user"]["spaces"]["film"])

        session = self.app.session(token)
        self.assertTrue(session["authenticated"])
        self.assertEqual("影视资源库", session["user"]["department"])
        restarted = V2Application(self.db_path)
        logged_in = restarted.login({"username": "王 小明", "password": "long-pass-123"})
        self.assertTrue(restarted.session(logged_in["session_token"])["authenticated"])
        restarted.logout(token)
        with self.assertRaises(ApiError) as raised:
            restarted.session(token)
        self.assertEqual(401, raised.exception.status)

        with closing(sqlite3.connect(self.db_path)) as conn:
            columns = {row[1] for row in conn.execute("PRAGMA table_info(users)")}
            stored_hash, status = conn.execute("SELECT password_hash,status FROM users").fetchone()
            stored_tokens = [row[0] for row in conn.execute("SELECT token_hash FROM sessions")]
        self.assertNotIn("system_username", columns)
        self.assertNotIn("provisioning_token", columns)
        self.assertTrue(stored_hash.startswith("scrypt$"))
        self.assertEqual("active", status)
        self.assertNotIn(token, stored_tokens)

    def test_duplicate_account_is_rejected_atomically(self):
        payload = {"username": "测试员工", "password": "12345678", "group_type": "writing"}
        self.app.register(payload)
        before = self._counts()
        with self.assertRaises(ApiError) as raised:
            self.app.register(payload)
        self.assertEqual(409, raised.exception.status)
        self.assertEqual(before, self._counts())

    def test_validation_rejects_invalid_department_and_newline_password(self):
        for payload in (
            {"username": "测试", "password": "12345678", "group_type": "admin"},
            {"username": "测试", "password": "1234\n5678", "group_type": "film"},
            {"username": "测试", "password": "x" * 1025, "group_type": "film"},
        ):
            with self.subTest(payload=payload), self.assertRaises(ApiError):
                self.app.register(payload)
        self.assertEqual((0, 0), self._counts())

    def test_legacy_os_account_schema_is_discarded_cleanly(self):
        legacy_db = Path(self.temp_dir.name) / "legacy.db"
        legacy_token_hash = hashlib.sha256(b"old-token").hexdigest()
        with closing(sqlite3.connect(legacy_db)) as conn:
            conn.execute("PRAGMA foreign_keys=ON")
            conn.executescript("""
                CREATE TABLE schema_version(version INTEGER NOT NULL);
                INSERT INTO schema_version VALUES(2);
                CREATE TABLE users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL,
                    username_key TEXT NOT NULL UNIQUE,
                    system_username TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    group_type TEXT NOT NULL,
                    provisioning_token TEXT,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE sessions (
                    token_hash TEXT PRIMARY KEY,
                    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL
                );
            """)
            conn.execute("INSERT INTO users VALUES(1,'旧员工','旧员工','myv2_0123456789abcdef01234567',?,'film',NULL,'active','2026-01-01T00:00:00Z')",
                         (hash_password("long-pass-123"),))
            conn.execute("INSERT INTO sessions VALUES(?,1,'2026-01-01T00:00:00Z')", (legacy_token_hash,))
            conn.commit()

        migrated = V2Application(legacy_db)
        with closing(sqlite3.connect(legacy_db)) as conn:
            user_count = conn.execute("SELECT COUNT(*) FROM users").fetchone()[0]
            session_count = conn.execute("SELECT COUNT(*) FROM sessions").fetchone()[0]
            columns = {row[1] for row in conn.execute("PRAGMA table_info(users)")}
            schema_version = conn.execute("SELECT version FROM schema_version").fetchone()[0]
            violations = conn.execute("PRAGMA foreign_key_check").fetchall()
        self.assertEqual((0, 0), (user_count, session_count))
        self.assertEqual(3, schema_version)
        self.assertTrue({"id", "username", "username_key", "password_hash", "group_type", "status"} <= columns)
        self.assertFalse({"system_username", "provisioning_token"} & columns)
        self.assertEqual([], violations)
        with self.assertRaises(ApiError):
            migrated.login({"username": "旧员工", "password": "long-pass-123"})
        fresh = migrated.register({"username": "旧员工", "password": "long-pass-123", "group_type": "film"})
        self.assertTrue(migrated.session(fresh["session_token"])["authenticated"])

    def _counts(self) -> tuple[int, int]:
        with closing(sqlite3.connect(self.db_path)) as conn:
            return (conn.execute("SELECT COUNT(*) FROM users").fetchone()[0],
                    conn.execute("SELECT COUNT(*) FROM sessions").fetchone()[0])


if __name__ == "__main__":
    unittest.main()
