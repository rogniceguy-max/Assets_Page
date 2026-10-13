"""Root-side, socket-activated helper for the login-free visitor read channel.

Started by ``muying-v2-visitor@.service`` through ``muying-v2-visitor.socket``
(Accept=yes), exactly like the account helper: the connected socket is handed over as
stdin/stdout, SO_PEERCRED is checked against the V2 service account, and one request
is served per process.

Every request drops privileges to the dedicated visitor identity before touching the
filesystem, so the UGOS ACL — not this code — is what limits what can be read. After
the drop the process can never regain privilege, and the only operations implemented
are read-only: there is no write, rename, delete or permission path to reach.

Protocol (one request per process):

    -> {"space": "film", "action": "list", "relativePath": "影视项目"}
    <- {"ok": true, "kind": "json", "length": N}\n  followed by N bytes of JSON
    -> {"space": "film", "action": "read", "relativePath": "a/b.mp4",
        "offset": 0, "length": 4194304}
    <- {"ok": true, "kind": "bytes", "length": N}\n  followed by N raw bytes
"""
from __future__ import annotations

import json
import grp
import os
import pwd
import socket
import stat
import struct
import sys
from pathlib import Path
from typing import Any

MAX_REQUEST_BYTES = 8 * 1024
MAX_ENTRIES = 5000
MAX_READ_BYTES = 4 * 1024 * 1024

EXPECTED_PEER_USER = os.environ.get("MUYING_V2_HELPER_PEER_USER", "muying-v2")
VISITOR_USER = os.environ.get("MUYING_V2_VISITOR_USER", "沐影参观")
VISITOR_GROUP = os.environ.get("MUYING_V2_VISITOR_GROUP", "my_visitor")

# Space roots are resolved here, never supplied by the caller: the client sends a space
# id and a relative path, and an unknown id is rejected outright.
DEFAULT_SPACES = {
    "film": "/volume1/Assets/01_影视资源库",
    "writing": "/volume1/Assets/02_文学创作库",
    "public": "/volume1/Assets/00_公共素材",
}


def _configured_spaces() -> dict[str, Path]:
    raw = os.environ.get("MUYING_V2_VISITOR_SPACES")
    spaces = DEFAULT_SPACES
    if raw:
        try:
            parsed = json.loads(raw)
            if isinstance(parsed, dict) and parsed:
                spaces = {str(k): str(v) for k, v in parsed.items()}
        except json.JSONDecodeError:
            pass
    roots: dict[str, Path] = {}
    for key, value in spaces.items():
        try:
            resolved = Path(value).resolve(strict=True)
        except OSError:
            continue
        if resolved.is_dir():
            roots[key] = resolved
    return roots


