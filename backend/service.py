"""V2 application identity API backed only by SQLite credentials and sessions."""

from __future__ import annotations

import sqlite3
from pathlib import Path
from typing import Any

from . import database
from .identity import normalize_username, valid_department
from .passwords import MIN_PASSWORD_LENGTH, hash_password, verify_password


DEPARTMENT_LABELS = {"film": "影视资源库", "writing": "文学创作库"}
MAX_PASSWORD_CHARS = 1024


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status = status
        self.code = code


class V2Application:
    """Create application accounts without creating operating-system or SMB users."""

    def __init__(self, db_path: Path):
        self.db_path = db_path
        database.init_db(db_path)

    def register(self, payload: dict[str, Any]) -> dict[str, Any]:
        username = normalize_username(payload.get("username"))
        password = payload.get("password")
        group_type = payload.get("group_type")
        if username is None:
            raise ApiError(400, "INVALID_USERNAME", "请输入有效的中文姓名。")
        if (not isinstance(password, str) or len(password) < MIN_PASSWORD_LENGTH
                or len(password) > MAX_PASSWORD_CHARS):
            raise ApiError(400, "INVALID_PASSWORD", "密码长度需为 8 至 1024 位。")
        if "\n" in password or "\r" in password:
            raise ApiError(400, "INVALID_PASSWORD", "密码不能包含换行符。")
        if not valid_department(group_type):
            raise ApiError(400, "INVALID_DEPARTMENT", "请选择有效的所属部门。")
        try:
            user_id, token = database.create_user_and_session(
                self.db_path, username, hash_password(password), group_type)
        except sqlite3.IntegrityError:
            raise ApiError(409, "ACCOUNT_EXISTS", "该姓名已注册。") from None
        return self._session_payload(user_id, username, group_type, token)

    def login(self, payload: dict[str, Any]) -> dict[str, Any]:
        username = normalize_username(payload.get("username"))
        password = payload.get("password")
        if (username is None or not isinstance(password, str)
                or len(password) < MIN_PASSWORD_LENGTH or len(password) > MAX_PASSWORD_CHARS
                or "\n" in password or "\r" in password):
            raise ApiError(401, "INVALID_CREDENTIALS", "用户名或密码不正确。")
        user = database.get_user_by_username(self.db_path, username)
        if user is None or not verify_password(password, str(user["password_hash"])):
            raise ApiError(401, "INVALID_CREDENTIALS", "用户名或密码不正确。")
        token = database.create_session(self.db_path, int(user["id"]))
        return self._session_payload(int(user["id"]), str(user["username"]),
                                     str(user["group_type"]), token)

    def session(self, token: str) -> dict[str, Any]:
        user_id = database.get_session_user_id(self.db_path, token)
        if user_id is None:
            raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")
        user = database.get_user_by_id(self.db_path, user_id)
        if user is None:
            database.revoke_session(self.db_path, token)
            raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")
        return {"authenticated": True,
                "user": self._user_payload(int(user["id"]), str(user["username"]),
                                            str(user["group_type"]))}

    def logout(self, token: str) -> None:
        database.revoke_session(self.db_path, token)

    @staticmethod
    def _user_payload(user_id: int, username: str, group_type: str) -> dict[str, Any]:
        return {
            "id": user_id,
            "username": username,
            "department": DEPARTMENT_LABELS[group_type],
            "group_type": group_type,
            # Kept as logical scope IDs for older WPF DTOs. These are not SMB paths or
            # operating-system home directories; File Storage V2 authorizes each request.
            "spaces": {
                "film": "department:film",
                "writing": "department:writing",
                "public": "public:assets",
                "personal": f"personal:{user_id}",
            },
        }

    @classmethod
    def _session_payload(cls, user_id: int, username: str, group_type: str,
                         token: str) -> dict[str, Any]:
        return {"success": True, "session_token": token,
                "user": cls._user_payload(user_id, username, group_type)}
