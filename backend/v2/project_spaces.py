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
                 provisioner: ProjectProvisioner | None = None):
        self._root = root
        self._session = session
        self._provisioner = provisioner or ProjectProvisioner(None)

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
        return self._session(token)

    def list_projects(self, actor: Any) -> list[dict[str, str]]:
        """Every directory in the project root, identified by inode.

        The same scan the workbook route uses, imported rather than re-implemented: the two
        must agree on what a project is, and a second copy is how they stop agreeing. The
        inode is the id because it survives a rename, which is the thing users are invited
        to do to a project.
        """
        if self._root is None:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。")
        try:
            directories = _project_directories(self._root)
        except Exception as error:
            raise ApiError(503, "STORAGE_UNAVAILABLE", "项目目录暂不可用。") from error
        return [{"id": str(entry.stat().st_ino), "name": entry.name} for entry in directories]

    def create_project(self, actor: Any, name: object) -> dict[str, Any]:
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

        # Read the id back from the directory rather than inventing one: the client joins
        # on this value, so it has to be the same number a later listing will report.
        after = self.list_projects(actor)
        created = next((item for item in after if item["name"] == result.get("name")), None)
        if created is None:
            raise ApiError(500, "PROJECT_NOT_LISTED", "项目已创建，但未能读回，请刷新后确认。")
        return created
