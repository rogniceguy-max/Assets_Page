"""HTTPS entry point for the parallel V2 API."""

from __future__ import annotations

import hashlib
import html
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
from .installer_store import InstallerStore
from .provisioner import AccountProvisioner
from .release_store import ReleaseNotFound, ReleaseStore, ReleaseUnavailable
from .service import ApiError, V2Application
from .feedback import FeedbackService
from .feedback_http import dispatch as dispatch_feedback
from .update_failure_reports import InvalidUpdateFailureReport, UpdateFailureReportStore, validate_report
from .visitor import VisitorReader, VisitorService
from .visitor_http import dispatch as dispatch_visitor

# The workbook routes need a spreadsheet library that nothing else in this service uses. If
# it is absent they go unavailable rather than the service refusing to start: login, the
# update channel and the download page have no dependency on any of this, and an API that
# will not boot because one optional feature's dependency is missing is a far worse failure
# than that one feature being offline. The routes then fall through to the host's 404.
try:
    from .project_workbooks_http import dispatch as dispatch_project_workbooks
    from .project_workbooks.nas_adapter import NasSecurity, NasWorkbookServices, NasWorkflowServices
    from .project_workbooks.projects_api import ProjectsApi
    # The project-space routes are the host's own, but they reuse the vendored package's
    # directory scan so the two cannot disagree about what a project is — which means they
    # share its spreadsheet dependency and go unavailable together with it.
    from .project_spaces import ProjectProvisioner, ProjectSpaceService
    from .project_spaces_http import dispatch as dispatch_project_spaces
except ImportError:
    dispatch_project_workbooks = None
    dispatch_project_spaces = None
    ProjectsApi = ProjectSpaceService = ProjectProvisioner = None
    NasSecurity = NasWorkbookServices = NasWorkflowServices = None


API_ROOT = "/api/v2"
MAX_BODY_BYTES = 64 * 1024
MAX_UPDATE_FAILURE_REPORT_BYTES = 2048
UPDATE_FAILURE_REPORT_PATH = f"{API_ROOT}/diagnostics/update-failures"
PACKAGE_PATH_RE = re.compile(rf"^{API_ROOT}/updates/packages/([a-f0-9]{{32}})$")
RELEASE_CHUNK_BYTES = 256 * 1024
# How long a connection may take to complete its TLS handshake before it is dropped.
# Without a bound, one client that opens a TCP connection and sends no ClientHello
# holds the accept loop; see process_request_thread.
HANDSHAKE_TIMEOUT_SECONDS = 20
# How long an accepted connection may sit without making progress.
REQUEST_TIMEOUT_SECONDS = 20
# Human-facing, so deliberately outside API_ROOT: this is a URL to paste to a
# colleague, not part of the client protocol.
DOWNLOAD_ROOT = "/download"
INSTALLER_FILE_PATH = f"{DOWNLOAD_ROOT}/muying-setup.exe"
INSTALLER_DIGEST_PATH = f"{DOWNLOAD_ROOT}/muying-setup.sha256"

