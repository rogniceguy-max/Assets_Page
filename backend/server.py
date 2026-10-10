# -*- coding: utf-8 -*-
"""
MuYing Hub - 轻量模块化 Web 服务端 (SPA 生产就绪版)
纯 Python 3.11 原生库构建，托管 React+Vite+Tailwind+shadcn/ui 生产产物，提供高效 RESTful API
已集成：
- Windows 原生凭据挂载体系（废弃临时 Token 与服务端明文下发）
- 静态资源路径规范化与目录穿越全方位防御
- scrypt 密码加盐哈希与存量平滑迁移机制
- TCP 原生客户端 IP 审计
"""
import os
import sys
import json
import re
import urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

# 引入本工程模块
from config import HOST, PORT, BASE_DIR, NAS_IP, NAS_HOSTNAME, SHARE_NAME, MOUNT_DRIVE, DRIVE_LABEL
from database import (
    init_db, 
    add_user, 
    get_user_by_name, 
    check_ip_registered, 
    hash_password, 
    verify_password, 
    update_user_password_hash, 
    log_download
)
from core.nas_manager import get_unique_linux_username, create_system_and_samba_user, delete_system_and_samba_user
from core.script_generator import generate_windows_batch

class MuYingHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        print(f"[{self.log_date_time_string()}] {self.command} {self.path} - {format%args}")

    def send_json(self, status_code: int, data: dict):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status_code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Access-Control-Allow-Origin', '*')
        self.end_headers()
        self.wfile.write(body)

    def parse_json_body(self) -> dict:
        content_length = int(self.headers.get('Content-Length', 0))
        if content_length <= 0:
            return {}
        raw_body = self.rfile.read(content_length).decode('utf-8')
        try:
            return json.loads(raw_body)
        except Exception:
            return {}

    def get_client_ip(self) -> str:
        """
        获取客户端真实 IP。
        内网直连部署环境下，直接基于底层 TCP 套接字地址提取，严禁无条件信任伪造的 X-Forwarded-For。
        """
        return self.client_address[0]

    def do_HEAD(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query)

        # 1. 静态资源路由（Vite 生产产物 assets/），包含严格路径规范化与目录穿越防御
        if path.startswith("/assets/"):
            rel_path = path[len("/assets/"):]
            assets_dir = os.path.realpath(os.path.join(BASE_DIR, "dist", "assets"))
            
            # URL 解码并规范化路径，防范 ..、%2e%2e 与空字符穿越
            decoded_rel = urllib.parse.unquote(rel_path)
            if '\x00' in decoded_rel:
                self.send_error(400, "Bad Request: Null Byte Detected")
                return

            clean_rel = os.path.normpath(decoded_rel).lstrip("/\\")
            full_path = os.path.realpath(os.path.join(assets_dir, clean_rel))

            # 严格目录穿越安全校验：确保目标文件完全限制在 dist/assets 内
            if not full_path.startswith(assets_dir + os.sep) and full_path != assets_dir:
                self.send_error(403, "Forbidden: Directory Traversal Blocked")
                return

            # 安全扩展名白名单检测（禁止访问 .py, .db, .env 等后端机密数据）
            allowed_exts = {'.js', '.css', '.svg', '.png', '.jpg', '.jpeg', '.ico', '.woff2', '.woff', '.ttf', '.map'}
            ext = os.path.splitext(full_path)[1].lower()
            if ext not in allowed_exts:
                self.send_error(403, "Forbidden: File Type Not Allowed")
                return

            if os.path.exists(full_path) and os.path.isfile(full_path):
                mime_map = {
                    '.js': 'application/javascript; charset=utf-8',
                    '.css': 'text/css; charset=utf-8',
                    '.svg': 'image/svg+xml',
                    '.png': 'image/png',
                    '.jpg': 'image/jpeg',
                    '.jpeg': 'image/jpeg',
                    '.ico': 'image/x-icon',
                    '.woff2': 'font/woff2',
                    '.woff': 'font/woff',
                    '.ttf': 'font/ttf',
                    '.map': 'application/json'
                }
                ctype = mime_map.get(ext, 'application/octet-stream')
                with open(full_path, "rb") as f:
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
        elif path == "/api/user-info":
            name = query.get('name', query.get('username', ['']))[0].strip()
            if not name:
                self.send_json(400, {"success": False, "error": "缺少姓名参数"})
                return
            user = get_user_by_name(name)
            if not user:
                self.send_json(404, {"success": False, "error": "用户不存在"})
                return
            safe_user = {
                "display_name": user["display_name"],
                "linux_username": user["linux_username"],
                "group_type": user["group_type"],
                "created_at": user["created_at"]
            }
            self.send_json(200, {"success": True, "user": safe_user})
            return

        elif path == "/api/download-script":
            # Windows 原生挂载工具下载：优先下发固定通用脚本，若传入参数必须通过严格白名单和数据库存在性校验
            name = query.get('name', query.get('user', query.get('username', [''])))[0].strip()
            linux_user = ""
            fullname = ""
            if name and len(name) <= 32:
                user = get_user_by_name(name)
                if user:
                    c_user = str(user.get("linux_username", "")).strip()
                    if re.match(r'^[a-zA-Z0-9_-]{1,32}$', c_user):
                        linux_user = c_user
                        fullname = str(user.get("display_name", "")).strip()

            # 生成 Windows 原生挂载工具（零密码、零 Token、纯原生凭据对话）
            script_content = generate_windows_batch(linux_user, fullname)

            client_ip = self.get_client_ip()
            log_download(fullname or linux_user or "universal_user", client_ip)

            data = script_content.encode('utf-8')
            # 严格 ASCII 文件名，杜绝 HTTP 响应头注入 (CRLF / Header Splitting)
            filename = f"Mount_MuYing_{linux_user}.bat" if linux_user else "Mount_MuYing.bat"

            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Disposition', f'attachment; filename="{filename}"')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return

        # 3. SPA 单页路由 Fallback (返回 dist/index.html)
        else:
            index_path = os.path.join(BASE_DIR, "dist", "index.html")
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

        if path == "/api/register":
            data = self.parse_json_body()
            fullname = str(data.get("fullname", "")).strip()
            password = str(data.get("password", "")).strip()
            group_type = str(data.get("group_type", "film")).strip()
            client_ip = self.get_client_ip()

            if not fullname or not password:
                self.send_json(400, {"success": False, "error": "请填写姓名与密码"})
                return

            if len(fullname) < 2:
                self.send_json(400, {"success": False, "error": "请输入完整姓名"})
                return

            if len(password) < 6:
                self.send_json(400, {"success": False, "error": "密码长度不能少于 6 位"})
                return

            if group_type not in ["film", "writing"]:
                group_type = "film"

            # 1. 严格检查：一个 IP 只能注册一次
            existing_ip_user = check_ip_registered(client_ip)
            if existing_ip_user:
                self.send_json(400, {
                    "success": False,
                    "error": f"安全限制：本机 IP（{client_ip}）已注册过员工（{existing_ip_user['display_name']}）。每个 IP 仅限注册一次。如需重新挂载，请点击【重新挂载】。"
                })
                return

            # 2. 检查姓名是否已被占用
            existing_name = get_user_by_name(fullname)
            if existing_name:
                self.send_json(400, {
                    "success": False,
                    "error": f"该姓名（{fullname}）已注册过。请直接在【登录】或【重新挂载】中输入姓名与密码。"
                })
                return

            # 3. 生成唯一的系统 Linux 用户名
            linux_username = get_unique_linux_username(fullname)

            # 4. 在 NAS 底层创建用户、配置 Samba 密码及个人空间
            success, msg = create_system_and_samba_user(linux_username, password, group_type, fullname)
            if not success:
                self.send_json(500, {"success": False, "error": f"系统开通失败: {msg}"})
                return

            # 5. 保存入库（scrypt 加盐安全哈希，绝不向数据库写入明文密码）
            try:
                add_user(fullname, linux_username, password, group_type, client_ip)
            except Exception as e:
                # 异常补偿：数据库落盘失败时回滚 NAS 系统与 Samba 账号，确保状态严格一致
                delete_system_and_samba_user(linux_username, fullname)
                self.send_json(500, {"success": False, "error": f"保存数据库异常，已回滚系统配置: {str(e)}"})
                return

            self.send_json(200, {
                "success": True,
                "message": "注册成功",
                "fullname": fullname,
                "linux_username": linux_username,
                "group_type": group_type
            })
            return

        elif path in ["/api/verify", "/api/login"]:
            data = self.parse_json_body()
            fullname = str(data.get("fullname", data.get("username", ""))).strip()
            password = str(data.get("password", "")).strip()
            client_ip = self.get_client_ip()

            if not fullname or not password:
                self.send_json(400, {"success": False, "error": "请填写姓名与密码"})
                return

            user = get_user_by_name(fullname)
            if not user:
                self.send_json(401, {"success": False, "error": "姓名或密码不正确"})
                return

            # 使用 scrypt 与兼容性校验
            is_valid, needs_rehash = verify_password(password, user["password_hash"])
            if not is_valid:
                self.send_json(401, {"success": False, "error": "姓名或密码不正确"})
                return

            # 平滑迁移：旧版 SHA-256 密码哈希自动无感升级为现代 scrypt 加盐哈希
            if needs_rehash:
                try:
                    new_scrypt_hash = hash_password(password)
                    update_user_password_hash(user["username"], new_scrypt_hash)
                    print(f"[安全升级] 用户 {user['username']} 密码哈希已成功平滑迁移至 scrypt！")
                except Exception as ex:
                    print(f"[迁移告警] 更新哈希异常: {ex}")

            msg = "登录成功" if path == "/api/login" else "验证成功"
            self.send_json(200, {
                "success": True,
                "message": msg,
                "fullname": user["display_name"],
                "linux_username": user["linux_username"],
                "group_type": user["group_type"]
            })
            return

        elif path == "/api/claim-mount":
            # 彻底废弃旧接口，杜绝任何明文凭据向客户端网络下发
            self.send_json(410, {
                "success": False, 
                "error": "接口已永久废弃 (410 Gone)。系统已升级为 Windows 原生凭据挂载体系，不再提供网络凭据明文下发。"
            })
            return

        else:
            self.send_error(404, "Not Found")

def run_server():
    init_db()
    server_address = (HOST, PORT)
    httpd = ThreadingHTTPServer(server_address, MuYingHandler)
    print(f"=====================================================")
    print(f"  MuYing Hub (React SPA 生产版 + Windows 原生凭据架构) 已成功启动！")
    print(f"  局域网访问地址: http://192.168.1.153:{PORT}")
    print(f"=====================================================")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n服务正在关闭...")
        httpd.server_close()

if __name__ == "__main__":
    run_server()
