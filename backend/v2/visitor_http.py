"""HTTP adapter for the login-free visitor channel.

Read-only by construction: the only mutating route is session creation/deletion. There
is no upload, rename, delete or metadata route, and the privileged helper implements
no write path either.
"""
from __future__ import annotations

import mimetypes
from urllib.parse import parse_qs, quote, urlsplit

from .service import ApiError
from .visitor import MAX_READ_BYTES, VisitorUnavailable

ROOT = "/api/v2/visitor"
CHUNK_BYTES = 1024 * 1024


def _bearer(handler) -> str | None:
    value = handler.headers.get("Authorization", "")
    if not value.startswith("Bearer "):
        return None
    token = value[7:].strip()
    return token or None


def _parse_range(header: str | None, size: int) -> tuple[int, int] | None:
    """Single-range only; returns an inclusive (start, end) or None for 'whole file'."""
    if not header or not header.startswith("bytes=") or "," in header:
        return None
    spec = header[6:].strip()
    if "-" not in spec:
        return None
    first, last = spec.split("-", 1)
    if first == "":
        if not last.isdigit():
            return None
        count = int(last)
        if count == 0:
            return None
        return max(0, size - count), size - 1
    if not first.isdigit():
        return None
    start = int(first)
    if last == "":
        return (start, size - 1) if start < size else None
    if not last.isdigit():
        return None
    end = int(last)
    if start >= size or end < start:
        return None
    return start, min(end, size - 1)


def dispatch(handler, service, method: str) -> bool:
    url = urlsplit(handler.path)
    if url.path != ROOT and not url.path.startswith(ROOT + "/"):
        return False

    parts = url.path[len(ROOT):].strip("/").split("/") if url.path != ROOT else []
    query = parse_qs(url.query, keep_blank_values=True, max_num_fields=8)
    token = _bearer(handler)

    try:
        if parts == ["session"]:
            if method == "POST":
                # No credentials required: this is the login-free entry.
                handler._send_json(201, service.start_session())
            elif method == "DELETE":
                service.end_session(token)
                handler._send_json(200, {"success": True})
            else:
                raise ApiError(405, "METHOD_NOT_ALLOWED", "不支持此操作。")
            return True

        if len(parts) == 3 and parts[0] == "spaces" and method == "GET":
            space, action = parts[1], parts[2]
            relative_path = (query.get("path") or [""])[0]
            if action == "entries":
                handler._send_json(200, service.list(token, space, relative_path))
                return True
            if action == "file":
                _stream_file(handler, service, token, space, relative_path)
                return True

        raise ApiError(404, "NOT_FOUND", "接口不存在。")
    except ApiError as error:
        if method != "GET":
            handler.close_connection = True
        handler._send_error(error)
    except VisitorUnavailable:
        handler.close_connection = True
        handler._send_error(ApiError(503, "VISITOR_UNAVAILABLE", "参观通道暂时不可用，请稍后重试。"))
    except ValueError:
        handler.close_connection = True
        handler._send_error(ApiError(400, "VISITOR_REQUEST_INVALID", "参观请求无效。"))
    except Exception:
        # Never log paths, identifiers, tokens or tracebacks.
        handler.close_connection = True
        handler._send_error(ApiError(503, "VISITOR_UNAVAILABLE", "参观通道暂时不可用，请稍后重试。"))
    return True


def _stream_file(handler, service, token, space: str, relative_path: str) -> None:
    # The first chunk also reports the total size, which Range and Content-Range need.
    first_offset = 0
    probe, size = service.read(token, space, relative_path, first_offset, min(CHUNK_BYTES, MAX_READ_BYTES))
    span = _parse_range(handler.headers.get("Range"), size)
    if handler.headers.get("Range") and span is None and size > 0:
        handler.send_response(416)
        handler.send_header("Content-Range", "bytes */%d" % size)
        handler.send_header("Content-Length", "0")
        handler.end_headers()
        return

    start, end = span if span else (0, max(0, size - 1))
    remaining = end - start + 1
    total = size

    name = relative_path.rsplit("/", 1)[-1]
    content_type = mimetypes.guess_type(name)[0] or "application/octet-stream"
    handler.send_response(206 if span else 200)
    handler.send_header("Content-Type", content_type)
    handler.send_header("Content-Length", str(remaining))
    handler.send_header("Accept-Ranges", "bytes")
    if span:
        handler.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, total))
    handler.send_header("Cache-Control", "no-store")
    handler.send_header("X-Content-Type-Options", "nosniff")
    handler.send_header("Referrer-Policy", "no-referrer")
    handler.send_header("Content-Disposition",
                        "inline; filename*=UTF-8''" + quote(name, safe=""))
    handler.end_headers()

    offset = start
    while remaining > 0:
        length = min(CHUNK_BYTES, remaining, MAX_READ_BYTES)
        if offset == first_offset and length <= len(probe):
            chunk = probe[:length]
        else:
            chunk, _ = service.read(token, space, relative_path, offset, length)
        if not chunk:
            break
        handler.wfile.write(chunk)
        offset += len(chunk)
        remaining -= len(chunk)
