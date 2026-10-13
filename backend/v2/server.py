"""HTTPS entry point for the parallel V2 API."""

from __future__ import annotations

import json
import os
import ssl
import sys
import threading
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import urlsplit

from .config import Settings, load_settings
from .provisioner import AccountProvisioner
from .service import ApiError, V2Application
from .feedback import FeedbackService
from .feedback_http import dispatch as dispatch_feedback


API_ROOT = "/api/v2"
MAX_BODY_BYTES = 64 * 1024


def _bearer_token(headers) -> str | None:
    value = headers.get("Authorization", "")
    if not value.startswith("Bearer "):
        return None
    token = value[7:].strip()
    return token if token and len(token) <= 256 else None


def create_handler(app: V2Application, feedback: FeedbackService | None = None):
    feedback = feedback or FeedbackService(app.session, app.db_path.parent / "feedback")
    class V2RequestHandler(BaseHTTPRequestHandler):
        server_version = "MuYingHubV2"
        sys_version = ""
        protocol_version = "HTTP/1.1"

        def log_message(self, _format: str, *_args: object) -> None:
            # Request bodies, usernames, tokens and client-provided path strings
            # are deliberately omitted from logs.
            return

        def do_GET(self) -> None:  # noqa: N802
            if dispatch_feedback(self, feedback, _bearer_token(self.headers), "GET"):
                return
            path = urlsplit(self.path).path
            if path == f"{API_ROOT}/health":
                self._send_json(200, {"status": "ok", "version": "2"})
                return
            if path == f"{API_ROOT}/session":
                token = _bearer_token(self.headers)
                if token is None:
                    self._send_error(ApiError(401, "SESSION_REQUIRED", "请先登录。"))
                    return
                try:
                    self._send_json(200, app.session(token))
                except ApiError as error:
                    self._send_error(error)
                return
            self._send_error(ApiError(404, "NOT_FOUND", "接口不存在。"))

        def do_POST(self) -> None:  # noqa: N802
            if dispatch_feedback(self, feedback, _bearer_token(self.headers), "POST"):
                return
            path = urlsplit(self.path).path
            payload = self._read_json()
            if payload is None:
                return
            if path == f"{API_ROOT}/register":
                try:
                    self._send_json(201, app.register(payload))
                except ApiError as error:
                    self._send_error(error)
                return
            if path == f"{API_ROOT}/login":
                try:
                    self._send_json(200, app.login(payload))
                except ApiError as error:
                    self._send_error(error)
                return
            if path == f"{API_ROOT}/logout":
                token = _bearer_token(self.headers)
                if token is None:
                    self._send_error(ApiError(401, "SESSION_REQUIRED", "请先登录。"))
                    return
                app.logout(token)
                self._send_json(200, {"success": True})
                return
            self._send_error(ApiError(404, "NOT_FOUND", "接口不存在。"))

        def do_PATCH(self) -> None:  # noqa: N802
            if not dispatch_feedback(self, feedback, _bearer_token(self.headers), "PATCH"):
                self._send_error(ApiError(404, "NOT_FOUND", "接口不存在。"))

        def _read_json(self, maximum: int = MAX_BODY_BYTES) -> dict[str, Any] | None:
            if self.headers.get("Transfer-Encoding"):
                self.close_connection = True
                self._send_error(ApiError(411, "CONTENT_LENGTH_REQUIRED", "请求格式无效。"))
                return None
            raw_length = self.headers.get("Content-Length")
            try:
                length = int(raw_length or "")
            except ValueError:
                length = -1
            if length < 0 or length > maximum:
                self.close_connection = True
                self._send_error(ApiError(413 if length > maximum else 411, "BODY_SIZE_INVALID", "请求长度无效。"))
                return None
            content_type = self.headers.get_content_type()
            if content_type != "application/json":
                self.close_connection = True
                self._send_error(ApiError(415, "JSON_REQUIRED", "请求格式必须为 JSON。"))
                return None
            try:
                body = self.rfile.read(length)
                value = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                self._send_error(ApiError(400, "JSON_INVALID", "请求内容无效。"))
                return None
            if not isinstance(value, dict):
                self._send_error(ApiError(400, "JSON_OBJECT_REQUIRED", "请求内容无效。"))
                return None
            return value

        def _send_error(self, error: ApiError) -> None:
            self._send_json(error.status, {"success": False, "code": error.code, "error": str(error)})

        def _send_json(self, status: int, data: dict[str, Any]) -> None:
            body = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.end_headers()
            self.wfile.write(body)

    return V2RequestHandler


def make_server(settings: Settings, app: V2Application) -> ThreadingHTTPServer:
    class V2HTTPServer(ThreadingHTTPServer):
        def __init__(self, server_address, request_handler):
            self._request_slots = threading.BoundedSemaphore(32)
            super().__init__(server_address, request_handler)

        def process_request(self, request, client_address):
            if not self._request_slots.acquire(blocking=False):
                request.close()
                return
            try:
                super().process_request(request, client_address)
            except Exception:
                self._request_slots.release()
                raise

        def process_request_thread(self, request, client_address):
            try:
                super().process_request_thread(request, client_address)
            finally:
                self._request_slots.release()

        def get_request(self):
            request, address = super().get_request()
            # Do not let a slow or abandoned request hold a worker thread forever.
            request.settimeout(20)
            return request, address

        def handle_error(self, request, client_address):
            # Default socket error tracebacks include IPs; feedback must not emit them.
            # Monitor aggregate health separately, without per-request identifiers.
            return

    # Explicit existing account IDs grant feedback maintenance only, default denied.
    raw_maintainers = os.environ.get("MUYING_V2_FEEDBACK_MAINTAINERS", "")
    maintainers = {int(value.strip()) for value in raw_maintainers.split(",") if value.strip().isdigit() and int(value.strip()) > 0}
    feedback_root = Path(os.environ.get("MUYING_V2_FEEDBACK_DIR", str(settings.data_dir / "feedback")))
    feedback = FeedbackService(app.session, feedback_root, maintainers)
    server = V2HTTPServer((settings.bind_host, settings.port), create_handler(app, feedback))
    server.daemon_threads = True
    server.request_queue_size = 64

    is_loopback = settings.bind_host in {"127.0.0.1", "::1", "localhost"}
    if settings.tls_certificate is None or settings.tls_private_key is None:
        if not is_loopback or os.environ.get("MUYING_V2_ALLOW_LOOPBACK_HTTP") != "1":
            server.server_close()
            raise RuntimeError("V2 API requires TLS unless loopback development HTTP is explicitly enabled.")
        return server

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(
        certfile=str(settings.tls_certificate),
        keyfile=str(settings.tls_private_key),
    )
    server.socket = context.wrap_socket(server.socket, server_side=True)
    return server


def build_app(settings: Settings) -> V2Application:
    return V2Application(
        settings.database_path,
        AccountProvisioner(settings.provisioner_socket),
    )


def main() -> int:
    settings = load_settings()
    app = build_app(settings)
    server = make_server(settings, app)
    print(f"MuYing Hub V2 listening on {settings.bind_host}:{settings.port}", file=sys.stderr)
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

