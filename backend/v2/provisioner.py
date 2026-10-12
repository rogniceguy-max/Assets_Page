"""Client for the small root-owned account helper over a local Unix socket."""

from __future__ import annotations

import json
import socket
from pathlib import Path
from typing import Any


MAX_HELPER_MESSAGE_BYTES = 16 * 1024


class ProvisionerError(RuntimeError):
    def __init__(
        self,
        code: str,
        message: str,
        *,
        unavailable: bool = False,
        may_have_run: bool = False,
    ):
        super().__init__(message)
        self.code = code
        self.unavailable = unavailable
        self.may_have_run = may_have_run


class AccountProvisioner:
    """Use the per-request root helper; this service never invokes shell commands."""

    def __init__(self, socket_path: Path | None, timeout_seconds: float = 30):
        self.socket_path = socket_path
        self.timeout_seconds = timeout_seconds

    def create_account(
        self,
        *,
        username: str,
        system_username: str,
        password: str,
        group_type: str,
        transaction_id: str,
    ) -> None:
        self._request(
            {
                "operation": "create_account",
                "username": username,
                "system_username": system_username,
                "password": password,
                "group_type": group_type,
                "transaction_id": transaction_id,
            }
        )

    def rollback_account(
        self, *, username: str, system_username: str, transaction_id: str
    ) -> None:
        self._request(
            {
                "operation": "rollback_account",
                "username": username,
                "system_username": system_username,
                "transaction_id": transaction_id,
            }
        )

    def get_account_status(
        self, *, username: str, system_username: str, group_type: str, transaction_id: str
    ) -> str:
        result = self._request(
            {
                "operation": "get_account_status",
                "username": username,
                "system_username": system_username,
                "group_type": group_type,
                "transaction_id": transaction_id,
            }
        )
        status = result.get("status") if isinstance(result, dict) else None
        if status not in {"pending", "active", "rolled_back", "absent", "unknown"}:
            raise ProvisionerError("BAD_HELPER_RESPONSE", "账号恢复状态无效。", may_have_run=False)
        return status

    def _request(self, payload: dict[str, Any]) -> dict[str, Any]:
        if self.socket_path is None:
            raise ProvisionerError(
                "PROVISIONER_UNAVAILABLE",
                "NAS 账号开通服务尚未配置。",
                unavailable=True,
            )

        encoded = bytearray(
            json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            + b"\n"
        )
        if len(encoded) > MAX_HELPER_MESSAGE_BYTES:
            raise ProvisionerError("REQUEST_TOO_LARGE", "账号请求过长。")

        response = bytearray()
        request_sent = False
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                channel.settimeout(self.timeout_seconds)
                channel.connect(str(self.socket_path))
                channel.sendall(encoded)
                request_sent = True
                while len(response) <= MAX_HELPER_MESSAGE_BYTES:
                    chunk = channel.recv(4096)
                    if not chunk:
                        break
                    newline = chunk.find(b"\n")
                    if newline >= 0:
                        response.extend(chunk[:newline])
                        break
                    response.extend(chunk)
        except (OSError, TimeoutError):
            raise ProvisionerError(
                "PROVISIONER_UNAVAILABLE",
                "NAS 账号开通服务暂时不可用。",
                unavailable=True,
                may_have_run=request_sent,
            ) from None
        finally:
            # `encoded` contains the registration password; clear the request buffer.
            encoded[:] = b"\x00" * len(encoded)

        if len(response) > MAX_HELPER_MESSAGE_BYTES:
            raise ProvisionerError(
                "BAD_HELPER_RESPONSE", "NAS 账号开通服务返回无效结果。", may_have_run=True
            )
        try:
            result = json.loads(response.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ProvisionerError(
                "BAD_HELPER_RESPONSE", "NAS 账号开通服务返回无效结果。", may_have_run=True
            ) from None

        if not isinstance(result, dict) or result.get("ok") is not True:
            code = result.get("code") if isinstance(result, dict) else None
            message = result.get("message") if isinstance(result, dict) else None
            raise ProvisionerError(
                code if isinstance(code, str) else "PROVISIONING_FAILED",
                message if isinstance(message, str) else "NAS 账号开通失败。",
                # The helper only returns this code when its own rollback failed;
                # other rejected requests are side-effect free or fully cleaned.
                # A transport failure after send is handled above as uncertain.
                may_have_run=code in {"ROLLBACK_FAILED", "INTERNAL_ERROR"},
            )
        return result
