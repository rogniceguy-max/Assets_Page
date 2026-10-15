from __future__ import annotations

import json
import secrets
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from backend import account_helper


class AccountHelperTransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir.name)
        self.manifest = self.root / "manifest.json"
        self.map_file = self.root / "username.map"
        self.lock_file = self.root / "helper.lock"
        self.system_username = "myv2_0123456789abcdef01234567"
        self.username = "王  小明"
        self.transaction_id = secrets.token_urlsafe(32)

    def tearDown(self):
        self.temp_dir.cleanup()

    def write_manifest(self, *, status="active", transaction_id=None):
        self.manifest.write_text(
            json.dumps({
                "version": 2,
                "entries": [{
                    "system_username": self.system_username,
                    "username": self.username,
                    "status": status,
                    "transaction_id": transaction_id or self.transaction_id,
                }],
            }, ensure_ascii=False),
            encoding="utf-8",
        )

    def test_rollback_rejects_an_unmatched_transaction_without_touching_accounts(self):
        self.write_manifest()
        with patch.object(account_helper, "MANIFEST_FILE", self.manifest), \
             patch.object(account_helper, "_remove_personal_directory") as remove_personal, \
             patch.object(account_helper, "_remove_samba_account") as remove_samba:
            with self.assertRaises(account_helper.HelperFailure) as raised:
                account_helper._rollback_uncommitted(
                    self.system_username, self.username, secrets.token_urlsafe(32)
                )

        self.assertEqual(raised.exception.code, "ROLLBACK_NOT_AUTHORIZED")
        remove_personal.assert_not_called()
        remove_samba.assert_not_called()

    def test_rollback_is_idempotent_only_for_the_matching_tombstone(self):
        self.write_manifest(status="rolled_back")
        with patch.object(account_helper, "MANIFEST_FILE", self.manifest), \
             patch.object(account_helper, "_remove_personal_directory") as remove_personal, \
             patch.object(account_helper, "_remove_samba_account") as remove_samba:
            account_helper._rollback_uncommitted(
                self.system_username, self.username, self.transaction_id
            )

        remove_personal.assert_not_called()
        remove_samba.assert_not_called()

    def test_manifest_map_includes_only_active_accounts_and_preserves_spaces(self):
        entries = [
            {
                "system_username": self.system_username,
                "username": self.username,
                "status": "active",
                "transaction_id": self.transaction_id,
            },
            {
                "system_username": "myv2_aaaaaaaaaaaaaaaaaaaaaaaa",
                "username": "待处理账号",
                "status": "pending",
                "transaction_id": secrets.token_urlsafe(32),
            },
        ]
        writes = []

        def capture(path: Path, data: bytes, mode: int = 0o600):
            writes.append((path, data, mode))

        with patch.object(account_helper, "MANIFEST_FILE", self.manifest), \
             patch.object(account_helper, "MAP_FILE", self.map_file), \
             patch.object(account_helper, "_write_atomic", side_effect=capture):
            account_helper._persist_manifest(entries)

        rendered_map = next(data.decode("utf-8") for path, data, _ in writes if path == self.map_file)
        self.assertIn(f'{self.system_username} = "王  小明"', rendered_map)
        self.assertNotIn("待处理账号", rendered_map)
        self.assertEqual([mode for _, _, mode in writes], [0o600, 0o600])

    def test_privileged_helper_enforces_the_accepted_password_maximum(self):
        request = {
            "username": self.username,
            "system_username": self.system_username,
            "group_type": "film",
            "password": "x" * 1025,
            "transaction_id": self.transaction_id,
        }
        with self.assertRaises(account_helper.HelperFailure) as raised:
            account_helper.create_account(request)
        self.assertEqual(raised.exception.code, "INVALID_PASSWORD")

    def test_recovery_does_not_activate_an_incomplete_active_manifest_entry(self):
        self.write_manifest(status="active")
        request = {
            "username": self.username,
            "system_username": self.system_username,
            "group_type": "film",
            "transaction_id": self.transaction_id,
        }
        with patch.object(account_helper, "MANIFEST_FILE", self.manifest), \
             patch.object(account_helper, "_active_account_is_complete", return_value=False), \
             patch.object(account_helper, "_reload_samba") as reload_samba:
            result = account_helper.get_account_status(request)

        self.assertEqual(result["status"], "pending")
        reload_samba.assert_not_called()

    def test_recovery_reloads_samba_before_confirming_a_complete_active_account(self):
        self.write_manifest(status="active")
        request = {
            "username": self.username,
            "system_username": self.system_username,
            "group_type": "film",
            "transaction_id": self.transaction_id,
        }
        with patch.object(account_helper, "MANIFEST_FILE", self.manifest), \
             patch.object(account_helper, "_active_account_is_complete", return_value=True), \
             patch.object(account_helper, "_reload_samba") as reload_samba:
            result = account_helper.get_account_status(request)

        self.assertEqual(result["status"], "active")
        reload_samba.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
