"""V2 identity API. File bytes stay on the native Windows SMB path."""

from __future__ import annotations

import json
import secrets
import sqlite3
from pathlib import Path
from typing import Any

from . import database
from .identity import normalize_username, valid_department
from .passwords import MIN_PASSWORD_LENGTH, hash_password, verify_password
from .provisioner import AccountProvisioner, ProvisionerError


DEPARTMENT_LABELS = {"film": "影视资源库", "writing": "文学创作库"}
MAX_PASSWORD_CHARS = 1024


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status = status
        self.code = code


class V2Application:
    """Use a separate V2 database and an injected NAS account provisioner."""

    def __init__(self, db_path: Path, provisioner: AccountProvisioner):
        self.db_path = db_path
        self.provisioner = provisioner
        database.init_db(db_path)
        self._recover_provisioning_users()

    def register(self, payload: dict[str, Any]) -> dict[str, Any]:
        username = normalize_username(payload.get("username"))
        password = payload.get("password")
        group_type = payload.get("group_type")

        if username is None:
            raise ApiError(400, "INVALID_USERNAME", "请输入有效的中文姓名。")
        if not isinstance(password, str) or len(password) < MIN_PASSWORD_LENGTH or len(password) > MAX_PASSWORD_CHARS:
            raise ApiError(400, "INVALID_PASSWORD", "密码长度需为 8 至 1024 位。")
        if "\n" in password or "\r" in password:
            raise ApiError(400, "INVALID_PASSWORD", "密码不能包含换行符。")
        if not valid_department(group_type):
            raise ApiError(400, "INVALID_DEPARTMENT", "请选择有效的所属部门。")

        system_username = "myv2_" + secrets.token_hex(12)
        transaction_id = secrets.token_urlsafe(32)
        password_hash = hash_password(password)
        try:
            user_id = database.reserve_user(
                self.db_path, username, system_username, password_hash, group_type, transaction_id
            )
        except sqlite3.IntegrityError:
            raise ApiError(409, "ACCOUNT_EXISTS", "该姓名已注册或存在账号冲突。") from None

        try:
            self.provisioner.create_account(
                username=username,
                system_username=system_username,
                password=password,
                group_type=group_type,
                transaction_id=transaction_id,
            )
        except ProvisionerError as error:
            if error.may_have_run:
                self._rollback(user_id, username, system_username, transaction_id)
            else:
                database.remove_user(self.db_path, user_id)
            raise ApiError(
                503 if error.unavailable else (409 if error.code in {"ACCOUNT_EXISTS", "ACCOUNT_COLLISION"} else 503),
                error.code,
                str(error),
            ) from None
        except Exception:
            self._rollback(user_id, username, system_username, transaction_id)
            raise ApiError(503, "PROVISIONING_FAILED", "NAS 账号开通失败，请稍后重试。") from None

        try:
            token = database.activate_user_and_create_session(self.db_path, user_id)
        except Exception:
            self._rollback(user_id, username, system_username, transaction_id)
            raise ApiError(503, "ACCOUNT_ACTIVATION_FAILED", "账号状态暂时无法保存。") from None

        return self._session_payload(
            user_id=user_id,
            username=username,
            system_username=system_username,
            group_type=group_type,
            token=token,
        )

    def login(self, payload: dict[str, Any]) -> dict[str, Any]:
        username = normalize_username(payload.get("username"))
        password = payload.get("password")
        if (
            username is None
            or not isinstance(password, str)
            or len(password) < MIN_PASSWORD_LENGTH
            or len(password) > MAX_PASSWORD_CHARS
            or "\n" in password
            or "\r" in password
        ):
            raise ApiError(401, "INVALID_CREDENTIALS", "用户名或密码不正确。")
        user = database.get_user_by_username(self.db_path, username)
        if user is None or not verify_password(password, user["password_hash"]):
            raise ApiError(401, "INVALID_CREDENTIALS", "用户名或密码不正确。")
        token = database.create_session(self.db_path, int(user["id"]))
        return self._session_payload(
            user_id=int(user["id"]),
            username=user["username"],
            system_username=user["system_username"],
            group_type=user["group_type"],
            token=token,
        )

    def session(self, token: str) -> dict[str, Any]:
        user_id = database.get_session_user_id(self.db_path, token)
        if user_id is None:
            raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")
        user = database.get_user_by_id(self.db_path, user_id)
        if user is None:
            database.revoke_session(self.db_path, token)
            raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")
        return {
            "authenticated": True,
            "user": self._user_payload(
                user_id=int(user["id"]),
                username=user["username"],
                system_username=user["system_username"],
                group_type=user["group_type"],
            ),
        }

    def logout(self, token: str) -> None:
        database.revoke_session(self.db_path, token)

    def _rollback(
        self, user_id: int, username: str, system_username: str, transaction_id: str
    ) -> None:
        try:
            self.provisioner.rollback_account(
                username=username,
                system_username=system_username,
                transaction_id=transaction_id,
            )
        except Exception:
            # Keep the provisioning record for later repair; never claim success.
            return
        database.remove_user(self.db_path, user_id)

    def _recover_provisioning_users(self) -> None:
        """Resolve crashes between root-side account creation and DB activation."""
        for user in database.list_provisioning_users(self.db_path):
            transaction_id = user.get("provisioning_token")
            if not isinstance(transaction_id, str):
                continue
            try:
                status = self.provisioner.get_account_status(
                    username=user["username"],
                    system_username=user["system_username"],
                    group_type=user["group_type"],
                    transaction_id=transaction_id,
                )
            except Exception:
                # Leave the durable provisioning row for the next startup/repair.
                continue

            if status == "active":
                database.activate_user(self.db_path, int(user["id"]))
            elif status in {"pending", "rolled_back", "absent"}:
                if status == "pending":
                    try:
                        self.provisioner.rollback_account(
                            username=user["username"],
                            system_username=user["system_username"],
                            transaction_id=transaction_id,
                        )
                    except Exception:
                        continue
                database.remove_user(self.db_path, int(user["id"]))

    @staticmethod
    def _user_payload(
        *, user_id: int, username: str, system_username: str, group_type: str
    ) -> dict[str, Any]:
        department = DEPARTMENT_LABELS[group_type]
        return {
            "id": user_id,
            "username": username,
            "department": department,
            "group_type": group_type,
            "spaces": {
                "film": "/01_影视资源库",
                "writing": "/02_文学创作库",
                "public": "/00_公共素材",
                # Return the account's private path directly; the root itself is
                # intentionally not enumerable by employee accounts.
                "personal": f"/03_个人空间/{system_username}",
            },
        }

    def _session_payload(
        self,
        *,
        user_id: int,
        username: str,
        system_username: str,
        group_type: str,
        token: str,
    ) -> dict[str, Any]:
        return {
            "success": True,
            "session_token": token,
            "user": self._user_payload(
                user_id=user_id,
                username=username,
                system_username=system_username,
                group_type=group_type,
            ),
        }
