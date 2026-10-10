# -*- coding: utf-8 -*-
"""
MuYing Hub - 一次性短期凭据票据（Ephemeral One-Time Token）管理模块
提供 60 秒有效期的凭据兑换机制，内存自动焚毁，数据库零明文存储
"""
import time
import secrets
import threading

class EphemeralTokenManager:
    def __init__(self, default_ttl: int = 60):
        self.default_ttl = default_ttl
        # token -> dict(token, linux_username, raw_password, fullname, client_ip, created_at, expires_at, used)
        self._tokens = {}
        self._lock = threading.Lock()

    def create_token(self, linux_username: str, raw_password: str, fullname: str = "", client_ip: str = "", ttl: int = None) -> str:
        """创建 60 秒一次性凭据令牌（密码仅暂存于内存 RAM 中）"""
        ttl = ttl or self.default_ttl
        token = secrets.token_urlsafe(32)
        expires_at = time.time() + ttl

        with self._lock:
            self._cleanup_expired()
            self._tokens[token] = {
                "token": token,
                "linux_username": linux_username,
                "raw_password": raw_password,
                "fullname": fullname or linux_username,
                "client_ip": client_ip,
                "created_at": time.time(),
                "expires_at": expires_at,
                "used": False
            }
        return token

    def check_token_valid(self, token: str) -> bool:
        """检查令牌是否仍然有效（未被使用且未过期）"""
        if not token:
            return False
        with self._lock:
            ticket = self._tokens.get(token)
            if not ticket:
                return False
            if ticket["used"] or time.time() > ticket["expires_at"]:
                return False
            return True

    def get_token_info(self, token: str) -> dict:
        """获取令牌基本信息（不返回密码字段），供下载脚本使用"""
        if not token:
            return None
        with self._lock:
            ticket = self._tokens.get(token)
            if not ticket or ticket["used"] or time.time() > ticket["expires_at"]:
                return None
            return {
                "token": ticket["token"],
                "linux_username": ticket["linux_username"],
                "fullname": ticket["fullname"],
                "client_ip": ticket["client_ip"],
                "expires_at": ticket["expires_at"]
            }

    def claim_token(self, token: str, client_ip: str = "") -> tuple:
        """
        客户端脚本兑换凭据（单次消费焚毁机制）：
        成功返回 (True, credential_dict)
        失败返回 (False, error_message)
        """
        if not token:
            return False, "缺少有效安全令牌 (Missing Token)"

        with self._lock:
            self._cleanup_expired()
            ticket = self._tokens.get(token)
            if not ticket:
                return False, "安全令牌不存在或已过期，请在网页端重新挂载"

            if ticket["used"]:
                return False, "该一次性令牌已被兑换使用，无法重复调用"

            now = time.time()
            if now > ticket["expires_at"]:
                self._tokens.pop(token, None)
                expired_seconds = int(now - ticket["expires_at"])
                return False, f"安全挂载窗口已超时（有效时间 60 秒，当前已过期 {expired_seconds} 秒）。请回到网页端点击【重新挂载】获取全新安全配置脚本。"

            # 立即标记为已使用，提取凭据，并在内存中彻底抹除密码
            ticket["used"] = True
            cred = {
                "linux_username": ticket["linux_username"],
                "password": ticket["raw_password"],
                "fullname": ticket["fullname"]
            }
            # 焚毁凭据与令牌
            ticket["raw_password"] = None
            self._tokens.pop(token, None)
            return True, cred

    def _cleanup_expired(self):
        """定期清理超时垃圾令牌"""
        now = time.time()
        expired_keys = [k for k, v in self._tokens.items() if now > v["expires_at"] + 120]
        for k in expired_keys:
            self._tokens.pop(k, None)

# 单例实例
token_manager = EphemeralTokenManager(default_ttl=60)
