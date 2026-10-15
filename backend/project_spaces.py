"""Authenticated project-space routes backed by logical SQLite project records.

Project creation, deletion, and restoration only update SQLite metadata. Physical folders
and NAS paths are managed by the file storage service and are never scanned here.
"""

from __future__ import annotations

from typing import Any

from .service import ApiError

class ProjectSpaceService:
    """Project listing, creation, and deletion used by the WPF project browser."""

    def __init__(self, session: Any, catalog: Any = None,
                 visitor_authenticate: Any = None):
        self._session = session
        self._catalog = catalog
        self._visitor_authenticate = visitor_authenticate

    def authorize(self, token: str | None) -> Any:
        """The signed-in actor, or an ApiError. The host's own session lookup decides.

        Every method below takes the actor it returned rather than a token, so a route
        cannot accidentally do work for a caller nobody checked. The actor is otherwise
        unused: the contract says projects are readable and writable by everyone signed in,
        so the only question is whether this caller is signed in at all.
        """
        if token is None:
            raise ApiError(401, "SESSION_INVALID", "请先登录。")
        try:
            return self._session(token)
        except ApiError:
            if self._visitor_authenticate and self._visitor_authenticate(token):
                return {
                    "authenticated": True,
                    "user": {
                        "id": 0,
                        "username": "访客",
                        "group_type": "visitor",
                        "role": "visitor",
                        "can_write": False,
                    },
                    "role": "visitor",
                    "can_write": False,
                }
            raise

    def list_projects(self, actor: Any, include_deleted: bool = False) -> list[dict[str, str]]:
        """Logical project records from SQLite; no filesystem scan is authoritative."""
        if self._catalog is None:
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目 SQLite 数据库尚未配置。")
        try:
            return self._catalog.projects(actor, include_deleted=include_deleted)
        except Exception as error:
            if isinstance(error, ApiError):
                raise
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。") from error

    def create_project(self, actor: Any, name: object) -> dict[str, Any]:
        if actor and (actor.get("role") == "visitor" or not actor.get("can_write", True)):
            raise ApiError(403, "FORBIDDEN", "访客账户只有只读权限。")
        if self._catalog is None:
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目 SQLite 数据库尚未配置。")
        try:
            return self._catalog.create_project_record(name, actor)
        except Exception as error:
            if isinstance(error, ApiError):
                raise
            if hasattr(error, "status") and hasattr(error, "code"):
                raise ApiError(error.status, error.code, getattr(error, "message", str(error))) from error
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE",
                "项目记录暂时无法保存，请稍后重试。") from error

    def delete_project(self, actor: Any, project_id: str) -> dict[str, Any]:
        if actor and (actor.get("role") == "visitor" or not actor.get("can_write", True)):
            raise ApiError(403, "FORBIDDEN", "访客账户只有只读权限。")
        if self._catalog is None:
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目 SQLite 数据库尚未配置。")
        try:
            return self._catalog.delete_project_record(project_id, actor)
        except Exception as error:
            if hasattr(error, "status") and hasattr(error, "code"):
                raise ApiError(error.status, error.code, getattr(error, "message", str(error))) from error
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目记录暂时无法更新。") from error

    def restore_project(self, actor: Any, project_id: str) -> dict[str, Any]:
        if actor and (actor.get("role") == "visitor" or not actor.get("can_write", True)):
            raise ApiError(403, "FORBIDDEN", "访客账户只有只读权限。")
        if self._catalog is None:
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目 SQLite 数据库尚未配置。")
        try:
            return self._catalog.restore_project_record(project_id, actor)
        except Exception as error:
            if hasattr(error, "status") and hasattr(error, "code"):
                raise ApiError(error.status, error.code, getattr(error, "message", str(error))) from error
            raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE", "项目记录暂时无法恢复。") from error
