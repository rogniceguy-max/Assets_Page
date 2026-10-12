from __future__ import annotations

import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from backend.v2 import database
from backend.v2.identity import normalize_username, valid_internal_username
from backend.v2.passwords import hash_password, verify_password
from backend.v2.provisioner import ProvisionerError
from backend.v2.service import ApiError, V2Application


class FakeProvisioner:
    def __init__(self, failure: ProvisionerError | None = None):
        self.failure = failure
        self.recovery_failure: Exception | None = None
        self.rollback_failure: Exception | None = None
        self.created: list[dict[str, str]] = []
        self.rolled_back: list[dict[str, str]] = []
        self.recovery_status = "unknown"

    def create_account(self, **kwargs):
        if self.failure is not None:
            raise self.failure
        self.created.append(kwargs)

    def rollback_account(self, **kwargs):
        self.rolled_back.append(kwargs)
        if self.rollback_failure is not None:
            raise self.rollback_failure

    def get_account_status(self, **kwargs):
        if self.recovery_failure is not None:
            raise self.recovery_failure
        return self.recovery_status


class IdentityTests(unittest.TestCase):
    def test_username_trims_outer_space_and_preserves_internal_space(self):
        self.assertEqual(normalize_username("  王  小明  "), "王  小明")

    def test_username_normalizes_unicode_and_rejects_controls_or_separators(self):
        self.assertEqual(normalize_username("e\u0301"), "é")
        for value in ("", "   ", "a/b", "a\\b", "a\x00b", "a\n b"):
            with self.subTest(value=repr(value)):
                self.assertIsNone(normalize_username(value))

    def test_generated_linux_account_name_is_strict(self):
        self.assertTrue(valid_internal_username("myv2_0123456789abcdef01234567"))
        self.assertFalse(valid_internal_username("root"))
        self.assertFalse(valid_internal_username("myv2_0123"))


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
        self.provisioner = FakeProvisioner()
        self.app = V2Application(self.db_path, self.provisioner)

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_register_login_persistent_session_and_manual_logout(self):
        registered = self.app.register(
            {
                "username": "  王 小明 ",
                "password": "long-pass-123",
                "group_type": "film",
            }
        )
        token = registered["session_token"]
        self.assertEqual(registered["user"]["username"], "王 小明")
        self.assertEqual(registered["user"]["spaces"]["personal"].split("/")[-1][:5], "myv2_")
        self.assertEqual(len(self.provisioner.created), 1)
        self.assertEqual(self.provisioner.created[0]["username"], "王 小明")
        self.assertGreaterEqual(len(self.provisioner.created[0]["transaction_id"]), 40)

        session = self.app.session(token)
        self.assertTrue(session["authenticated"])
        self.assertEqual(session["user"]["department"], "影视资源库")

        logged_in = self.app.login({"username": "王 小明", "password": "long-pass-123"})
        self.assertTrue(self.app.session(logged_in["session_token"])["authenticated"])
        self.app.logout(token)
        with self.assertRaises(ApiError) as raised:
            self.app.session(token)
        self.assertEqual(raised.exception.status, 401)

        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            stored_hash, status = conn.execute(
                "SELECT password_hash, status FROM users"
            ).fetchone()
            stored_token_hashes = [row[0] for row in conn.execute("SELECT token_hash FROM sessions")]
        self.assertTrue(stored_hash.startswith("scrypt$"))
        self.assertEqual(status, "active")
        self.assertNotIn(token, stored_token_hashes)

    def test_duplicate_account_is_rejected_before_provisioning(self):
        payload = {"username": "测试员工", "password": "12345678", "group_type": "writing"}
        self.app.register(payload)
        with self.assertRaises(ApiError) as raised:
            self.app.register(payload)
        self.assertEqual(raised.exception.status, 409)
        self.assertEqual(len(self.provisioner.created), 1)

    def test_validation_rejects_invalid_department_and_newline_password(self):
        with self.assertRaises(ApiError):
            self.app.register({"username": "测试", "password": "12345678", "group_type": "admin"})
        with self.assertRaises(ApiError):
            self.app.register({"username": "测试", "password": "1234\n5678", "group_type": "film"})
        with self.assertRaises(ApiError):
            self.app.register({"username": "测试", "password": "x" * 1025, "group_type": "film"})
        self.assertEqual(self.provisioner.created, [])

    def test_provisioner_error_before_side_effect_removes_pending_user(self):
        self.provisioner.failure = ProvisionerError(
            "GROUP_UNAVAILABLE", "NAS 部门权限组尚未配置。", may_have_run=False
        )
        with self.assertRaises(ApiError):
            self.app.register({"username": "测试员工", "password": "12345678", "group_type": "film"})
        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM users").fetchone()[0], 0)
        self.assertEqual(self.provisioner.rolled_back, [])

    def test_uncertain_provisioner_result_requests_rollback(self):
        self.provisioner.failure = ProvisionerError(
            "PROVISIONER_UNAVAILABLE", "helper timeout", unavailable=True, may_have_run=True
        )
        with self.assertRaises(ApiError):
            self.app.register({"username": "测试员工", "password": "12345678", "group_type": "film"})
        self.assertEqual(len(self.provisioner.rolled_back), 1)

    def test_startup_recovers_root_created_account_after_process_crash(self):
        transaction_id = "A" * 43
        user_id = database.reserve_user(
            self.db_path,
            "恢复员工",
            "myv2_0123456789abcdef01234567",
            hash_password("long-pass-123"),
            "film",
            transaction_id,
        )
        self.provisioner.recovery_status = "active"

        recovered = V2Application(self.db_path, self.provisioner)
        logged_in = recovered.login({"username": "恢复员工", "password": "long-pass-123"})

        self.assertTrue(recovered.session(logged_in["session_token"])["authenticated"])
        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            status, token = conn.execute(
                "SELECT status, provisioning_token FROM users WHERE id=?", (user_id,)
            ).fetchone()
        self.assertEqual(status, "active")
        self.assertIsNone(token)

    def test_startup_removes_transaction_that_root_helper_rolled_back(self):
        database.reserve_user(
            self.db_path,
            "回滚员工",
            "myv2_0123456789abcdef01234567",
            hash_password("long-pass-123"),
            "film",
            "B" * 43,
        )
        self.provisioner.recovery_status = "rolled_back"

        V2Application(self.db_path, self.provisioner)

        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM users").fetchone()[0], 0)

    def test_startup_rolls_back_a_pending_root_transaction(self):
        user_id = database.reserve_user(
            self.db_path,
            "待回滚员工",
            "myv2_aaaaaaaaaaaaaaaaaaaaaaaa",
            hash_password("long-pass-123"),
            "writing",
            "C" * 43,
        )
        self.provisioner.recovery_status = "pending"

        V2Application(self.db_path, self.provisioner)

        self.assertEqual(len(self.provisioner.rolled_back), 1)
        self.assertEqual(self.provisioner.rolled_back[0]["transaction_id"], "C" * 43)
        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM users WHERE id=?", (user_id,)).fetchone()[0], 0)

    def test_startup_keeps_pending_row_if_scoped_rollback_fails(self):
        user_id = database.reserve_user(
            self.db_path,
            "待人工检查员工",
            "myv2_bbbbbbbbbbbbbbbbbbbbbbbb",
            hash_password("long-pass-123"),
            "film",
            "D" * 43,
        )
        self.provisioner.recovery_status = "pending"
        self.provisioner.rollback_failure = RuntimeError("helper unavailable")

        V2Application(self.db_path, self.provisioner)

        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            status, token = conn.execute(
                "SELECT status, provisioning_token FROM users WHERE id=?", (user_id,)
            ).fetchone()
        self.assertEqual(status, "provisioning")
        self.assertEqual(token, "D" * 43)

    def test_startup_removes_absent_transaction_but_keeps_unknown_or_unreachable(self):
        absent_id = database.reserve_user(
            self.db_path,
            "已不存在员工",
            "myv2_cccccccccccccccccccccccc",
            hash_password("long-pass-123"),
            "film",
            "E" * 43,
        )
        self.provisioner.recovery_status = "absent"
        V2Application(self.db_path, self.provisioner)
        with closing(sqlite3.connect(self.db_path)) as conn, conn:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM users WHERE id=?", (absent_id,)).fetchone()[0], 0)

        for index, failure in enumerate((None, RuntimeError("helper unavailable"))):
            user_id = database.reserve_user(
                self.db_path,
                "保留状态员工" + str(index),
                "myv2_" + ("d" if index == 0 else "e") * 24,
                hash_password("long-pass-123"),
                "writing",
                ("F" if index == 0 else "G") * 43,
            )
            self.provisioner.recovery_status = "unknown"
            self.provisioner.recovery_failure = failure
            V2Application(self.db_path, self.provisioner)
            with closing(sqlite3.connect(self.db_path)) as conn, conn:
                self.assertEqual(
                    conn.execute("SELECT status FROM users WHERE id=?", (user_id,)).fetchone()[0],
                    "provisioning",
                )
            self.provisioner.recovery_failure = None


if __name__ == "__main__":
    unittest.main()
