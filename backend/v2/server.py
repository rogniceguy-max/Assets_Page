"""HTTPS entry point for the parallel V2 API."""

from __future__ import annotations

import hashlib
import json
import os
import re
import sqlite3
import ssl
import sys
import threading
import time
from collections import deque
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import urlsplit

from .config import Settings, load_settings
from .provisioner import AccountProvisioner
from .release_store import ReleaseNotFound, ReleaseStore, ReleaseUnavailable
from .service import ApiError, V2Application
from .feedback import FeedbackService
from .feedback_http import dispatch as dispatch_feedback
from .update_failure_reports import InvalidUpdateFailureReport, UpdateFailureReportStore, validate_report
from .visitor import VisitorReader, VisitorService
from .visitor_http import dispatch as dispatch_visitor


API_ROOT = "/api/v2"
MAX_BODY_BYTES = 64 * 1024
MAX_UPDATE_FAILURE_REPORT_BYTES = 2048
UPDATE_FAILURE_REPORT_PATH = f"{API_ROOT}/diagnostics/update-failures"
PACKAGE_PATH_RE = re.compile(rf"^{API_ROOT}/updates/packages/([a-f0-9]{{32}})$")
RELEASE_CHUNK_BYTES = 256 * 1024


def _bearer_token(headers) -> str | None:
    value = headers.get("Authorization", "")
    if not value.startswith("Bearer "):
        return None
    token = value[7:].strip()
    return token if token and len(token) <= 256 else None


