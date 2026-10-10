# -*- coding: utf-8 -*-
"""
MuYing Hub - 轻量模块化 Web 服务端 (SPA 生产就绪版)
纯 Python 3.11 原生库构建，托管 React+Vite+Tailwind+shadcn/ui 生产产物，提供高效 RESTful API
已集成 60 秒一次性短期凭据票据（Ephemeral One-Time Token）安全体系与零明文存储机制
"""
import os
import sys
import json
import urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

# 引入本工程模块
from config import HOST, PORT, BASE_DIR, NAS_IP, NAS_HOSTNAME, SHARE_NAME, MOUNT_DRIVE, DRIVE_LABEL
from database import init_db, add_user, get_user_by_name, check_ip_registered, hash_password, log_download
from core.nas_manager import get_unique_linux_username, create_system_and_samba_user
from core.script_generator import generate_windows_batch
from core.token_manager import token_manager

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
        forwarded = self.headers.get('X-Forwarded-For')
        if forwarded:
            return forwarded.split(',')[0].strip()
        return self.client_address[0]

    def do_HEAD(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.end_headers()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query)

        # 1. 静态资源路由（Vite 生产产物 assets/）
        if path.startswith("/assets/"):
            rel_path = path[len("/assets/"):]
            full_path = os.path.join(BASE_DIR, "dist", "assets", rel_path)
            if os.path.exists(full_path) and os.path.isfile(full_path):
                ext = os.path.splitext(full_path)[1].lower()
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
            name = query.get('name', query.get('username', ['']))[0]
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
            token = query.get('token', [''])[0]
            if not token:
                self.send_json(403, {"success": False, "error": "拒绝访问：缺少有效安全令牌。请先在网页端注册或重新领脚本。"})
                return

            ticket_info = token_manager.get_token_info(token)
            if not ticket_info:
                self.send_json(403, {"success": False, "error": "安全令牌不存在或已过期（超过 60 秒窗口）。请在网页端点击【重新领脚本】重新获取。"})
                return

            linux_user = ticket_info["linux_username"]
            fullname = ticket_info["fullname"]

            # 生成该员工本人的专属动态脚本（绝无明文密码，仅包含该 60 秒临时令牌）
            script_content = generate_windows_batch(linux_user, token, fullname)

            client_ip = self.get_client_ip()
            log_download(fullname, client_ip)

            data = script_content.encode('utf-8')
            filename = f"Mount_MuYing_{linux_user}.bat"
            quoted_filename = urllib.parse.quote(filename)

            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Disposition', f'attachment; filename="{filename}"; filename*=UTF-8\'\'{quoted_filename}')
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
                    "error": f"安全限制：本机 IP（{client_ip}）已注册过员工（{existing_ip_user['display_name']}）。每个 IP 仅限注册一次。如需重新获取脚本，请点击【重新领脚本】。"
                })
                return

            # 2. 检查姓名是否已被占用
            existing_name = get_user_by_name(fullname)
            if existing_name:
                self.send_json(400, {
                    "success": False,
                    "error": f"该姓名（{fullname}）已注册过。请直接在【重新领脚本】中输入姓名与密码获取专属脚本。"
                })
                return

            # 3. 生成唯一的系统 Linux 用户名
            linux_username = get_unique_linux_username(fullname)

            # 4. 在 NAS 底层创建用户、配置 Samba 密码及个人空间
            success, msg = create_system_and_samba_user(linux_username, password, group_type, fullname)
            if not success:
                self.send_json(500, {"success": False, "error": f"系统开通失败: {msg}"})
                return

            # 5. 保存入库（安全零明文：绝不向数据库写入原始密码）
            try:
                add_user(fullname, linux_username, password, group_type, client_ip)
            except Exception as e:
                self.send_json(500, {"success": False, "error": f"保存数据库异常: {str(e)}"})
                return

            # 6. 在后端内存开启 60 秒一次性安全兑换窗口
            token = token_manager.create_token(linux_username, password, fullname, client_ip, ttl=60)

            self.send_json(200, {
                "success": True,
                "message": "注册成功",
                "token": token,
                "fullname": fullname,
                "linux_username": linux_username,
                "group_type": group_type,
                "expires_in": 60
            })
            return

        elif path == "/api/verify":
            data = self.parse_json_body()
            fullname = str(data.get("fullname", "")).strip()
            password = str(data.get("password", "")).strip()
            client_ip = self.get_client_ip()

            user = get_user_by_name(fullname)
            if not user or user["password_hash"] != hash_password(password):
                self.send_json(401, {"success": False, "error": "姓名或密码不正确"})
                return

            # 验证通过：在后端内存开启 60 秒一次性安全兑换窗口
            token = token_manager.create_token(user["linux_username"], password, user["display_name"], client_ip, ttl=60)

            self.send_json(200, {
                "success": True,
                "message": "验证成功",
                "token": token,
                "fullname": user["display_name"],
                "linux_username": user["linux_username"],
                "group_type": user["group_type"],
                "expires_in": 60
            })
            return

        elif path == "/api/claim-mount":
            # 客户端挂载脚本动态兑换凭据（单次消费焚毁）
            data = self.parse_json_body()
            token = str(data.get("token", "")).strip()
            if not token:
                self.send_json(400, {"success": False, "error": "缺少有效安全令牌 (Missing Token)"})
                return

            client_ip = self.get_client_ip()
            success, result = token_manager.claim_token(token, client_ip)
            if not success:
                self.send_json(403, {"success": False, "error": result})
                return

            self.send_json(200, {
                "success": True,
                "linux_username": result["linux_username"],
                "password": result["password"],
                "fullname": result["fullname"]
            })
            return

        else:
            self.send_error(404, "Not Found")

def run_server():
    init_db()
    server_address = (HOST, PORT)
    httpd = ThreadingHTTPServer(server_address, MuYingHandler)
    print(f"=====================================================")
    print(f"  MuYing Hub (React SPA 生产版 + 60s 凭据安全体系) 已成功启动！")
    print(f"  局域网访问地址: http://192.168.1.153:{PORT}")
    print(f"=====================================================")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n服务正在关闭...")
        httpd.server_close()

if __name__ == "__main__":
    run_server()
