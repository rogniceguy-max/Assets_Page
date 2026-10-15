"""Small V2 adapter; does not change identity, SMB, TLS or transfer APIs."""
from urllib.parse import parse_qs, urlsplit
from .feedback import MAX_BODY_BYTES
from .service import ApiError

ROOT = "/api/v2/feedback"


def dispatch(handler, service, token, method):
    url = urlsplit(handler.path)
    if url.path != ROOT and not url.path.startswith(ROOT + "/"):
        return False
    try:
        # Authenticate before reading an upload or touching the feedback store.
        service.authorize(token)
        parts = url.path[len(ROOT):].strip("/").split("/") if url.path != ROOT else []
        if method == "GET":
            if not parts:
                handler._send_json(200, service.list(token, parse_qs(url.query, keep_blank_values=True, max_num_fields=8)))
            elif parts == ["capabilities"]:
                handler._send_json(200, service.capabilities(token))
            elif len(parts) == 2 and parts[0] == "attachments":
                body = service.attachment(token, parts[1])
                handler.send_response(200)
                handler.send_header("Content-Type", "image/png")
                handler.send_header("Content-Length", str(len(body)))
                handler.send_header("Cache-Control", "no-store")
                handler.send_header("X-Content-Type-Options", "nosniff")
                handler.send_header("Referrer-Policy", "no-referrer")
                handler.end_headers()
                handler.wfile.write(body)
            elif len(parts) == 1:
                handler._send_json(200, service.detail(token, parts[0]))
            else:
                raise ApiError(404, "NOT_FOUND", "接口不存在。")
        elif method == "POST" and not parts:
            payload = handler._read_json(MAX_BODY_BYTES)
            if payload is not None:
                handler._send_json(201, service.create(token, payload))
        elif method == "PATCH" and len(parts) == 1:
            service.authorize(token, manage=True)
            payload = handler._read_json()
            if payload is not None:
                handler._send_json(200, service.update(token, parts[0], payload))
        else:
            raise ApiError(405, "METHOD_NOT_ALLOWED", "不支持此操作。")
    except ApiError as error:
        if method != "GET":
            handler.close_connection = True
        handler._send_error(error)
    except ValueError:
        handler.close_connection = True
        handler._send_error(ApiError(400, "FEEDBACK_INVALID", "反馈请求无效。"))
    except Exception:
        # Never log request identifiers, addresses, bodies, tokens or tracebacks.
        handler.close_connection = True
        handler._send_error(ApiError(503, "FEEDBACK_UNAVAILABLE", "反馈服务暂时不可用，请稍后重试。"))
    return True