def create_handler(app: V2Application, *,
                   update_releases_dir: Path | None = None,
                   update_failure_db_path: Path | None = None,
                   feedback: FeedbackService | None = None,
                   visitor: VisitorService | None = None):
    feedback = feedback or FeedbackService(app.session, app.db_path.parent / "feedback")
    # No visitor reader configured means the tour is unavailable, not open: the routes
    # still answer, but every read fails closed.
    visitor = visitor or VisitorService(VisitorReader(None))
    release_store = ReleaseStore(update_releases_dir)
    failure_store = None
    db_path = update_failure_db_path or getattr(app, "db_path", None)
    if db_path is not None:
        try:
            failure_store = UpdateFailureReportStore(Path(db_path))
        except (OSError, sqlite3.Error):
            failure_store = None
    report_rate_lock = threading.Lock()
    report_global_requests: deque[float] = deque()
    report_source_requests: dict[str, deque[float]] = {}

    def allow_failure_report(source: str) -> bool:
        now = time.monotonic()
        one_hour_ago = now - 3600
        with report_rate_lock:
            while report_global_requests and report_global_requests[0] < one_hour_ago:
                report_global_requests.popleft()
            if len(report_global_requests) >= 1000:
                return False
            for key, entries in list(report_source_requests.items()):
                while entries and entries[0] < one_hour_ago:
                    entries.popleft()
                if not entries:
                    report_source_requests.pop(key, None)
            source_requests = report_source_requests.get(source)
            if source_requests is None:
                if len(report_source_requests) >= 1024:
                    return False
                source_requests = deque()
                report_source_requests[source] = source_requests
            while source_requests and source_requests[0] < one_hour_ago:
                source_requests.popleft()
            if len(source_requests) >= 20:
                return False
            report_global_requests.append(now)
            source_requests.append(now)
            return True

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
            if dispatch_visitor(self, visitor, "GET"):
                return
            path = urlsplit(self.path).path
            if path == f"{API_ROOT}/health":
                self._send_json(200, {"status": "ok", "version": "2"})
                return
            if path == f"{API_ROOT}/updates/stable/manifest":
                self._serve_update_manifest()
                return
            package_match = PACKAGE_PATH_RE.fullmatch(path)
            if package_match:
                self._serve_update_package(package_match.group(1))
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

        def do_DELETE(self) -> None:  # noqa: N802
            # Only the visitor channel uses DELETE: ending a tour session.
            if not dispatch_visitor(self, visitor, "DELETE"):
                self._send_error(ApiError(404, "NOT_FOUND", "接口不存在。"))

        def do_POST(self) -> None:  # noqa: N802
            if dispatch_feedback(self, feedback, _bearer_token(self.headers), "POST"):
                return
            if dispatch_visitor(self, visitor, "POST"):
                return
            parsed_path = urlsplit(self.path)
            path = parsed_path.path
            if path == UPDATE_FAILURE_REPORT_PATH:
                if not allow_failure_report(self.client_address[0]):
                    self.close_connection = True
                    self._send_rate_limited(3600)
                    return
                if parsed_path.query or self.headers.get("Authorization"):
                    self.close_connection = True
                    self._send_error(ApiError(400, "UPDATE_REPORT_ANONYMOUS_ONLY", "诊断报告请求格式无效。"))
                    return
                payload = self._read_json(MAX_UPDATE_FAILURE_REPORT_BYTES, reject_duplicate_fields=True)
                if payload is None:
                    return
                try:
                    report = validate_report(payload)
                except InvalidUpdateFailureReport:
                    self._send_error(ApiError(400, "UPDATE_REPORT_INVALID", "诊断报告内容无效。"))
                    return
                if failure_store is None:
                    self._send_json(503, {"success": False, "code": "UPDATE_REPORT_STORAGE_UNAVAILABLE", "error": "诊断报告服务暂不可用。"})
                    return
                try:
                    failure_store.record(report)
                except (OSError, sqlite3.Error):
                    self._send_json(503, {"success": False, "code": "UPDATE_REPORT_STORAGE_UNAVAILABLE", "error": "诊断报告服务暂不可用。"})
                    return
                self._send_json(202, {"success": True, "received": True})
                return

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

        def _read_json(self, maximum_bytes: int = MAX_BODY_BYTES, reject_duplicate_fields: bool = False) -> dict[str, Any] | None:
            if self.headers.get("Transfer-Encoding"):
                self.close_connection = True
                self._send_error(ApiError(411, "CONTENT_LENGTH_REQUIRED", "请求格式无效。"))
                return None
            raw_length = self.headers.get("Content-Length")
            try:
                length = int(raw_length or "")
            except ValueError:
                length = -1
            if length < 0 or length > maximum_bytes:
                self.close_connection = True
                self._send_error(ApiError(413 if length > maximum_bytes else 411, "BODY_SIZE_INVALID", "请求长度无效。"))
                return None
            content_type = self.headers.get_content_type()
            if content_type != "application/json":
                self.close_connection = True
                self._send_error(ApiError(415, "JSON_REQUIRED", "请求格式必须为 JSON。"))
                return None
            try:
                body = self.rfile.read(length)
                if reject_duplicate_fields:
                    value = json.loads(body.decode("utf-8"), object_pairs_hook=self._unique_json_object)
                else:
                    value = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
                self._send_error(ApiError(400, "JSON_INVALID", "请求内容无效。"))
                return None
            if not isinstance(value, dict):
                self._send_error(ApiError(400, "JSON_OBJECT_REQUIRED", "请求内容无效。"))
                return None
            return value

        def _send_error(self, error: ApiError) -> None:
            self._send_json(error.status, {"success": False, "code": error.code, "error": str(error)})

        def _serve_update_manifest(self) -> None:
            try:
                body, opened, _manifest = release_store.open_manifest()
            except ReleaseUnavailable:
                self._send_json(503, {"success": False, "code": "UPDATE_RELEASE_UNAVAILABLE", "error": "更新服务暂不可用。"})
                return
            try:
                etag = self._release_etag(body)
                not_modified = self._etag_matches(self.headers.get("If-None-Match"), etag)
                self.send_response(304 if not_modified else 200)
                self._send_release_headers("application/json; charset=utf-8", None if not_modified else len(body), etag)
                self.end_headers()
                if not_modified is False:
                    self.wfile.write(body)
            finally:
                opened.close()

        def _serve_update_package(self, package_id: str) -> None:
            if not release_store.configured:
                self._send_json(503, {"success": False, "code": "UPDATE_RELEASE_UNAVAILABLE", "error": "更新服务暂不可用。"})
                return
            try:
                opened, package_sha256 = release_store.open_verified_package(package_id)
            except ReleaseNotFound:
                self._send_json(404, {"success": False, "code": "UPDATE_PACKAGE_NOT_FOUND", "error": "更新包不存在。"})
                return
            except ReleaseUnavailable:
                self._send_json(503, {"success": False, "code": "UPDATE_RELEASE_UNAVAILABLE", "error": "更新服务暂不可用。"})
                return
            try:
                etag = f'"{package_sha256}"'
                if self._etag_matches(self.headers.get("If-None-Match"), etag):
                    self.send_response(304)
                    self._send_release_headers("application/zip", None, etag)
                    self.send_header("Accept-Ranges", "bytes")
                    self.end_headers()
                    return

                start, end, partial = 0, opened.size - 1, False
                range_header = self.headers.get("Range")
                if range_header and (self.headers.get("If-Range") is not None and
                                     (self.headers.get("If-Range") != etag or etag.startswith("W/"))):
                    range_header = None
                if range_header:
                    parsed_range = self._parse_byte_range(range_header, opened.size)
                    if parsed_range is False:
                        self.send_response(416)
                        self.send_header("Content-Range", f"bytes */{opened.size}")
                        self._send_release_headers("application/zip", 0, etag)
                        self.send_header("Accept-Ranges", "bytes")
                        self.end_headers()
                        return
                    if parsed_range is not None:
                        start, end = parsed_range
                        partial = True

                length = max(0, end - start + 1)
                self.send_response(206 if partial else 200)
                self._send_release_headers("application/zip", length, etag)
                self.send_header("Accept-Ranges", "bytes")
                if partial:
                    self.send_header("Content-Range", f"bytes {start}-{end}/{opened.size}")
                self.end_headers()
                os.lseek(opened.file_descriptor, start, os.SEEK_SET)
                remaining = length
                while remaining:
                    chunk = os.read(opened.file_descriptor, min(RELEASE_CHUNK_BYTES, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
            finally:
                opened.close()

        @staticmethod
        def _release_etag(body: bytes) -> str:
            return f'"{hashlib.sha256(body).hexdigest()}"'

        @staticmethod
        def _etag_matches(header: str | None, etag: str) -> bool:
            if not header:
                return False
            for value in header.split(","):
                candidate = value.strip()
                if candidate == "*" or candidate == etag or candidate.removeprefix("W/") == etag:
                    return True
            return False

        @staticmethod
        def _parse_byte_range(value: str, size: int) -> tuple[int, int] | bool | None:
            match = re.fullmatch(r"bytes=(\d*)-(\d*)", value.strip())
            if not match:
                return None
            if size <= 0:
                return False
            first, last = match.groups()
            if not first and not last:
                return None
            if len(first) > 20:
                return False
            if not first:
                if len(last) > 20:
                    return 0, size - 1
                suffix_length = int(last)
                if suffix_length <= 0:
                    return False
                return max(0, size - suffix_length), size - 1
            start = int(first)
            if start >= size:
                return False
            end = size - 1 if not last or len(last) > 20 else int(last)
            if end < start:
                return False
            return start, min(end, size - 1)

        def _send_release_headers(self, content_type: str, length: int | None, etag: str) -> None:
            self.send_header("Content-Type", content_type)
            if length is not None:
                self.send_header("Content-Length", str(length))
            self.send_header("Cache-Control", "public, max-age=300, must-revalidate")
            self.send_header("ETag", etag)
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")

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

        @staticmethod
        def _unique_json_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
            result: dict[str, object] = {}
            for key, value in pairs:
                if key in result:
                    raise ValueError("duplicate JSON field")
                result[key] = value
            return result

        def _send_rate_limited(self, seconds: int) -> None:
            body = json.dumps({"success": False, "code": "RATE_LIMITED", "error": "请求过于频繁，请稍后重试。"},
                              ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            self.send_response(429)
            self.send_header("Retry-After", str(seconds))
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
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
    visitor = VisitorService(VisitorReader(settings.visitor_socket))
    # A tour session may read public feedback and post anonymously; maintenance still
    # requires an employee session with a maintainer id.
    feedback = FeedbackService(app.session, feedback_root, maintainers,
                               visitor_authenticate=visitor.has_session)
    server = V2HTTPServer((settings.bind_host, settings.port),
                          create_handler(app, update_releases_dir=settings.update_releases_dir,
                                         update_failure_db_path=settings.database_path,
                                         feedback=feedback, visitor=visitor))
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

