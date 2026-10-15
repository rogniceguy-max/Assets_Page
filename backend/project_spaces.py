"""Project spaces: listing them, and asking the root helper to create one.

The listing reads the project root directly — the service account can read it, and a
directory is a project. Creation cannot be done here: this process runs with
``ProtectSystem=strict`` and may only write ``/var/lib/muying-v2``, so creating a project
goes through the socket-activated root helper (``project_helper.py``), exactly as account
provisioning does.

Kept apart from the vendored ``project_workbooks`` package on purpose. That package is a
byte-identical copy of a handoff and is meant to stay that way; these two routes are the
host's own, under the paths the contract specifies (PROJECT_SPACE_V1.md).
"""

from __future__ import annotations

import json
import socket
from pathlib import Path
from typing import Any

from .project_workbooks.nas_adapter import _project_directories
from .service import ApiError

MAX_HELPER_MESSAGE_BYTES = 16 * 1024


class ProjectHelperUnavailable(RuntimeError):
    """The helper could not be reached, so nothing was attempted."""


class ProjectHelperFailed(RuntimeError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


class ProjectProvisioner:
    """Asks the root helper to create a project. Never invokes a shell command itself."""

    def __init__(self, socket_path: Path | None, timeout_seconds: float = 60):
        self.socket_path = socket_path
        self.timeout_seconds = timeout_seconds

    def create_project(self, name: str) -> dict[str, Any]:
        return self._request({"operation": "create_project", "name": name})

    def delete_project(self, name: str) -> dict[str, Any]:
        return self._request({"operation": "delete_project", "name": name})

    def move_asset_folder(self, project_name: str, source_path: str, target_path: str) -> dict[str, Any]:
        return self._request({"operation": "move_asset_folder", "project_name": project_name,
                              "source_path": source_path, "target_path": target_path})

    def archive_legacy_workbook(self, project_name: str, file_name: str, sha256: str) -> dict[str, Any]:
        return self._request({"operation": "archive_legacy_workbook", "project_name": project_name,
                              "file_name": file_name, "sha256": sha256})

    def add_sheet(self, project_name: str, file_name: str, sheet_name: str) -> dict[str, Any]:
        return self._request({
            "operation": "add_sheet",
            "project_name": project_name,
            "file_name": file_name,
            "sheet_name": sheet_name,
        })

    def ensure_workbooks(self, project_name: str) -> dict[str, Any]:
        return self._request({
            "operation": "ensure_workbooks",
            "project_name": project_name,
        })

    def ensure_asset_folder(self, project_name: str, category: str, name: str) -> dict[str, Any]:
        return self._request({
            "operation": "ensure_asset_folder",
            "project_name": project_name,
            "category": category,
            "name": name,
        })

    def _request(self, payload: dict[str, Any]) -> dict[str, Any]:
        if self.socket_path is None:
            raise ProjectHelperUnavailable("项目创建服务尚未配置。")
        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        if len(encoded) > MAX_HELPER_MESSAGE_BYTES:
            raise ProjectHelperFailed("REQUEST_TOO_LARGE", "项目请求过长。")

        channel = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        channel.settimeout(self.timeout_seconds)
        try:
            try:
                channel.connect(str(self.socket_path))
            except OSError as error:
                raise ProjectHelperUnavailable("项目创建服务不可用，请联系管理员。") from error
            channel.sendall(encoded)
            response = bytearray()
            while len(response) <= MAX_HELPER_MESSAGE_BYTES:
                chunk = channel.recv(4096)
                if not chunk:
                    break
                newline = chunk.find(b"\n")
                if newline >= 0:
                    response.extend(chunk[:newline])
                    break
                response.extend(chunk)
        finally:
            channel.close()

        if len(response) > MAX_HELPER_MESSAGE_BYTES:
            raise ProjectHelperFailed("RESPONSE_TOO_LARGE", "项目创建服务返回了过长的响应。")
        try:
            result = json.loads(bytes(response).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ProjectHelperFailed("INVALID_RESPONSE", "项目创建服务返回了无效的响应。") from error
        if not isinstance(result, dict):
            raise ProjectHelperFailed("INVALID_RESPONSE", "项目创建服务返回了无效的响应。")
        if not result.get("ok"):
            raise ProjectHelperFailed(
                str(result.get("code") or "INTERNAL_ERROR"),
                str(result.get("message") or "创建项目失败。"))
        return result


class ProjectSpaceService:
    """The two project-space routes the client has today."""

    def __init__(self, root: Path | None, session: Any,
                 provisioner: ProjectProvisioner | None = None, catalog: Any = None,
                 visitor_authenticate: Any = None):
        self._root = root
        self._session = session
        self._provisioner = provisioner or ProjectProvisioner(None)
        self._catalog = catalog
        self._visitor_authenticate = visitor_authenticate

    @property
    def configured(self) -> bool:
        return self._root is not None

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
                        "system_username": "my_visitor",
                        "group_type": "visitor",
                        "role": "visitor",
                        "can_write": False,
                    },
                    "role": "visitor",
                    "can_write": False,
                }
            raise

    def list_projects(self, actor: Any) -> list[dict[str, str]]:
        """Every project directory, assigned a stable SQLite ID independent of its name."""
        if self._root is None:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。")
        if self._catalog is not None:
            try:
                return self._catalog.projects(actor)
            except Exception as error:
                if isinstance(error, ApiError):
                    raise
                raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。") from error
        try:
            directories = _project_directories(self._root)
        except Exception as error:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。") from error
        return [{"id": str(entry.stat().st_ino), "name": entry.name} for entry in directories]

    def create_project(self, actor: Any, name: object) -> dict[str, Any]:
        if actor and (actor.get("role") == "visitor" or not actor.get("can_write", True)):
            raise ApiError(403, "FORBIDDEN", "访客账户只有只读权限。")
        if self._root is None:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。")
        try:
            result = self._provisioner.create_project(name if isinstance(name, str) else "")
        except ProjectHelperUnavailable as error:
            raise ApiError(503, "PROJECT_SERVICE_UNAVAILABLE", str(error)) from error
        except ProjectHelperFailed as error:
            # The helper's codes are specific — a taken name, an invalid name — and they are
            # the ones the user needs to see. Anything else is its own internal failure and
            # is reported as such rather than widening what this service claims to know.
            status = 409 if error.code == "NAME_TAKEN" else 422 if error.code == "INVALID_NAME" else 500
            raise ApiError(status, error.code, str(error)) from error

        if self._catalog is not None:
            try:
                return self._catalog.register_created_project(str(result.get("name") or name))
            except Exception as error:
                raise ApiError(503, "PROJECT_DATABASE_UNAVAILABLE",
                    "项目文件夹已创建，但表格数据库暂不可用；刷新项目列表后可重试初始化。") from error

        # Legacy fallback for hosts without the SQLite workbook catalog.
        after = self.list_projects(actor)
        created = next((item for item in after if item["name"] == result.get("name")), None)
        if created is None:
            raise ApiError(500, "PROJECT_NOT_LISTED", "项目已创建，但未能读回，请刷新后确认。")
        return created

    def delete_project(self, actor: Any, project_id: str) -> dict[str, Any]:
        if actor and (actor.get("role") == "visitor" or not actor.get("can_write", True)):
            raise ApiError(403, "FORBIDDEN", "访客账户只有只读权限。")
        if self._root is None:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。")

        proj_name = None
        if self._catalog is not None:
            try:
                row, directory = self._catalog._project(project_id)
                proj_name = directory.name
            except Exception:
                pass

        if not proj_name:
            for directory in _project_directories(self._root):
                if directory.name == project_id or str(directory.stat().st_ino) == project_id:
                    proj_name = directory.name
                    break

        if not proj_name:
            raise ApiError(404, "NOT_FOUND", "项目不存在。")

        try:
            self._provisioner.delete_project(proj_name)
        except ProjectHelperUnavailable as error:
            raise ApiError(503, "PROJECT_SERVICE_UNAVAILABLE", str(error)) from error
        except ProjectHelperFailed as error:
            raise ApiError(500, error.code, str(error)) from error

        if self._catalog is not None:
            try:
                self._catalog.delete_project_record(project_id)
            except Exception:
                pass

        return {"ok": True, "name": proj_name, "deleted": True}
