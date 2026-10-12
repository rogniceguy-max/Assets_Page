# -*- coding: utf-8 -*-
"""Local-only mock service for the MuYing Drive first-version spike."""
import os
import json
import sqlite3
import hmac
import urllib.parse
from http.cookies import SimpleCookie
from pathlib import Path
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

# 引入本工程模块
from config import FRONTEND_DIST_DIR, HOST, PORT
from database import (
    add_user,
    get_user_by_id,
    get_user_by_username,
    init_db,
    normalize_username,
    remove_user,
    verify_password,
)
from core.nas_manager import (
    create_local_mock_personal_directory,
    ensure_local_mock_roots,
)
from core.file_service import (
    FileServiceError,
    list_directory,
    open_download,
    parse_range_header,
)
from core.session_store import (
    SESSION_COOKIE_MAX_AGE_SECONDS,
    SESSION_COOKIE_NAME,
    SESSION_STORE,
)
from core.upload_store import MAX_UPLOAD_CHUNK_BYTES, UPLOAD_STORE

MAX_JSON_BODY_BYTES = 1024 * 1024

class MuYingHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        path = urllib.parse.urlparse(self.path).path
        if path.startswith("/api/uploads/"):
            path = "/api/uploads/{id}"
        print(f"[{self.log_date_time_string()}] {self.command} {path} - {format%args}")

    def send_json(self, status_code: int, data: dict, headers: dict = None):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status_code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    @staticmethod
    def _session_cookie(token: str) -> str:
        return (
            f"{SESSION_COOKIE_NAME}={token}; Path=/; Max-Age={SESSION_COOKIE_MAX_AGE_SECONDS}; "
            "HttpOnly; SameSite=Strict"
        )

    @staticmethod
    def _clear_session_cookie() -> str:
        return f"{SESSION_COOKIE_NAME}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"

    def _session_token(self):
        cookie = SimpleCookie()
        try:
            cookie.load(self.headers.get("Cookie", ""))
        except Exception:
            return None
        morsel = cookie.get(SESSION_COOKIE_NAME)
        return morsel.value if morsel else None

    def _current_auth(self):
        token = self._session_token()
        if not token:
            return None, None, None
        session = SESSION_STORE.get(token)
        if session is None:
            return token, None, None
        user = get_user_by_id(session.user_id)
        if not user or user.get("status") != "active":
            SESSION_STORE.revoke(token)
            return token, None, None
        return token, session, user

    def _require_auth(self):
        token, session, user = self._current_auth()
        if session is None or user is None:
            headers = {"Set-Cookie": self._clear_session_cookie()} if token else None
            self.send_json(401, {"success": False, "error": "请先登录"}, headers)
            return None
        return token, session, user

    def _require_csrf(self, session) -> bool:
        supplied = self.headers.get("X-CSRF-Token", "")
        try:
            valid = hmac.compare_digest(
                supplied.encode("utf-8"), session.csrf_token.encode("utf-8")
            ) if supplied else False
        except UnicodeEncodeError:
            valid = False
        if not valid:
            self.send_json(403, {"success": False, "error": "CSRF 校验失败"})
            return False
        return True

    def _send_file_error(self, error: FileServiceError, extra_headers: dict = None):
        payload = {"success": False, "error": error.message}
        payload.update(error.details)
        self.send_json(error.status_code, payload, extra_headers)

    def _stream_download(self, user: dict, raw_path: str):
        try:
            source, filename, file_size = open_download(user, raw_path)
        except FileServiceError as error:
            self._send_file_error(error)
            return

        try:
            byte_range = parse_range_header(self.headers.get("Range", ""), file_size)
        except FileServiceError as error:
            source.close()
            self._send_file_error(
                error,
                {"Content-Range": f"bytes */{file_size}", "Accept-Ranges": "bytes"},
            )
            return

        if byte_range is None:
            status_code = 200
            start = 0
            content_length = file_size
        else:
            status_code = 206
            start, end = byte_range
            content_length = end - start + 1
            source.seek(start)

        quoted_filename = urllib.parse.quote(filename, safe="")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(content_length))
        self.send_header("Content-Disposition", f"attachment; filename=\"download\"; filename*=UTF-8''{quoted_filename}")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Cache-Control", "private, no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        if byte_range is not None:
            self.send_header("Content-Range", f"bytes {start}-{start + content_length - 1}/{file_size}")
        self.end_headers()

        remaining = content_length
        try:
            while remaining:
                chunk = source.read(min(64 * 1024, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            source.close()

    def parse_json_body(self) -> dict:
        if self.headers.get("Transfer-Encoding"):
            self.close_connection = True
            self.send_json(
                411,
                {"success": False, "error": "JSON 请求必须使用 Content-Length"},
                {"Connection": "close"},
            )
            return None
        try:
            content_length = int(self.headers.get("Content-Length", 0))
        except (TypeError, ValueError):
            self.close_connection = True
            self.send_json(400, {"success": False, "error": "请求长度无效"}, {"Connection": "close"})
            return None
        if content_length < 0:
            self.close_connection = True
            self.send_json(400, {"success": False, "error": "请求长度无效"}, {"Connection": "close"})
            return None
        if content_length > MAX_JSON_BODY_BYTES:
            self.close_connection = True
            self.send_json(
                413,
                {"success": False, "error": "JSON 请求体不能超过 1 MiB"},
                {"Connection": "close"},
            )
            return None
        if content_length <= 0:
            return {}
        try:
            raw_bytes = self.rfile.read(content_length)
            if len(raw_bytes) != content_length:
                self.close_connection = True
                self.send_json(400, {"success": False, "error": "请求体未完整到达"}, {"Connection": "close"})
                return None
            raw_body = raw_bytes.decode("utf-8")
            payload = json.loads(raw_body)
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
            self.send_json(400, {"success": False, "error": "JSON 请求体无效"})
            return None
        if not isinstance(payload, dict):
            self.send_json(400, {"success": False, "error": "JSON 请求体必须是对象"})
            return None
        return payload

    def do_HEAD(self):
        if urllib.parse.urlparse(self.path).path.startswith("/api/"):
            self.send_error(405, "Method Not Allowed")
            return
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query)

        # 1. 静态资源路由（Vite 生产产物 assets/）
        if path.startswith("/assets/"):
            rel_path = urllib.parse.unquote(path[len("/assets/"):])
            asset_root = (Path(FRONTEND_DIST_DIR) / "assets").resolve()
            try:
                full_path = (asset_root / rel_path).resolve()
                is_contained = full_path.is_relative_to(asset_root)
            except (OSError, RuntimeError, ValueError):
                full_path = asset_root / "__invalid_asset_path__"
                is_contained = False

            if is_contained and full_path.is_file():
                ext = full_path.suffix.lower()
                mime_map = {
                    '.js': 'application/javascript; charset=utf-8',
                    '.css': 'text/css; charset=utf-8',
                    '.svg': 'image/svg+xml',
                    '.png': 'image/png',
                    '.ico': 'image/x-icon',
                    '.woff2': 'font/woff2',
                    '.woff': 'font/woff'
                }
                ctype = mime_map.get(ext, 'application/octet-stream')
                with full_path.open("rb") as f:
                    content = f.read()
                self.send_response(200)
                self.send_header('Content-Type', ctype)
                self.send_header('Content-Length', str(len(content)))
                self.send_header('Cache-Control', 'public, max-age=31536000, immutable')
                self.end_headers()
                self.wfile.write(content)
                return
            else:
                self.send_error(404, "Asset Not Found")
                return

        # 2. API 路由
        elif path == "/api/session":
            token, session, user = self._current_auth()
            if session is None or user is None:
                headers = {"Set-Cookie": self._clear_session_cookie()} if token else None
                self.send_json(200, {"authenticated": False}, headers)
                return
            self.send_json(200, {
                "authenticated": True,
                "user": {
                    "username": user["username"],
                    "group_type": user["group_type"],
                },
                "csrf_token": session.csrf_token,
            })
            return

        elif path == "/api/files":
            auth = self._require_auth()
            if auth is None:
                return
            _token, _session, user = auth
            path_values = query.get("path", ["/"])
            if len(path_values) != 1:
                self.send_json(400, {"success": False, "error": "只能指定一个路径"})
                return
            try:
                self.send_json(200, list_directory(user, path_values[0]))
            except FileServiceError as error:
                self._send_file_error(error)
            return

        elif path == "/api/files/download":
            auth = self._require_auth()
            if auth is None:
                return
            _token, _session, user = auth
            path_values = query.get("path", [])
            if len(path_values) != 1:
                self.send_json(400, {"success": False, "error": "必须指定一个文件路径"})
                return
            self._stream_download(user, path_values[0])
            return

        elif path.startswith("/api/uploads/"):
            auth = self._require_auth()
            if auth is None:
                return
            _token, session, user = auth
            suffix = path[len("/api/uploads/"):]
            if not suffix or "/" in suffix:
                self.send_json(404, {"success": False, "error": "上传任务不存在"})
                return
            try:
                self.send_json(
                    200,
                    UPLOAD_STORE.status(suffix, user["id"], session.session_key),
                )
            except FileServiceError as error:
                self._send_file_error(error)
            return

        elif path == "/api/user-info":
            auth = self._require_auth()
            if auth is None:
                return
            _token, _session, user = auth
            safe_user = {
                "username": user["username"],
                "display_name": user["display_name"],
                "linux_username": user["linux_username"],
                "group_type": user["group_type"],
                "created_at": user["created_at"]
            }
            self.send_json(200, {"success": True, "user": safe_user})
            return

        elif path == "/api/download-script":
            self.send_error(410, "Credential script downloads are disabled")
            return

        # Unknown API paths must not fall through to the SPA document.
        elif path == "/api" or path.startswith("/api/"):
            self.send_json(404, {"success": False, "error": "接口不存在"})
            return

        # 3. SPA 单页路由 Fallback (返回前端构建产物)
        else:
            index_path = os.path.join(FRONTEND_DIST_DIR, "index.html")
            if os.path.exists(index_path):
                with open(index_path, "rb") as f:
                    content = f.read()
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.send_header('Content-Length', str(len(content)))
                self.end_headers()
                self.wfile.write(content)
                return
            else:
                self.send_error(404, "SPA Build Not Found")
                return

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        if path == "/api/logout":
            token, session, _user = self._current_auth()
            if session is not None:
                if not self._require_csrf(session):
                    return
            if token:
                session_key = (
                    session.session_key
                    if session is not None
                    else SESSION_STORE.session_key_for_token(token)
                )
                if session_key:
                    UPLOAD_STORE.discard_session(session_key)
                SESSION_STORE.revoke(token)
            self.send_json(
                200,
                {"success": True},
                {"Set-Cookie": self._clear_session_cookie()},
            )
            return

        if path == "/api/uploads":
            auth = self._require_auth()
            if auth is None:
                return
            _token, session, user = auth
            if not self._require_csrf(session):
                return

            data = self.parse_json_body()
            if data is None:
                return
            directory_path = data.get("path")
            filename = data.get("filename")
            size = data.get("size")
            if not isinstance(directory_path, str) or not isinstance(filename, str):
                self.send_json(400, {"success": False, "error": "必须提供目录路径和文件名"})
                return
            if isinstance(size, bool) or not isinstance(size, int):
                self.send_json(400, {"success": False, "error": "文件大小必须是整数"})
                return
            try:
                _upload_id, payload = UPLOAD_STORE.begin(
                    user, session.session_key, directory_path, filename, size
                )
                self.send_json(201, payload)
            except FileServiceError as error:
                self._send_file_error(error)
            return

        if path.startswith("/api/uploads/") and path.endswith("/complete"):
            auth = self._require_auth()
            if auth is None:
                return
            _token, session, user = auth
            if not self._require_csrf(session):
                return
            parts = path.split("/")
            if len(parts) != 5 or not parts[3] or parts[4] != "complete":
                self.send_json(404, {"success": False, "error": "上传任务不存在"})
                return
            try:
                payload = UPLOAD_STORE.complete(user, session.session_key, parts[3])
                self.send_json(201, payload)
            except FileServiceError as error:
                self._send_file_error(error)
            return

        if path == "/api/register":
            data = self.parse_json_body()
            if data is None:
                return
            username = normalize_username(data.get("username", data.get("fullname", "")))
            password = data.get("password", "")
            group_type = data.get("group_type")

            if username is None:
                self.send_json(400, {"success": False, "error": "请填写用户名"})
                return

            if not isinstance(password, str):
                self.send_json(400, {"success": False, "error": "密码格式不正确"})
                return

            if len(password) < 8:
                self.send_json(400, {"success": False, "error": "密码长度不能少于 8 位"})
                return

            if group_type not in ["film", "writing"]:
                self.send_json(400, {"success": False, "error": "请选择有效的部门"})
                return

            # Registration identity is the exact user-provided account name.
            existing_user = get_user_by_username(username)
            if existing_user:
                self.send_json(400, {
                    "success": False,
                    "error": "该用户名已注册"
                })
                return

            # Persist only the scrypt verifier. The current backend uses a local
            # directory mock and never creates an OS or Samba account.
            user_id = None
            try:
                user_id = add_user(username, password, group_type)
                if not create_local_mock_personal_directory(user_id):
                    remove_user(user_id)
                    self.send_json(500, {"success": False, "error": "本地模拟开户失败"})
                    return
            except sqlite3.IntegrityError:
                self.send_json(409, {"success": False, "error": "该用户名已注册"})
                return
            except Exception:
                if user_id is not None:
                    remove_user(user_id)
                self.send_json(500, {"success": False, "error": "本地模拟开户失败"})
                return

            session_token, csrf_token = SESSION_STORE.create(user_id)
            self.send_json(200, {
                "success": True,
                "message": "本地模拟账号已创建；未创建系统或 Samba 账号",
                "username": username,
                "fullname": username,
                "linux_username": username,
                "group_type": group_type,
                "csrf_token": csrf_token,
            }, {"Set-Cookie": self._session_cookie(session_token)})
            return

        elif path == "/api/verify":
            data = self.parse_json_body()
            if data is None:
                return
            username = normalize_username(data.get("username", data.get("fullname", "")))
            password = data.get("password", "")

            if username is None or not isinstance(password, str):
                self.send_json(401, {"success": False, "error": "用户名或密码不正确"})
                return

            user = get_user_by_username(username)
            if not user or not verify_password(password, user["password_hash"]):
                self.send_json(401, {"success": False, "error": "用户名或密码不正确"})
                return

            if user.get("status") != "active":
                self.send_json(403, {"success": False, "error": "账号已停用"})
                return
            session_token, csrf_token = SESSION_STORE.create(user["id"])
            self.send_json(
                200,
                {
                    "success": True,
                    "username": username,
                    "fullname": username,
                    "csrf_token": csrf_token,
                },
                {"Set-Cookie": self._session_cookie(session_token)},
            )
            return

        else:
            self.send_error(404, "Not Found")

    def do_PUT(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        prefix = "/api/uploads/"
        if not path.startswith(prefix):
            self.send_error(404, "Not Found")
            return

        upload_id = path[len(prefix):]
        if not upload_id or "/" in upload_id:
            self.send_json(404, {"success": False, "error": "上传任务不存在"})
            return

        # This API accepts bounded-by-Content-Length chunks only; unsupported
        # request framing is rejected before reading the body.
        if self.headers.get("Transfer-Encoding"):
            self.close_connection = True
            self.send_json(411, {"success": False, "error": "请使用 Content-Length 发送上传分块"}, {"Connection": "close"})
            return
        try:
            content_length = int(self.headers.get("Content-Length", ""))
        except ValueError:
            content_length = -1
        if content_length < 0:
            self.close_connection = True
            self.send_json(411, {"success": False, "error": "缺少有效 Content-Length"}, {"Connection": "close"})
            return
        if content_length > MAX_UPLOAD_CHUNK_BYTES:
            self.close_connection = True
            self.send_json(
                413,
                {"success": False, "error": "单个上传分块不能超过 4 MiB"},
                {"Connection": "close"},
            )
            return
        if content_length == 0:
            self.send_json(400, {"success": False, "error": "上传分块不能为空"})
            return

        auth = self._require_auth()
        if auth is None:
            self.close_connection = True
            return
        _token, session, user = auth
        if not self._require_csrf(session):
            self.close_connection = True
            return

        try:
            payload = UPLOAD_STORE.append_chunk(
                user,
                session.session_key,
                upload_id,
                self.headers.get("Content-Range", ""),
                content_length,
                self.rfile,
            )
            self.send_json(202, payload)
        except FileServiceError as error:
            self.close_connection = True
            self._send_file_error(error, {"Connection": "close"})

def run_server():
    init_db()
    ensure_local_mock_roots()
    UPLOAD_STORE.clear_abandoned_staging()
    server_address = (HOST, PORT)
    httpd = ThreadingHTTPServer(server_address, MuYingHandler)
    print(f"=====================================================")
    print(f"  MuYing Hub 本地模拟服务已启动。", flush=True)
    print(f"  绑定地址: {HOST}:{httpd.server_port}；不会创建系统或 Samba 账号，也不会连接 NAS。", flush=True)
    print(f"READY_URL=http://{HOST}:{httpd.server_port}/", flush=True)
    print(f"=====================================================")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n服务正在关闭...")
        httpd.server_close()

if __name__ == "__main__":
    run_server()
