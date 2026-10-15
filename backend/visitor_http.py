"""HTTP adapter for ephemeral visitor sessions."""

from __future__ import annotations

from urllib.parse import urlsplit

from .service import ApiError

ROOT = "/api/v2/visitor"
SESSION_PATH = ROOT + "/session"


def dispatch(handler, service, method: str) -> bool:
    path = urlsplit(handler.path).path.rstrip("/") or "/"
    if path != ROOT and not path.startswith(ROOT + "/"):
        return False

    try:
        if path != SESSION_PATH:
            raise ApiError(404, "NOT_FOUND", "接口不存在。")
        token = handler.headers.get("Authorization", "")
        token = token[7:].strip() if token.startswith("Bearer ") else None
        if method == "POST":
            handler._send_json(201, service.start_session())
        elif method == "DELETE":
            service.end_session(token)
            handler._send_json(200, {"success": True})
        else:
            raise ApiError(405, "METHOD_NOT_ALLOWED", "不支持此操作。")
    except ApiError as error:
        if method != "GET":
            handler.close_connection = True
        handler._send_error(error)
    except Exception:
        handler.close_connection = True
        handler._send_error(ApiError(503, "VISITOR_UNAVAILABLE", "参观服务暂时不可用，请稍后重试。"))
    return True
