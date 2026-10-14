"""Privileged project provisioner for the V2 service.

Runs as root, socket-activated, one request per connection, stdin/stdout framed as one
JSON line each way. Same shape as :mod:`backend.v2.account_helper`, and for the same
reason: creating a project means writing to ``/volume1``, and the V2 service unit runs with
``ProtectSystem=strict`` so its writable set is ``/var/lib/muying-v2`` and nothing else.

The alternative was to add ``ReadWritePaths=/volume1/Assets/Project`` to that unit, which
would give the whole service — including the workbook reader and the installer download
route — write access to a shared directory. The socket narrows it to one operation, and
this process holds no state and no session of its own.

Two things are easy to get wrong here and are handled explicitly below:

* **A locally created directory gets no UGOS ACE.** Inheritance happens when Samba creates
  a file, not when ``mkdir`` does, so a project folder made here has only its POSIX mode.
  That is enough — it was measured on the real share, employees read and write and the
  visitor is denied — but it is why the owner, group and mode are set here rather than left
  to the umask.
* **Templates must be recognised by content.** :mod:`backend.v2.project_workbooks.projector`
  decides what a workbook is by reading its header row, never its filename. The headers
  below are the ones that make it answer ``scene_beats`` and ``asset_review``; the test that
  asserts this feeds each generated template back through the projector, so the two cannot
  drift apart silently.
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import struct
import sys
from pathlib import Path
from typing import Any

try:  # Only on the Linux NAS; keeping these optional is what lets the suite run elsewhere.
    import grp
    import pwd
except ImportError:  # pragma: no cover - Windows
    grp = None
    pwd = None

MAX_REQUEST_BYTES = 16 * 1024
MAX_NAME_CHARS = 128

# The account the helper must be called by. Anything else on the box that can open the
# socket is refused; the socket itself is 0600 and owned by this account.
EXPECTED_PEER_USER = os.environ.get("MUYING_V2_HELPER_PEER_USER", "muying-v2")

# The project root, and who the created project belongs to. The service account owns it
# (it is the one that reads the workbooks back) and every employee is in the group.
PROJECT_ROOT = Path(os.environ.get("MUYING_V2_PROJECT_DOCS_ROOT", "/volume1/Assets/Project"))
OWNER_USER = os.environ.get("MUYING_V2_PROJECT_OWNER_USER", "muying-v2")
OWNER_GROUP = os.environ.get("MUYING_V2_PROJECT_OWNER_GROUP", "group_assets")

DIRECTORY_MODE = 0o770
FILE_MODE = 0o660

# The two workbooks every project starts with. Sheet names are what a user sees in the
# workbook reader; the header rows are what the projector classifies on. A beat sheet scores
# by scene aliases and carries no asset field; an asset sheet carries 类别 and 名称, which is
# what tips the projector from one to the other.
BEAT_SHEET_NAME = "节拍表"
ASSET_SHEET_NAME = "资产表"

BEAT_HEADERS = ["场景号", "节拍号", "故事内容", "标注等级", "拆分规划",
                "成员批注一", "成员批注二", "成员批注三", "成员批注四", "成员批注五", "成员批注六"]
ASSET_HEADERS = ["类别", "名称", "出场", "形态"]

# Names Windows refuses, with or without an extension. Checked case-insensitively.
RESERVED_NAMES = {
    "CON", "PRN", "AUX", "NUL",
    "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
}
INVALID_NAME_CHARS = set('\\/:*?"<>|')


class HelperFailure(Exception):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _validate_name(raw: object) -> str:
    """One path segment, or a refusal that says which rule it broke.

    The name arrives from a user through the API. It becomes a directory name under the
    project root, so this is the one place a user-supplied string turns into a path — and
    the rules are the ones the client applies to a rename, because a project that could be
    created but not renamed (or the reverse) would be a bug either way.

    One deliberate difference: surrounding whitespace is trimmed before the rules apply, so
    a name pasted in with a trailing space is accepted as the name without it. The client's
    rename target is refused instead, and that asymmetry is the right way round — a rename
    to "孟婆 " means the user is trying to name something "孟婆 " at a point where the space
    is invisible and Windows would quietly drop it, while a create field is where someone
    pastes. Everything after the trim is judged identically on both sides.
    """
    if not isinstance(raw, str):
        raise HelperFailure("INVALID_NAME", "项目名称无效。")
    name = raw.strip()
    if not name:
        raise HelperFailure("INVALID_NAME", "项目名称不能为空。")
    if len(name) > MAX_NAME_CHARS:
        raise HelperFailure("INVALID_NAME", f"项目名称最长 {MAX_NAME_CHARS} 个字符。")
    if name in (".", ".."):
        raise HelperFailure("INVALID_NAME", "项目名称无效。")
    if any(character < " " or character in INVALID_NAME_CHARS for character in name):
        raise HelperFailure("INVALID_NAME", '项目名称不能包含 \\ / : * ? " < > | 或控制字符。')
    if name[-1] in (".", " "):
        raise HelperFailure("INVALID_NAME", "项目名称不能以点或空格结尾。")
    stem = name.rsplit(".", 1)[0] if "." in name[1:] else name
    if stem.upper() in RESERVED_NAMES:
        raise HelperFailure("INVALID_NAME", "该名称是系统保留名，请换一个。")
    return name


def _template_bytes(headers: list[str], sheet_name: str) -> bytes:
    """A one-sheet .xlsx holding just the header row, as bytes.

    openpyxl comes from the pinned wheel directory the service already uses. Imported here
    rather than at module scope so that a host missing the library reports it as a failed
    project creation instead of refusing to start the helper at all.
    """
    try:
        from io import BytesIO
        from openpyxl import Workbook
    except ImportError:
        raise HelperFailure(
            "TEMPLATE_UNAVAILABLE", "NAS 上缺少生成表格模板所需的组件，请联系管理员。") from None

    workbook = Workbook()
    sheet = workbook.active
    sheet.title = sheet_name
    sheet.append(headers)
    buffer = BytesIO()
    workbook.save(buffer)
    workbook.close()
    return buffer.getvalue()


def _ownership() -> tuple[int, int]:
    """The uid and gid a created project should belong to.

    Looked up by name every time rather than cached: this is one request per process, and a
    cached id would be a stale one the day an account is recreated.
    """
    try:
        return pwd.getpwnam(OWNER_USER).pw_uid, grp.getgrnam(OWNER_GROUP).gr_gid
    except (KeyError, AttributeError):
        raise HelperFailure("OWNERSHIP_UNAVAILABLE", "目标属主或属组不存在，请联系管理员。") from None


def _apply_ownership(path: Path, uid: int, gid: int) -> None:
    """Hands a created path to its owner.

    ``os.chown`` exists only on Unix, so this is a no-op elsewhere — which happens only when
    the suite is running somewhere other than the NAS. On the host that matters, the call
    always runs: a project directory left owned by root would be unreadable to the service
    account that has to read the workbooks back out of it.
    """
    if not hasattr(os, "chown"):
        return
    os.chown(path, uid, gid)


def _apply_mode(path: Path, mode: int) -> None:
    """Sets the mode explicitly, after the path exists.

    Not the mode passed to ``mkdir`` or ``os.open``: on this share those are not what ends
    up on disk. A directory asked for as 0770 and a file asked for as 0660 both came back as
    0770 — the file executable — which is how this was found: it was measured on the NAS
    rather than assumed from the call. The same was true of ACL inheritance, and the lesson
    is the same one: on this file system the creation call is a request, not a result.
    """
    os.chmod(path, mode)


def _write_atomic(path: Path, data: bytes, mode: int) -> None:
    temporary = path.with_name(path.name + ".tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        _apply_mode(path, mode)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


def create_project(request: dict[str, Any]) -> dict[str, Any]:
    """Creates one project directory and its two default workbooks, or neither.

    The atomicity the contract asks for (PROJECT_SPACE_V1.md: "失败时不得留下只有一张表的
    半成品项目") is achieved by building the whole thing under a temporary name and renaming
    it into place once every part exists. A rename within one directory is atomic, so a
    project that appears at all is a project that is complete. The alternative — create then
    clean up on failure — leaves a window where a crash strands exactly the half-built
    project the contract forbids.
    """
    name = _validate_name(request.get("name"))

    root = PROJECT_ROOT
    if not root.is_dir():
        raise HelperFailure("ROOT_UNAVAILABLE", "项目根目录不可用，请联系管理员。")

    final = root / name
    # Refuse anything already there. os.path.lexists rather than exists: a dangling symlink
    # sitting on the name would otherwise pass, and the rename below would then follow it.
    if os.path.lexists(final):
        raise HelperFailure("NAME_TAKEN", "已存在同名项目，请换一个名称。")

    owner_uid, owner_gid = _ownership()

    staging = root / f".creating-{name}"
    if os.path.lexists(staging):
        raise HelperFailure("NAME_TAKEN", "该项目正在创建中，请稍候重试。")

    try:
        os.mkdir(staging, DIRECTORY_MODE)
        _apply_mode(staging, DIRECTORY_MODE)
        _apply_ownership(staging, owner_uid, owner_gid)

        _write_atomic(staging / f"{BEAT_SHEET_NAME}.xlsx",
                      _template_bytes(BEAT_HEADERS, BEAT_SHEET_NAME), FILE_MODE)
        _write_atomic(staging / f"{ASSET_SHEET_NAME}.xlsx",
                      _template_bytes(ASSET_HEADERS, ASSET_SHEET_NAME), FILE_MODE)
        for created in staging.iterdir():
            _apply_ownership(created, owner_uid, owner_gid)

        # Everything exists; make it visible in one step.
        os.rename(staging, final)
    except BaseException:
        shutil.rmtree(staging, ignore_errors=True)
        raise

    return {"ok": True, "name": name, "path": str(final)}


def _validate_sheet_name(raw: object) -> str:
    if not isinstance(raw, str):
        raise HelperFailure("INVALID_SHEET_NAME", "工作表名称无效。")
    name = raw.strip()
    if not name:
        raise HelperFailure("INVALID_SHEET_NAME", "工作表名称不能为空。")
    if len(name) > 31:
        raise HelperFailure("INVALID_SHEET_NAME", "工作表名称最长 31 个字符。")
    invalid_chars = set('\\/?*:[]')
    if any(c in invalid_chars or c < " " for c in name):
        raise HelperFailure("INVALID_SHEET_NAME", "工作表名称不能包含 \\ / ? * : [ ] 或控制字符。")
    if name.startswith("'") or name.endswith("'"):
        raise HelperFailure("INVALID_SHEET_NAME", "工作表名称不能以单引号开头或结尾。")
    return name


def _validate_filename(raw: object) -> str:
    if not isinstance(raw, str):
        raise HelperFailure("INVALID_FILENAME", "文件名无效。")
    name = raw.strip()
    if not name.lower().endswith(".xlsx") or "/" in name or "\\" in name or ".." in name:
        raise HelperFailure("INVALID_FILENAME", "文件名无效。")
    return name


def add_sheet(request: dict[str, Any]) -> dict[str, Any]:
    project_name = _validate_name(request.get("project_name"))
    file_name = _validate_filename(request.get("file_name"))
    sheet_name = _validate_sheet_name(request.get("sheet_name"))

    root = PROJECT_ROOT
    if not root.is_dir():
        raise HelperFailure("ROOT_UNAVAILABLE", "项目根目录不可用，请联系管理员。")

    target_dir = root / project_name
    if not target_dir.is_dir():
        raise HelperFailure("PROJECT_NOT_FOUND", "项目目录不存在。")

    target_file = target_dir / file_name
    if not target_file.is_file():
        raise HelperFailure("FILE_NOT_FOUND", "目标工作簿不存在。")

    try:
        from io import BytesIO
        from openpyxl import load_workbook
    except ImportError:
        raise HelperFailure(
            "TEMPLATE_UNAVAILABLE", "NAS 上缺少生成表格模板所需的组件，请联系管理员。") from None

    try:
        workbook = load_workbook(target_file)
    except Exception as error:
        raise HelperFailure("CORRUPT_WORKBOOK", "无法打开工作簿。") from error

    try:
        if sheet_name in workbook.sheetnames:
            raise HelperFailure("SHEET_EXISTS", f"工作表「{sheet_name}」已存在。")
        ws = workbook.create_sheet(title=sheet_name)
        headers = BEAT_HEADERS
        for existing in workbook.sheetnames:
            if existing == sheet_name:
                continue
            first_row = [cell.value for cell in workbook[existing][1]] if workbook[existing].max_row >= 1 else []
            if "出场" in first_row and "形态" in first_row:
                headers = ASSET_HEADERS
                break
        ws.append(headers)
        buffer = BytesIO()
        workbook.save(buffer)
    finally:
        workbook.close()

    owner_uid, owner_gid = _ownership()
    _write_atomic(target_file, buffer.getvalue(), FILE_MODE)
    _apply_ownership(target_file, owner_uid, owner_gid)

    return {"ok": True, "sheet_name": sheet_name, "file_name": file_name}


def ensure_workbooks(request: dict[str, Any]) -> dict[str, Any]:
    project_name = _validate_name(request.get("project_name"))
    root = PROJECT_ROOT
    if not root.is_dir():
        raise HelperFailure("ROOT_UNAVAILABLE", "项目根目录不可用，请联系管理员。")
    target_dir = root / project_name
    if not target_dir.is_dir():
        raise HelperFailure("PROJECT_NOT_FOUND", "项目目录不存在。")

    owner_uid, owner_gid = _ownership()
    created = []

    files = [f.name for f in target_dir.iterdir() if f.is_file() and not f.name.startswith(".")]
    has_beat = any(name.lower().endswith(".xlsx") and ("节拍" in name or "beat" in name.lower()) for name in files)
    if not has_beat:
        beat_path = target_dir / f"{BEAT_SHEET_NAME}.xlsx"
        _write_atomic(beat_path, _template_bytes(BEAT_HEADERS, BEAT_SHEET_NAME), FILE_MODE)
        _apply_ownership(beat_path, owner_uid, owner_gid)
        created.append(beat_path.name)

    has_asset = any(name.lower().endswith(".xlsx") and ("资产" in name or "asset" in name.lower()) for name in files)
    if not has_asset:
        asset_path = target_dir / f"{ASSET_SHEET_NAME}.xlsx"
        _write_atomic(asset_path, _template_bytes(ASSET_HEADERS, ASSET_SHEET_NAME), FILE_MODE)
        _apply_ownership(asset_path, owner_uid, owner_gid)
        created.append(asset_path.name)

    return {"ok": True, "created": created}


def ensure_asset_folder(request: dict[str, Any]) -> dict[str, Any]:
    project_name = _validate_name(request.get("project_name"))
    category = _validate_name(request.get("category"))
    name = _validate_name(request.get("name"))

    root = PROJECT_ROOT
    if not root.is_dir():
        raise HelperFailure("ROOT_UNAVAILABLE", "项目根目录不可用，请联系管理员。")
    target_dir = root / project_name
    if not target_dir.is_dir():
        raise HelperFailure("PROJECT_NOT_FOUND", "项目目录不存在。")

    owner_uid, owner_gid = _ownership()
    target_folder = target_dir / category / name
    if not target_folder.is_dir():
        target_folder.mkdir(parents=True, exist_ok=True)
        cat_dir = target_dir / category
        _apply_mode(cat_dir, DIRECTORY_MODE)
        _apply_ownership(cat_dir, owner_uid, owner_gid)
        _apply_mode(target_folder, DIRECTORY_MODE)
        _apply_ownership(target_folder, owner_uid, owner_gid)

    return {"ok": True, "path": str(target_folder)}


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
        raise HelperFailure("REQUEST_TOO_LARGE", "项目请求过长。")
    return bytes(result)


def _verify_peer(fd: int) -> None:
    if not hasattr(socket, "SO_PEERCRED"):
        raise HelperFailure("SOCKET_UNSUPPORTED", "NAS 本机项目通道不可用。")
    peer = socket.socket(family=socket.AF_UNIX, type=socket.SOCK_STREAM, fileno=os.dup(fd))
    try:
        _pid, uid, _gid = struct.unpack(
            "3i", peer.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")))
    finally:
        peer.close()
    try:
        expected_uid = pwd.getpwnam(EXPECTED_PEER_USER).pw_uid
    except KeyError:
        raise HelperFailure("SERVICE_ACCOUNT_MISSING", "V2 服务账号尚未配置。") from None
    if uid != expected_uid:
        raise HelperFailure("UNAUTHORIZED_PEER", "本机项目通道拒绝访问。")


def handle_request(raw: bytes) -> dict[str, Any]:
    try:
        request = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return {"ok": False, "code": "INVALID_REQUEST", "message": "项目请求格式无效。"}
    if not isinstance(request, dict):
        return {"ok": False, "code": "INVALID_REQUEST", "message": "项目请求格式无效。"}
    try:
        op = request.get("operation")
        if op == "create_project":
            return create_project(request)
        if op == "add_sheet":
            return add_sheet(request)
        if op == "ensure_workbooks":
            return ensure_workbooks(request)
        if op == "ensure_asset_folder":
            return ensure_asset_folder(request)
        return {"ok": False, "code": "UNKNOWN_OPERATION", "message": "项目操作不受支持。"}
    except HelperFailure as error:
        return {"ok": False, "code": error.code, "message": str(error)}
    except OSError:
        # The detail can name the paths involved, and this runs as root.
        return {"ok": False, "code": "INTERNAL_ERROR", "message": "操作失败，请稍后重试。"}
    except Exception:
        return {"ok": False, "code": "INTERNAL_ERROR", "message": "操作失败，请稍后重试。"}


def main() -> int:
    input_fd = 0
    output_fd = 1
    try:
        peer_socket = socket.socket(family=socket.AF_UNIX, type=socket.SOCK_STREAM,
                                    fileno=os.dup(input_fd))
        try:
            _verify_peer(peer_socket.fileno())
        finally:
            peer_socket.close()
        response = handle_request(_read_line(input_fd))
    except HelperFailure as error:
        response = {"ok": False, "code": error.code, "message": str(error)}
    except Exception:
        response = {"ok": False, "code": "INTERNAL_ERROR", "message": "创建项目失败，请稍后重试。"}

    encoded = json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
    view = memoryview(encoded)
    while view:
        view = view[os.write(output_fd, view):]
    return 0


if __name__ == "__main__":
    sys.exit(main())