# The page at the bare URL. Served as HTML with a link rather than as an index of paths:
# this address is meant to be pasted to a colleague who has nothing installed, and a page
# that prints a path as text gives them nothing to click. Placeholders are substituted with
# str.replace rather than a template engine — the values come from the installer store, not
# from the request, and escaping them is enough.
DOWNLOAD_PAGE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>下载沐影盘</title>
<style>
  :root { color-scheme: light dark; }
  body { font: 15px/1.7 -apple-system, "Microsoft YaHei UI", system-ui, sans-serif;
         max-width: 620px; margin: 0 auto; padding: 48px 22px 80px; }
  h1 { font-size: 22px; margin: 0 0 6px; }
  .sub { opacity: .65; margin: 0 0 28px; }
  a.go { display: block; text-align: center; text-decoration: none; font-size: 17px;
         padding: 16px; border-radius: 10px; background: #5B5BD6; color: #fff;
         font-weight: 600; }
  a.go:hover { background: #5151CD; }
  dl { margin: 28px 0 0; display: grid; grid-template-columns: auto 1fr; gap: 6px 16px; }
  dt { opacity: .65; white-space: nowrap; }
  dd { margin: 0; font-family: Consolas, Menlo, monospace; font-size: 13px; word-break: break-all; }
  .box { margin-top: 28px; padding: 14px 16px; border-radius: 10px;
         border: 1px solid rgba(127,127,127,.3); background: rgba(127,127,127,.08); }
  .box p { margin: 0 0 8px; }
  .box p:last-child { margin: 0; }
</style>
</head>
<body>
<h1>沐影盘</h1>
<p class="sub">点击下面的按钮下载。装好之后桌面会出现「沐影盘」图标。</p>

<a class="go" href="{{PATH}}" download>下载安装包（{{SIZE}} MB）</a>

<dl>
  <dt>版本文件</dt><dd>{{NAME}}</dd>
  <dt>大小</dt><dd>{{SIZE}} MB</dd>
  <dt>SHA-256</dt><dd>{{DIGEST}}</dd>
</dl>

<div class="box">
  <p><b>安装不需要管理员权限</b>，也不会弹 UAC。</p>
  <p>第一次运行如果出现「Windows 已保护你的电脑」，点<b>「更多信息」</b>，再点<b>「仍要运行」</b>。</p>
  <p>想核对文件有没有传坏，可以对照上面的 SHA-256，校验值文件在
     <a href="{{DIGEST_PATH}}">{{DIGEST_PATH}}</a>。</p>
</div>
</body>
</html>
"""

DOWNLOAD_PAGE_NOTHING = """<!DOCTYPE html>
<html lang="zh-CN">
<head><meta charset="utf-8"><title>下载沐影盘</title></head>
<body style="font:15px/1.7 -apple-system,'Microsoft YaHei UI',system-ui,sans-serif;padding:48px 22px">
<h1 style="font-size:22px">沐影盘</h1>
<p>尚未发布安装包。</p>
</body>
</html>
"""

# Downloads run in the same worker pool as the authenticated API, and every connection
# holds one of its 32 slots for as long as the transfer lasts. Without a separate cap, a
# roomful of colleagues fetching the installer at once — the entire point of the route —
# would take login and update checks down with it. Capping downloads well below the pool
# leaves the API most of its capacity whatever the downloads are doing.
MAX_CONCURRENT_DOWNLOADS = 8
# A client that drains a chunk just inside the socket timeout holds its slot forever
# otherwise: 20 s of silence never happens if it never goes quiet. This bounds the hold
# however slowly it reads. At a gigabit the whole file takes about two seconds; this is
# the floor below which a transfer is not really progressing.
MAX_DOWNLOAD_SECONDS = 300


def _bearer_token(headers) -> str | None:
    value = headers.get("Authorization", "")
    if not value.startswith("Bearer "):
        return None
    token = value[7:].strip()
    return token if token and len(token) <= 256 else None


def create_handler(app: V2Application, *,
                   update_releases_dir: Path | None = None,
                   installer_dir: Path | None = None,
                   installer: InstallerStore | None = None,
                   download_slots: threading.BoundedSemaphore | None = None,
                   update_failure_db_path: Path | None = None,
                   feedback: FeedbackService | None = None,
                   visitor: VisitorService | None = None,
                   project_workbooks: ProjectsApi | None = None,
                   project_spaces: ProjectSpaceService | None = None):
    feedback = feedback or FeedbackService(app.session, app.db_path.parent / "feedback")
    # No visitor reader configured means the tour is unavailable, not open: the routes
    # still answer, but every read fails closed.
    visitor = visitor or VisitorService(VisitorReader(None))
    release_store = ReleaseStore(update_releases_dir)
    # Unset means the download routes answer 503 rather than serving something
    # unintended; the same fail-closed posture the update store takes. Injectable so the
    # routes can be exercised on a platform without the POSIX directory primitives the
    # real store is built on.
    installer = installer or InstallerStore(installer_dir)
    # Injectable so the refusal path can be exercised without holding eight real
    # transfers open. It must be shared across requests, which is why it lives here and
    # not on the handler.
    download_slots = download_slots or threading.BoundedSemaphore(MAX_CONCURRENT_DOWNLOADS)
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
            # The workbook module writes its own response and returns whether it owned the
            # path; it has to run before the host's routes because it authenticates itself.
            if project_spaces is not None and dispatch_project_spaces(
                    self, project_spaces, _bearer_token(self.headers), "GET"):
                return
            if project_spaces is not None and dispatch_project_spaces(
                    self, project_spaces, _bearer_token(self.headers), "POST"):
                return
            if project_workbooks is not None and dispatch_project_workbooks(self, project_workbooks):
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
            if path in (DOWNLOAD_ROOT, DOWNLOAD_ROOT + "/"):
                self._serve_download_index()
                return
            if path == INSTALLER_FILE_PATH:
                self._serve_installer()
                return
            if path == INSTALLER_DIGEST_PATH:
                self._serve_installer_digest()
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
            # Wired so the module's create and import routes answer the contract's 503 rather
            # than falling through to the host's 404, which would read as "no such endpoint".
            if project_workbooks is not None and dispatch_project_workbooks(self, project_workbooks):
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

        def _serve_installer(self) -> None:
            """Streams the newest first-install package.

            Unauthenticated on purpose: it is meant to be pasted to a colleague who has
            nothing installed yet, and the LAN is the boundary. The package is unsigned,
            so the digest route beside it is what lets a download be checked.
            """
            if not installer.configured:
                self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                return
            # Refused rather than queued: a queued download would hold its worker slot
            # while waiting for another, which is the exhaustion this exists to stop.
            if not download_slots.acquire(blocking=False):
                self.send_response(503)
                self.send_header("Retry-After", "30")
                self._send_release_headers("application/json; charset=utf-8", None, '"busy"')
                self.end_headers()
                self.wfile.write(json.dumps(
                    {"success": False, "code": "DOWNLOAD_BUSY", "error": "同时下载的人数过多，请稍后重试。"},
                    ensure_ascii=False).encode("utf-8"))
                return

            opened = None
            try:
                try:
                    name = installer.latest_name()
                    # One descriptor for the ETag and the bytes: a checksum computed from
                    # a separate open could describe a file the caller never receives.
                    opened, digest = installer.open_verified(name)
                except ReleaseNotFound:
                    self._send_error(ApiError(404, "INSTALLER_NOT_FOUND", "尚未发布安装包。"))
                    return
                except (ReleaseUnavailable, OSError):
                    self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                    return

                etag = f'"{digest}"'
                if self._etag_matches(self.headers.get("If-None-Match"), etag):
                    self.send_response(304)
                    self._send_release_headers("application/octet-stream", None, etag)
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
                        self._send_release_headers("application/octet-stream", 0, etag)
                        self.send_header("Accept-Ranges", "bytes")
                        self.end_headers()
                        return
                    if parsed_range is not None:
                        start, end = parsed_range
                        partial = True

                length = max(0, end - start + 1)
                self.send_response(206 if partial else 200)
                self._send_release_headers("application/octet-stream", length, etag)
                self.send_header("Accept-Ranges", "bytes")
                # A browser needs the filename, and it is the versioned one rather than
                # the URL's stable alias so a saved copy says which version it is.
                self.send_header("Content-Disposition", f'attachment; filename="{name}"')
                if partial:
                    self.send_header("Content-Range", f"bytes {start}-{end}/{opened.size}")
                self.end_headers()
                os.lseek(opened.file_descriptor, start, os.SEEK_SET)
                deadline = time.monotonic() + MAX_DOWNLOAD_SECONDS
                remaining = length
                while remaining:
                    chunk = os.read(opened.file_descriptor, min(RELEASE_CHUNK_BYTES, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
                    if remaining and time.monotonic() > deadline:
                        # The client is not draining. Ending the response costs it a
                        # truncated download; continuing would hold this worker slot,
                        # and a share of the API's capacity, for as long as it stalled.
                        self.close_connection = True
                        break
            finally:
                if opened is not None:
                    opened.close()
                download_slots.release()

        def _serve_installer_digest(self) -> None:
            if not installer.configured:
                self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                return
            try:
                name = installer.latest_name()
                digest = installer.sha256(name)
            except ReleaseNotFound:
                self._send_error(ApiError(404, "INSTALLER_NOT_FOUND", "尚未发布安装包。"))
                return
            except (ReleaseUnavailable, OSError):
                self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                return

            # The format `sha256sum -c` expects, so the file can be saved next to the
            # download and verified without the reader reformatting anything.
            body = f"{digest}  {name}\n".encode("utf-8")
            self.send_response(200)
            self._send_release_headers("text/plain; charset=utf-8", len(body), f'"{digest}"')
            self.end_headers()
            self.wfile.write(body)

        def _serve_download_index(self) -> None:
            """The page a colleague is handed.

            A page with a link, not an index of paths. This URL exists to be pasted to
            someone who has nothing installed, and printing "/download/muying-setup.exe"
            as text gives them nothing to click — which is exactly how it was reported:
            "只显示了黑屏和文字", no download.

            Nothing from the request is echoed into it — no Host header, no path — so it
            cannot reflect anything into a browser. Every value comes from the installer
            store, and the two that could in principle be odd are escaped.
            """
            if not installer.configured:
                self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                return

            try:
                name = installer.latest_name()
                opened, digest = installer.open_verified(name)
                try:
                    size = opened.size
                finally:
                    # Opened only to read the size; leaving it open leaks a descriptor
                    # per request, and on Windows holds a lock on the published file.
                    opened.close()
            except ReleaseNotFound:
                body = DOWNLOAD_PAGE_NOTHING
            except (ReleaseUnavailable, OSError):
                # 503 rather than a friendly 200: an operator who mistyped the directory,
                # or forgot to make the package read-only, should see the endpoint report
                # itself unhealthy instead of a page that reads as nothing published yet.
                self._send_error(ApiError(503, "INSTALLER_UNAVAILABLE", "安装包下载暂不可用。"))
                return
            else:
                body = DOWNLOAD_PAGE.replace("{{NAME}}", html.escape(name)) \
                                   .replace("{{DIGEST}}", html.escape(digest)) \
                                   .replace("{{SIZE}}", f"{size / (1024 * 1024):.1f}") \
                                   .replace("{{PATH}}", INSTALLER_FILE_PATH) \
                                   .replace("{{DIGEST_PATH}}", INSTALLER_DIGEST_PATH)

            encoded = body.encode("utf-8")
            self.send_response(200)
            self._send_release_headers("text/html; charset=utf-8", len(encoded),
                                       f'"{hashlib.sha256(encoded).hexdigest()}"')
            self.end_headers()
            self.wfile.write(encoded)

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
            # Set once the listener is wrapped. The loopback development path below
            # returns an unwrapped server, and a plain socket has no do_handshake.
            self.tls_enabled = False
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
                if self.tls_enabled:
                    # The handshake runs here, in a worker, not inside accept() on the
                    # accept thread. A client that opens a TCP connection and never sends
                    # a ClientHello would otherwise block that one thread indefinitely:
                    # the accept backlog fills and the entire listener stops answering,
                    # for both the API and the downloads, with no worker involved and
                    # nothing to time out. Here it costs one worker slot and ends by
                    # timeout.
                    #
                    # The listener cannot simply be given a timeout instead: CPython's
                    # socket.accept() explicitly forces the accepted socket back to
                    # blocking mode when the listener has one, so that would make the
                    # hang permanent rather than bound it.
                    request.settimeout(HANDSHAKE_TIMEOUT_SECONDS)
                    try:
                        request.do_handshake()
                    except OSError:
                        # Includes ssl.SSLError, a malformed ClientHello, and the timeout.
                        self.shutdown_request(request)
                        return
                # Do not let a slow or abandoned request hold a worker thread forever.
                request.settimeout(REQUEST_TIMEOUT_SECONDS)
                super().process_request_thread(request, client_address)
            finally:
                self._request_slots.release()

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
    # Read-only first cut: `projects`, `list_files` and `document` are backed by the project
    # directory; every other method on both protocols answers 503, which is the contract's own
    # way of saying "not implemented". Unset means the module is not served at all and those
    # paths fall through to the host's 404.
    provisioner = ProjectProvisioner(settings.project_socket) if ProjectProvisioner is not None else None
    project_workbooks = None
    if settings.project_docs_root is not None and ProjectsApi is not None:
        project_workbooks = ProjectsApi(
            NasSecurity(app.session),
            NasWorkbookServices(settings.project_docs_root),
            workflow=NasWorkflowServices(settings.project_docs_root, provisioner=provisioner))
    # Listing needs only the directory; creating needs the root helper. Unset socket means
    # listing still works and creating answers 503 with the reason, which is better than a
    # + that appears to work.
    project_spaces = None
    if settings.project_docs_root is not None and ProjectSpaceService is not None:
        project_spaces = ProjectSpaceService(
            settings.project_docs_root, app.session, provisioner)
    server = V2HTTPServer((settings.bind_host, settings.port),
                          create_handler(app, update_releases_dir=settings.update_releases_dir,
                                         installer_dir=settings.installer_dir,
                                         update_failure_db_path=settings.database_path,
                                         feedback=feedback, visitor=visitor,
                                         project_workbooks=project_workbooks,
                                         project_spaces=project_spaces))
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
    # do_handshake_on_connect=False so accept() returns as soon as TCP connects; the
    # handshake is done in the worker, where it can be given a timeout. See
    # process_request_thread.
    server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    server.tls_enabled = True
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

