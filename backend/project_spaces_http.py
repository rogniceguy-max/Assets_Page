"""The project-space routes: list the projects, and create one.

A host module rather than part of the vendored ``project_workbooks`` package, and under the
paths the contract names (PROJECT_SPACE_V1.md). The vendored package claims the whole
``/api/v2/project-workbooks`` prefix, so this could not live there even if it wanted to.

Authenticated like every other feature route: the contract puts project spaces behind a
signed-in user, and there is no anonymous path to them.
"""
from __future__ import annotations

from urllib.parse import urlsplit

from .service import ApiError

ROOT = "/api/v2/project-spaces"
# A project name is a directory name. The helper enforces the real rules; this only stops an
# oversized body from being read at all.
MAX_CREATE_BODY_BYTES = 4 * 1024


def dispatch(handler, service, token, method: str) -> bool:
    url = urlsplit(handler.path)
    if url.path != ROOT and not url.path.startswith(ROOT + "/"):
        return False

    try:
        # The actor is resolved before anything is read or touched, so an unauthenticated
        # caller never reaches the file system or the helper.
        actor = service.authorize(token)

        if method == "GET" and url.path == ROOT:
            handler._send_json(200, {"items": service.list_projects(actor)})
        elif method == "POST" and url.path == ROOT:
            payload = handler._read_json(MAX_CREATE_BODY_BYTES)
            if payload is not None:
                if not isinstance(payload, dict):
                    raise ApiError(422, "INVALID_NAME", "请求内容无效。")
                handler._send_json(201, service.create_project(actor, payload.get("name")))
        else:
            raise ApiError(404, "NOT_FOUND", "接口不存在。")
    except ApiError as error:
        if method != "GET":
            handler.close_connection = True
        handler._send_error(error)
    except Exception:
        handler.close_connection = True
        handler._send_error(ApiError(503, "PROJECT_SPACE_UNAVAILABLE", "项目服务暂时不可用，请稍后重试。"))
    return True