class RequestRejected(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def _drop_privileges() -> None:
    """Become the visitor identity. This cannot be undone by this process.

    The dedicated visitor *group* is used, not the account's primary group. UGOS gives
    new accounts the shared ``users`` group as their primary group, so dropping to
    ``pw_gid`` would run under a group shared with employees and administrators — both
    an over-grant and, in practice, a denial, because the read-only ACLs are granted to
    the dedicated group rather than to ``users``.
    """
    try:
        account = pwd.getpwnam(VISITOR_USER)
    except KeyError:
        raise RequestRejected("VISITOR_ACCOUNT_MISSING", "参观身份尚未配置。") from None
    try:
        group = grp.getgrnam(VISITOR_GROUP)
    except KeyError:
        raise RequestRejected("VISITOR_GROUP_MISSING", "参观专用组尚未配置。") from None

    try:
        os.setgroups([group.gr_gid])
        os.setgid(group.gr_gid)
        os.setuid(account.pw_uid)
    except OSError:
        raise RequestRejected("PRIVILEGE_DROP_FAILED", "参观通道无法安全降权。") from None

    # Verify the drop actually took effect before any filesystem access.
    if os.getuid() != account.pw_uid or os.geteuid() != account.pw_uid:
        raise RequestRejected("PRIVILEGE_DROP_FAILED", "参观通道无法安全降权。")
    if os.getgid() != group.gr_gid or os.getgroups() != [group.gr_gid]:
        raise RequestRejected("PRIVILEGE_DROP_FAILED", "参观通道无法安全降权。")


def _resolve(space_root: Path, relative_path: str) -> Path:
    """Resolve a client-supplied relative path inside a space root, or reject it.

    Rejects absolute paths, traversal, control characters and empty segments, then
    walks the path component by component refusing symlinks, and finally confirms the
    result really sits under the space root. The ACL still applies afterwards; this
    only prevents the request itself from escaping the space.
    """
    if not isinstance(relative_path, str):
        raise RequestRejected("INVALID_PATH", "参观路径无效。")
    candidate = relative_path.strip("/")
    if "\x00" in candidate or any(ord(character) < 32 for character in candidate):
        raise RequestRejected("INVALID_PATH", "参观路径无效。")
    if candidate.startswith("/") or ":" in candidate:
        raise RequestRejected("INVALID_PATH", "参观路径无效。")
    segments = [segment for segment in candidate.replace("\\", "/").split("/") if segment not in ("", ".")]
    if any(segment == ".." for segment in segments):
        raise RequestRejected("INVALID_PATH", "参观路径无效。")

    current = space_root
    for segment in segments:
        current = current / segment
        try:
            info = current.lstat()
        except OSError:
            raise RequestRejected("NOT_FOUND", "参观目标不存在。") from None
        if stat.S_ISLNK(info.st_mode):
            raise RequestRejected("INVALID_PATH", "参观路径无效。")

    try:
        resolved = current.resolve(strict=True)
    except OSError:
        raise RequestRejected("NOT_FOUND", "参观目标不存在。") from None
    if resolved != space_root and space_root not in resolved.parents:
        raise RequestRejected("INVALID_PATH", "参观路径无效。")
    return resolved


def _list(space_root: Path, target: Path) -> dict[str, Any]:
    entries: list[dict[str, Any]] = []
    try:
        with os.scandir(target) as iterator:
            for entry in iterator:
                if len(entries) >= MAX_ENTRIES:
                    break
                try:
                    info = entry.stat(follow_symlinks=False)
                except OSError:
                    continue
                if stat.S_ISLNK(info.st_mode):
                    continue
                entries.append(
                    {
                        "name": entry.name,
                        "isDirectory": stat.S_ISDIR(info.st_mode),
                        "size": info.st_size,
                        "modifiedUtc": int(info.st_mtime),
                    }
                )
    except PermissionError:
        raise RequestRejected("ACCESS_DENIED", "参观身份无权访问该目录。") from None
    except OSError:
        raise RequestRejected("LIST_FAILED", "无法列出该目录。") from None

    entries.sort(key=lambda item: (not item["isDirectory"], item["name"]))
    return {"entries": entries, "truncated": len(entries) >= MAX_ENTRIES}


def _read(target: Path, offset: int, length: int) -> tuple[bytes, int]:
    """Returns the requested bytes plus the total file size, for Content-Range."""
    if not isinstance(offset, int) or offset < 0:
        raise RequestRejected("INVALID_RANGE", "读取范围无效。")
    if not isinstance(length, int) or length <= 0 or length > MAX_READ_BYTES:
        raise RequestRejected("INVALID_RANGE", "读取范围无效。")
    try:
        with open(target, "rb", buffering=0) as handle:
            size = os.fstat(handle.fileno()).st_size
            handle.seek(offset)
            return handle.read(length), size
    except PermissionError:
        raise RequestRejected("ACCESS_DENIED", "参观身份无权读取该文件。") from None
    except IsADirectoryError:
        raise RequestRejected("NOT_A_FILE", "参观目标不是文件。") from None
    except OSError:
        raise RequestRejected("READ_FAILED", "无法读取该文件。") from None


def handle_request(raw: bytes) -> tuple[dict[str, Any], bytes]:
    try:
        request = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise RequestRejected("INVALID_REQUEST", "参观请求格式无效。")
    if not isinstance(request, dict):
        raise RequestRejected("INVALID_REQUEST", "参观请求格式无效。")

    spaces = _configured_spaces()
    space_id = request.get("space")
    if not isinstance(space_id, str) or space_id not in spaces:
        raise RequestRejected("UNKNOWN_SPACE", "参观空间不存在。")
    action = request.get("action")
    if action not in {"list", "read"}:
        raise RequestRejected("UNKNOWN_ACTION", "参观操作不受支持。")

    target = _resolve(spaces[space_id], request.get("relativePath", ""))

    # Privileges are dropped only after the request is validated, and before any
    # filesystem access: from here on the UGOS ACL decides what is visible.
    _drop_privileges()

    if action == "list":
        payload = json.dumps(_list(spaces[space_id], target), ensure_ascii=False).encode("utf-8")
        return {"ok": True, "kind": "json", "length": len(payload)}, payload

    payload, size = _read(target, request.get("offset", 0), request.get("length", 0))
    return {"ok": True, "kind": "bytes", "length": len(payload), "size": size}, payload


def _verify_peer(fd: int) -> None:
    if not hasattr(socket, "SO_PEERCRED"):
        raise RequestRejected("SOCKET_UNSUPPORTED", "NAS 本机参观通道不可用。")
    peer = socket.socket(family=socket.AF_UNIX, type=socket.SOCK_STREAM, fileno=os.dup(fd))
    try:
        _pid, uid, _gid = struct.unpack(
            "3i", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")))
    finally:
        peer.close()
    try:
        expected_uid = pwd.getpwnam(EXPECTED_PEER_USER).pw_uid
    except KeyError:
        raise RequestRejected("SERVICE_ACCOUNT_MISSING", "V2 服务账号尚未配置。") from None
    if uid != expected_uid:
        raise RequestRejected("UNAUTHORIZED_PEER", "本机参观通道拒绝访问。")


def _read_line(fd: int) -> bytes:
    result = bytearray()
    while True:
        chunk = os.read(fd, 4096)
        if not chunk:
            break
        newline = chunk.find(b"\n")
        if newline >= 0:
            result.extend(chunk[:newline])
            break
        result.extend(chunk)
        if len(result) > MAX_REQUEST_BYTES:
            raise RequestRejected("REQUEST_TOO_LARGE", "参观请求过长。")
    return bytes(result)


def _write_all(fd: int, data: bytes) -> None:
    view = memoryview(data)
    while view:
        view = view[os.write(fd, view):]


def main() -> int:
    try:
        peer_socket = socket.socket(family=socket.AF_UNIX, type=socket.SOCK_STREAM, fileno=os.dup(0))
        try:
            _verify_peer(peer_socket.fileno())
        finally:
            peer_socket.close()
        header, payload = handle_request(_read_line(0))
    except RequestRejected as error:
        header, payload = {"ok": False, "code": error.code, "message": str(error)}, b""
    except Exception:
        # Never expose paths, identifiers or diagnostics.
        header, payload = {"ok": False, "code": "INTERNAL_ERROR", "message": "参观通道处理失败。"}, b""

    _write_all(1, json.dumps(header, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
    if payload:
        _write_all(1, payload)
    return 0


if __name__ == "__main__":
    sys.exit(main())
