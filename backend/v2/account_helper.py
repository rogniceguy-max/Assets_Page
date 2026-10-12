"""Per-request privileged account helper for the parallel V2 service.

The helper is started by a systemd socket unit as root. The UNIX socket is
owned by the unprivileged V2 service account; SO_PEERCRED is checked as a
second boundary. It accepts only account creation and rollback requests.
"""

from __future__ import annotations

import hmac
import json
import os
import socket
import stat
import struct
import subprocess
import tempfile
import threading
from contextlib import contextmanager
from pathlib import Path
from typing import Any

try:  # These databases are only needed on the Linux NAS; keeping import optional enables local unit tests.
    import grp
except ImportError:  # pragma: no cover - Windows development host
    grp = None
try:
    import pwd
except ImportError:  # pragma: no cover - Windows development host
    pwd = None

try:  # fcntl is available on the NAS; the fallback keeps local unit tests portable.
    import fcntl
except ImportError:  # pragma: no cover - exercised on Windows only
    fcntl = None

from .identity import normalize_username, valid_department, valid_internal_username


MAX_REQUEST_BYTES = 16 * 1024
MAX_PASSWORD_CHARS = 1024
ASSET_ROOT = Path(os.environ.get("MUYING_V2_PUBLIC_ROOT", "/volume1/Assets"))
MAP_FILE = Path(os.environ.get("MUYING_V2_USERNAME_MAP", "/etc/samba/muying-v2-users.map"))
MANIFEST_FILE = Path(os.environ.get("MUYING_V2_USERNAME_MANIFEST", "/etc/samba/muying-v2-users.json"))
EXPECTED_PEER_USER = os.environ.get("MUYING_V2_SERVICE_USER", "muying-v2")
LOCK_FILE = Path(os.environ.get("MUYING_V2_PROVISIONER_LOCK", "/run/lock/muying-v2-account-helper.lock"))
_portable_lock = threading.Lock()


class HelperFailure(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def _run(args: list[str], *, input_text: str | None = None, timeout: int = 20) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        args,
        input=input_text,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )


def _command_succeeded(args: list[str], *, input_text: str | None = None, timeout: int = 20) -> bool:
    try:
        return _run(args, input_text=input_text, timeout=timeout).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def _load_manifest() -> list[dict[str, str]]:
    if not MANIFEST_FILE.exists():
        return []
    try:
        value = json.loads(MANIFEST_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        raise HelperFailure("MAPPING_STORE_UNAVAILABLE", "账号映射数据无法读取。") from None
    if not isinstance(value, dict) or value.get("version") not in {1, 2} or not isinstance(value.get("entries"), list):
        raise HelperFailure("MAPPING_STORE_INVALID", "账号映射数据格式无效。")
    entries = value["entries"]
    if any(
        not isinstance(row, dict)
        or not isinstance(row.get("system_username"), str)
        or not isinstance(row.get("username"), str)
        or not valid_internal_username(row.get("system_username"))
        or normalize_username(row.get("username")) != row.get("username")
        or row.get("status", "active") not in {"pending", "active", "rolled_back"}
        or (row.get("transaction_id") is not None and not _valid_transaction_id(row.get("transaction_id")))
        for row in entries
    ):
        raise HelperFailure("MAPPING_STORE_INVALID", "账号映射数据格式无效。")
    # Normalize legacy entries so every later write uses the V2 manifest shape.
    return [
        {
            "system_username": row["system_username"],
            "username": row["username"],
            "status": row.get("status", "active"),
            **({"transaction_id": row["transaction_id"]} if row.get("transaction_id") else {}),
        }
        for row in entries
    ]


def _valid_transaction_id(value: object) -> bool:
    return (
        isinstance(value, str)
        and 40 <= len(value) <= 64
        and all(char.isascii() and (char.isalnum() or char in "-_=") for char in value)
    )


@contextmanager
def _transaction_lock():
    """Serialize manifest and Unix/Samba account transactions across helpers."""
    if fcntl is None:
        with _portable_lock:
            yield
        return

    LOCK_FILE.parent.mkdir(parents=True, exist_ok=True)
    with LOCK_FILE.open("a+b") as handle:
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def _write_atomic(path: Path, data: bytes, mode: int = 0o600) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb", closefd=True) as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.chown(temporary, 0, 0)
        os.replace(temporary, path)
        directory_fd = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    except Exception:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
        raise


def _persist_manifest(entries: list[dict[str, str]]) -> None:
    manifest = json.dumps({"version": 2, "entries": entries}, ensure_ascii=False, separators=(",", ":"))
    map_bytes = _render_username_map(entries)
    _write_atomic(MANIFEST_FILE, manifest.encode("utf-8"))
    _write_atomic(MAP_FILE, map_bytes)


def _render_username_map(entries: list[dict[str, str]]) -> bytes:
    rendered = []
    for row in sorted(entries, key=lambda item: item["system_username"]):
        if row.get("status", "active") != "active":
            continue
        username = row["username"]
        # The map format requires quotes for SMB names containing spaces.
        # Backslashes are rejected by normalize_username; escape embedded quotes.
        alias = username.replace('"', '\\"')
        rendered.append(f'{row["system_username"]} = "{alias}"')
    map_bytes = ("\n".join(rendered) + ("\n" if rendered else "")).encode("utf-8")
    if any(len(line.encode("utf-8")) > 1023 for line in rendered):
        raise HelperFailure("USERNAME_TOO_LONG", "用户名超过 NAS 账号映射限制。")
    return map_bytes


def _reload_samba() -> None:
    if not _command_succeeded(["smbcontrol", "all", "reload-config"]):
        raise HelperFailure("SAMBA_RELOAD_FAILED", "NAS 文件服务暂时无法加载账号映射。")


def _ensure_no_collision(username: str, system_username: str, entries: list[dict[str, str]]) -> None:
    key = username.casefold()
    if any(
        row.get("status", "active") != "rolled_back"
        and row["username"].casefold() == key
        for row in entries
    ):
        raise HelperFailure("ACCOUNT_EXISTS", "该姓名已经注册。")
    if valid_internal_username(system_username):
        try:
            pwd.getpwnam(system_username)
            raise HelperFailure("SYSTEM_ACCOUNT_EXISTS", "NAS 内部账号冲突，请重试。")
        except KeyError:
            pass
    try:
        pwd.getpwnam(username)
        raise HelperFailure("ACCOUNT_COLLISION", "该姓名与已有 NAS 账号冲突。")
    except KeyError:
        pass
    try:
        grp.getgrnam(username)
        raise HelperFailure("ACCOUNT_COLLISION", "该姓名与已有 NAS 账号冲突。")
    except KeyError:
        pass

    pdb = _run(["pdbedit", "-L"])
    if pdb.returncode != 0:
        raise HelperFailure("PASSDB_UNAVAILABLE", "NAS Samba 账号库暂时不可用。")
    existing = {line.split(":", 1)[0].casefold() for line in pdb.stdout.splitlines() if ":" in line}
    if username.casefold() in existing or system_username.casefold() in existing:
        raise HelperFailure("ACCOUNT_COLLISION", "该姓名与已有 NAS 账号冲突。")


def _check_prerequisites(group_type: str) -> None:
    required = ["group_assets", "group_film" if group_type == "film" else "group_writing"]
    for name in required:
        try:
            grp.getgrnam(name)
        except KeyError:
            raise HelperFailure("GROUP_UNAVAILABLE", "NAS 部门权限组尚未配置。") from None
    root = ASSET_ROOT.resolve(strict=True)
    personal_root = root / "03_个人空间"
    if personal_root.is_symlink() or not personal_root.is_dir():
        raise HelperFailure("PERSONAL_ROOT_UNAVAILABLE", "NAS 个人空间目录不可用。")


def _create_personal_directory(system_username: str) -> None:
    personal_root = ASSET_ROOT.resolve(strict=True) / "03_个人空间"
    target = personal_root / system_username
    if target.exists() or target.is_symlink():
        raise HelperFailure("PERSONAL_DIRECTORY_EXISTS", "NAS 个人目录冲突，请重试。")
    os.mkdir(target, 0o700)
    account = pwd.getpwnam(system_username)
    os.chown(target, account.pw_uid, account.pw_gid)
    os.chmod(target, 0o700)


def _remove_personal_directory(system_username: str) -> None:
    if not valid_internal_username(system_username):
        return
    personal_root = ASSET_ROOT.resolve(strict=True) / "03_个人空间"
    target = personal_root / system_username
    if target.is_symlink():
        raise HelperFailure("ROLLBACK_UNSAFE", "NAS 个人目录状态异常，未自动删除。")
    try:
        target.rmdir()
    except FileNotFoundError:
        return
    except OSError:
        raise HelperFailure("ROLLBACK_NOT_EMPTY", "NAS 个人目录非空，未自动删除。") from None


def _write_samba_password(system_username: str, password: str) -> None:
    result = _run(
        ["smbpasswd", "-s", "-a", system_username],
        input_text=f"{password}\n{password}\n",
    )
    if result.returncode != 0:
        raise HelperFailure("SAMBA_ACCOUNT_FAILED", "NAS Samba 账号创建失败。")


def _remove_samba_account(system_username: str) -> None:
    before = _run(["pdbedit", "-L"])
    if before.returncode != 0:
        raise HelperFailure("PASSDB_UNAVAILABLE", "NAS Samba 账号库暂时不可用。")
    existing = {line.split(":", 1)[0].casefold() for line in before.stdout.splitlines() if ":" in line}
    if system_username.casefold() not in existing:
        return

    # Confirm removal even if smbpasswd reports a non-zero status. Never delete
    # the Unix principal while its Samba passdb entry might still exist.
    _run(["smbpasswd", "-x", system_username])
    after = _run(["pdbedit", "-L"])
    if after.returncode != 0:
        raise HelperFailure("PASSDB_UNAVAILABLE", "NAS Samba 账号库暂时不可用。")
    remaining = {line.split(":", 1)[0].casefold() for line in after.stdout.splitlines() if ":" in line}
    if system_username.casefold() in remaining:
        raise HelperFailure("SAMBA_ACCOUNT_REMOVE_FAILED", "NAS Samba 账号清理失败。")


def _rollback_uncommitted(system_username: str, username: str, transaction_id: str) -> None:
    entries = _load_manifest()
    match = next(
        (
            row for row in entries
            if row["system_username"] == system_username
            and row["username"] == username
            and row.get("transaction_id") is not None
            and hmac.compare_digest(row["transaction_id"], transaction_id)
        ),
        None,
    )
    if match is None:
        raise HelperFailure("ROLLBACK_NOT_AUTHORIZED", "账号回滚凭据无效。")
    if match.get("status") == "rolled_back":
        return

    _remove_personal_directory(system_username)
    _remove_samba_account(system_username)
    if valid_internal_username(system_username):
        try:
            pwd.getpwnam(system_username)
        except KeyError:
            pass
        else:
            _run(["userdel", system_username])
            try:
                pwd.getpwnam(system_username)
            except KeyError:
                pass
            else:
                raise HelperFailure("SYSTEM_ACCOUNT_REMOVE_FAILED", "NAS 内部账号清理失败。")

    match["status"] = "rolled_back"
    _persist_manifest(entries)
    _reload_samba()


def create_account(request: dict[str, Any]) -> dict[str, Any]:
    username = normalize_username(request.get("username"))
    system_username = request.get("system_username")
    group_type = request.get("group_type")
    password = request.get("password")
    transaction_id = request.get("transaction_id")
    if username is None or username != request.get("username"):
        raise HelperFailure("INVALID_USERNAME", "姓名格式无效。")
    if not valid_internal_username(system_username):
        raise HelperFailure("INVALID_SYSTEM_ACCOUNT", "NAS 内部账号格式无效。")
    if not valid_department(group_type):
        raise HelperFailure("INVALID_DEPARTMENT", "部门信息无效。")
    if (
        not isinstance(password, str)
        or len(password) < 8
        or len(password) > MAX_PASSWORD_CHARS
        or "\n" in password
        or "\r" in password
    ):
        raise HelperFailure("INVALID_PASSWORD", "密码需为 8 至 1024 位且不能包含换行符。")
    if not _valid_transaction_id(transaction_id):
        raise HelperFailure("INVALID_TRANSACTION", "账号请求凭据无效。")
    if grp is None or pwd is None:
        raise HelperFailure("UNSUPPORTED_PLATFORM", "账号开通仅支持 NAS Linux 环境。")

    entries = _load_manifest()
    _ensure_no_collision(username, system_username, entries)
    _check_prerequisites(group_type)
    department_group = "group_film" if group_type == "film" else "group_writing"

    entry = {
        "system_username": system_username,
        "username": username,
        "status": "pending",
        "transaction_id": transaction_id,
    }
    entries.append(entry)
    # Record the transaction before touching Unix/Samba state. The root-owned
    # random transaction ID makes cleanup exact and safe to retry.
    try:
        _persist_manifest(entries)
    except Exception:
        try:
            _persist_manifest([row for row in entries if row is not entry])
        except Exception:
            raise HelperFailure(
                "ROLLBACK_FAILED", "NAS 账号映射状态暂时无法安全保存，需要管理员检查。"
            ) from None
        raise HelperFailure("MAPPING_STORE_UNAVAILABLE", "NAS 账号映射数据暂时无法保存。") from None

    try:
        if not _command_succeeded(
            [
                "useradd", "--system", "--no-create-home", "--no-user-group",
                "--gid", "group_assets", "--groups", department_group,
                "--shell", "/usr/sbin/nologin", system_username,
            ]
        ):
            raise HelperFailure("SYSTEM_ACCOUNT_FAILED", "NAS 内部账号创建失败。")
        _write_samba_password(system_username, password)
        _create_personal_directory(system_username)
        entry["status"] = "active"
        _persist_manifest(entries)
        _reload_samba()
    except Exception as original_error:
        try:
            _rollback_uncommitted(system_username, username, transaction_id)
        except Exception:
            raise HelperFailure(
                "ROLLBACK_FAILED", "NAS 账号创建未完成，系统需要修复该账号状态。"
            ) from None
        if isinstance(original_error, HelperFailure):
            raise original_error
        raise HelperFailure("ACCOUNT_PROVISIONING_FAILED", "NAS 账号创建失败。") from None

    return {"ok": True}


def rollback_account(request: dict[str, Any]) -> dict[str, Any]:
    username = normalize_username(request.get("username"))
    system_username = request.get("system_username")
    transaction_id = request.get("transaction_id")
    if (
        username is None
        or username != request.get("username")
        or not valid_internal_username(system_username)
        or not _valid_transaction_id(transaction_id)
    ):
        raise HelperFailure("INVALID_ROLLBACK", "回滚请求无效。")
    _rollback_uncommitted(system_username, username, transaction_id)
    return {"ok": True}


def get_account_status(request: dict[str, Any]) -> dict[str, Any]:
    username = normalize_username(request.get("username"))
    system_username = request.get("system_username")
    group_type = request.get("group_type")
    transaction_id = request.get("transaction_id")
    if (
        username is None
        or username != request.get("username")
        or not valid_internal_username(system_username)
        or not valid_department(group_type)
        or not _valid_transaction_id(transaction_id)
    ):
        raise HelperFailure("INVALID_STATUS_QUERY", "账号恢复请求无效。")

    entries = _load_manifest()
    for row in entries:
        if (
            row["system_username"] == system_username
            and row["username"] == username
            and row.get("transaction_id") is not None
            and hmac.compare_digest(row["transaction_id"], transaction_id)
        ):
            status = row.get("status", "active")
            if status != "active":
                return {"ok": True, "status": status}
            if not _active_account_is_complete(system_username, group_type, entries):
                # The root-side transaction may have stopped between manifest,
                # map, Unix/Samba account, and private-directory updates. Let the
                # API request its existing transaction-scoped rollback.
                return {"ok": True, "status": "pending"}
            # The helper may have stopped after publishing the map but before
            # reloading smbd. Reloading configuration is idempotent and does not
            # disconnect existing SMB sessions.
            _reload_samba()
            return {"ok": True, "status": "active"}

    if pwd is None:
        raise HelperFailure("UNSUPPORTED_PLATFORM", "账号状态检查仅支持 NAS Linux 环境。")
    return {"ok": True, "status": "unknown" if _account_artifacts_exist(system_username) else "absent"}


def _active_account_is_complete(
    system_username: str, group_type: str, entries: list[dict[str, str]]
) -> bool:
    if pwd is None or grp is None or not hasattr(os, "getgrouplist"):
        raise HelperFailure("UNSUPPORTED_PLATFORM", "账号状态检查仅支持 NAS Linux 环境。")

    try:
        account = pwd.getpwnam(system_username)
    except KeyError:
        return False

    try:
        assets_gid = grp.getgrnam("group_assets").gr_gid
        department = "group_film" if group_type == "film" else "group_writing"
        department_gid = grp.getgrnam(department).gr_gid
        memberships = set(os.getgrouplist(system_username, account.pw_gid))
    except KeyError:
        raise HelperFailure("GROUP_UNAVAILABLE", "NAS 部门权限组尚未配置。") from None
    except OSError:
        raise HelperFailure("ACCOUNT_STATUS_UNAVAILABLE", "NAS 内部账号状态暂时无法核实。") from None
    if account.pw_gid != assets_gid or department_gid not in memberships:
        return False

    passdb = _run(["pdbedit", "-L"])
    if passdb.returncode != 0:
        raise HelperFailure("PASSDB_UNAVAILABLE", "NAS Samba 账号库暂时不可用。")
    passdb_names = {line.split(":", 1)[0].casefold() for line in passdb.stdout.splitlines() if ":" in line}
    if system_username.casefold() not in passdb_names:
        return False

    try:
        personal_root = ASSET_ROOT.resolve(strict=True) / "03_个人空间"
        target = personal_root / system_username
        if target.is_symlink():
            return False
        info = target.stat()
    except FileNotFoundError:
        return False
    except OSError:
        raise HelperFailure("ACCOUNT_STATUS_UNAVAILABLE", "NAS 个人目录状态暂时无法核实。") from None
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != account.pw_uid or stat.S_IMODE(info.st_mode) != 0o700:
        return False

    try:
        current_map = MAP_FILE.read_bytes()
    except FileNotFoundError:
        return False
    except OSError:
        raise HelperFailure("MAPPING_STORE_UNAVAILABLE", "账号映射数据无法读取。") from None
    return current_map == _render_username_map(entries)


def _account_artifacts_exist(system_username: str) -> bool:
    if pwd is None:
        raise HelperFailure("UNSUPPORTED_PLATFORM", "账号状态检查仅支持 NAS Linux 环境。")
    try:
        pwd.getpwnam(system_username)
        return True
    except KeyError:
        pass

    passdb = _run(["pdbedit", "-L"])
    if passdb.returncode != 0:
        raise HelperFailure("PASSDB_UNAVAILABLE", "NAS Samba 账号库暂时不可用。")
    passdb_names = {line.split(":", 1)[0].casefold() for line in passdb.stdout.splitlines() if ":" in line}
    if system_username.casefold() in passdb_names:
        return True

    try:
        personal_root = ASSET_ROOT.resolve(strict=True) / "03_个人空间"
    except OSError:
        raise HelperFailure("ACCOUNT_STATUS_UNAVAILABLE", "NAS 账号残留状态暂时无法核实。") from None
    personal_dir = personal_root / system_username
    if personal_dir.exists() or personal_dir.is_symlink():
        return True
    try:
        map_data = MAP_FILE.read_bytes()
    except FileNotFoundError:
        return False
    except OSError:
        raise HelperFailure("ACCOUNT_STATUS_UNAVAILABLE", "NAS 账号残留状态暂时无法核实。") from None
    return system_username.encode("ascii") in map_data


def _read_line(fd: int) -> bytes:
    result = bytearray()
    while len(result) <= MAX_REQUEST_BYTES:
        chunk = os.read(fd, 4096)
        if not chunk:
            break
        newline = chunk.find(b"\n")
        if newline >= 0:
            result.extend(chunk[:newline])
            break
        result.extend(chunk)
    if len(result) > MAX_REQUEST_BYTES:
        raise HelperFailure("REQUEST_TOO_LARGE", "账号请求过长。")
    return bytes(result)


def _verify_peer(fd: int) -> None:
    if not hasattr(socket, "SO_PEERCRED"):
        raise HelperFailure("SOCKET_UNSUPPORTED", "NAS 本机账号通道不可用。")
    peer = socket.socket(family=socket.AF_UNIX, type=socket.SOCK_STREAM, fileno=os.dup(fd))
    try:
        _pid, uid, _gid = struct.unpack("3i", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")))
    finally:
        peer.close()
    try:
        expected_uid = pwd.getpwnam(EXPECTED_PEER_USER).pw_uid
    except KeyError:
        raise HelperFailure("SERVICE_ACCOUNT_MISSING", "V2 服务账号尚未配置。") from None
    if uid != expected_uid:
        raise HelperFailure("UNAUTHORIZED_PEER", "本机账号通道拒绝访问。")


def handle_request(raw: bytes) -> dict[str, Any]:
    try:
        request = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return {"ok": False, "code": "INVALID_REQUEST", "message": "账号请求格式无效。"}
    if not isinstance(request, dict):
        return {"ok": False, "code": "INVALID_REQUEST", "message": "账号请求格式无效。"}
    try:
        operation = request.get("operation")
        with _transaction_lock():
            if operation == "create_account":
                return create_account(request)
            if operation == "rollback_account":
                return rollback_account(request)
            if operation == "get_account_status":
                return get_account_status(request)
            return {"ok": False, "code": "UNKNOWN_OPERATION", "message": "账号操作不受支持。"}
    except HelperFailure as error:
        return {"ok": False, "code": error.code, "message": str(error)}
    except Exception:
        # Do not expose subprocess diagnostics, account identifiers, or passwords.
        return {"ok": False, "code": "INTERNAL_ERROR", "message": "NAS 账号操作失败。"}


def main() -> int:
    input_fd = 0
    output_fd = 1
    try:
        peer_socket = socket.socket(
            family=socket.AF_UNIX,
            type=socket.SOCK_STREAM,
            fileno=os.dup(input_fd),
        )
        try:
            _verify_peer(peer_socket.fileno())
        finally:
            peer_socket.close()
        raw = _read_line(input_fd)
        response = handle_request(raw)
    except HelperFailure as error:
        response = {"ok": False, "code": error.code, "message": str(error)}
    except Exception:
        response = {"ok": False, "code": "INTERNAL_ERROR", "message": "NAS 账号操作失败。"}
    encoded = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
    view = memoryview(encoded)
    while view:
        view = view[os.write(output_fd, view):]
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
