"""Opt-in stdlib HTTP adapter. Nothing starts or registers routes on import."""
from __future__ import annotations

import json
import re
import io
import time
import threading
import unicodedata
from tempfile import SpooledTemporaryFile
from dataclasses import dataclass, field
from typing import Any, BinaryIO, Mapping, Protocol
from urllib.parse import parse_qs, unquote, urlsplit

from .workflow_services import StreamAsset, WorkflowServices

ROOT = "/api/v2/project-workbooks"
IMAGE_TYPES = frozenset({"image/png", "image/jpeg", "image/webp"})
UPLOAD_LIMIT = 64 * 1024 * 1024
JSON_LIMIT = 64 * 1024
SCENE_JSON_LIMIT = 256 * 1024
JSON_RESPONSE_LIMIT = 25 * 1024 * 1024
SCENE_ROW_VERSION_KEYS = frozenset({
    "scene_number", "beat_number", "story_content", "breakdown_plan", "label_level", "note",
    "video", "videos", "discarded_videos",
})
SCENE_ROW_SCALAR_FIELDS = frozenset({
    "scene_number", "beat_number", "story_content", "breakdown_plan", "label_level", "note",
})
SCENE_ROW_VIDEO_ACTIONS = frozenset({"attach", "add", "discard", "restore"})
SCENE_ROW_VIDEO_OPERATION_FIELDS = frozenset({
    "video_action", "video", "video_id", "video_name", "allow_multiple",
})


class ApiFault(Exception):
    """Only safe, user-facing text belongs in message."""
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


@dataclass(frozen=True)
class Response:
    status: int
    body: bytes | BinaryIO
    headers: Mapping[str, str] = field(default_factory=dict)
    length: int | None = None
    deadline_seconds: int = 90

    def __post_init__(self) -> None:
        object.__setattr__(self, "headers", {**self.headers,
            "Cache-Control": "private, no-store", "X-Content-Type-Options": "nosniff"})


class Security(Protocol):
    def authenticate(self, headers: Mapping[str, str]) -> Any:
        """Validate host bearer session; return host principal or raise 401 ApiFault."""
        ...


class WorkbookServices(Protocol):
    """Workbook-document reads; the active workflow service owns all other API operations.

    All returned mappings conform to ../contracts/API.md. Never expose disk paths.
    """
    def document(self, actor: Any, file_id: str) -> dict[str, Any]: ...


class ReviewServices(Protocol):
    """Separate review ACL; normal reviewers need not have image-replace permission."""
    def summaries(self, actor: Any, file_id: str, row_id: str) -> list[dict[str, Any]]: ...
    def thread(self, actor: Any, file_id: str, row_id: str, image_id: str | None) -> dict[str, Any]: ...
    def vote(self, actor: Any, file_id: str, row_id: str, image_id: str,
             verdict: str | None) -> dict[str, Any]: ...
    def add_comment(self, actor: Any, file_id: str, row_id: str, image_id: str | None,
                    text: str) -> dict[str, Any]: ...
    def edit_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                     expected_version: int, text: str) -> dict[str, Any]: ...
    def delete_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                       expected_version: int) -> dict[str, Any]: ...


def json_response(status: int, value: Any) -> Response:
    data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(data) > JSON_RESPONSE_LIMIT:
        raise ApiFault(413, "TOO_LARGE", "完整结果超过响应上限，需要分页能力。")
    return Response(status, data,
                    {"Content-Type": "application/json; charset=utf-8"})


def error_response(error: ApiFault) -> Response:
    return json_response(error.status, {"error": {"code": error.code, "message": error.message}})


def invalid(message: str = "请求内容无效。") -> ApiFault:
    return ApiFault(422, "INVALID_REQUEST", message)


def valid_id(value: Any) -> bool:
    return (isinstance(value, str) and 0 < len(value) <= 512 and value not in {".", ".."}
            and "/" not in value and "\\" not in value
            and not any(ord(c) < 32 or ord(c) == 127 or 0xD800 <= ord(c) <= 0xDFFF for c in value))


def review_version(value: Any) -> int:
    if type(value) is not int or not 1 <= value <= 2**63 - 1:
        raise invalid("评论版本无效。")
    return value


def review_text(value: Any) -> str:
    if not isinstance(value, str) or any(0xD800 <= ord(c) <= 0xDFFF for c in value):
        raise invalid("评论文字无效。")
    value = value.strip()
    if not 1 <= len(value) <= 4000:
        raise invalid("评论需要 1–4000 个字符。")
    return value


def review_target(value: Any, *, allow_general: bool = True) -> str | None:
    if value is None and allow_general:
        return None
    if not valid_id(value):
        raise invalid("请选择有效图片。")
    return value


def optional_text(value: Any, maximum: int) -> str | None:
    if value is None:
        return None
    if (not isinstance(value, str) or len(value) > maximum
            or any(0xD800 <= ord(c) <= 0xDFFF for c in value)):
        raise invalid("文字长度或编码无效。")
    return value


def asset_component(value: Any, maximum: int, label: str) -> str | None:
    text = optional_text(value, maximum)
    if text is None:
        return None
    if (text in {".", ".."} or any(character in text for character in "/\\")
            or any(ord(character) < 32 or ord(character) == 127 for character in text)
            or len(text.encode("utf-8")) > 200):
        raise invalid(f"{label}不能包含路径分隔符或控制字符。")
    return text or None


def video_item_payload(value: Any) -> dict[str, Any] | None:
    """Validate a VideoItem before it crosses the workbook API boundary.

    New writes use project-relative POSIX paths, carry an ID, and store the file name as
    the final path segment. Stored workbook payloads are not rewritten by this validator.
    """
    if value is None:
        return None
    if not isinstance(value, dict):
        raise invalid("视频信息格式无效。")
    allowed = {"id", "file_id", "name", "path", "duration", "thumb_url", "thumbUrl",
               "discarded_at", "discardedAt"}
    if set(value) - allowed:
        raise invalid("视频信息包含未知字段。")

    def safe_text(item: Any, maximum: int, label: str, *, non_empty: bool = False) -> str | None:
        if item is None:
            return None
        if (not isinstance(item, str) or len(item) > maximum
                or any(unicodedata.category(char) == "Cc" or 0xD800 <= ord(char) <= 0xDFFF
                       for char in item)
                or (non_empty and not item.strip())):
            raise invalid(f"{label}无效。")
        return item

    name = safe_text(value.get("name"), 255, "视频名称", non_empty=True)
    if name is None or name in {".", ".."} or "/" in name or "\\" in name:
        raise invalid("视频名称必须是文件名。")

    video_id = value.get("id")
    if not valid_id(video_id):
        raise invalid("视频标识无效。")
    file_id = value.get("file_id")
    if not valid_id(file_id) or file_id != video_id:
        raise invalid("视频标识必须对应稳定文件标识。")

    path = safe_text(value.get("path"), 4096, "视频路径")
    if path is None:
        raise invalid("新视频必须使用项目内逻辑路径。")
    if path is not None:
        segments = path.split("/")
        if (not path or path.startswith("/") or "\\" in path
                or re.match(r"^[A-Za-z]:", path)
                or any(segment in {"", ".", ".."} for segment in segments)):
            raise invalid("新视频路径必须是项目内使用 / 分隔的相对路径。")
        if segments[-1].casefold() != name.casefold():
            raise invalid("视频路径末尾必须与视频名称一致。")

    payload = dict(value)
    payload["name"] = name
    if "duration" in payload:
        safe_text(payload["duration"], 100, "视频时长")
    for field in ("thumb_url", "thumbUrl"):
        if field in payload:
            safe_text(payload[field], 2048, "视频缩略图地址")
    for field in ("discarded_at", "discardedAt"):
        if field in payload:
            safe_text(payload[field], 100, "视频删除时间")
    return payload


def scene_row_update_version_fields(changes: Mapping[str, Any]) -> set[str]:
    """Return every field version a generic scene-row update can change."""
    version_fields = set(changes) & SCENE_ROW_SCALAR_FIELDS

    if "video_action" not in changes:
        if "video" in changes:
            version_fields.update({"video", "videos"})
        return version_fields

    action = changes["video_action"]
    if not isinstance(action, str) or action not in SCENE_ROW_VIDEO_ACTIONS:
        raise invalid("视频操作无效。")
    if action in {"attach", "add"}:
        if changes.get("video"):
            version_fields.update({"video", "videos"})
            allow_multiple = changes.get("allow_multiple", action == "add")
            if type(allow_multiple) is not bool:
                raise invalid("视频列表选项无效。")
            if not allow_multiple:
                version_fields.add("discarded_videos")
    elif action == "discard":
        version_fields.update({"video", "videos", "discarded_videos"})
    elif action == "restore":
        version_fields.update({"video", "videos", "discarded_videos"})
    return version_fields


def is_pure_video_operation(changes: Mapping[str, Any]) -> bool:
    """Whether an unversioned generic scene-row update is only an atomic video action."""
    action = changes.get("video_action")
    return (isinstance(action, str) and action in SCENE_ROW_VIDEO_ACTIONS
            and not (set(changes) & SCENE_ROW_SCALAR_FIELDS)
            and not (set(changes) - SCENE_ROW_VIDEO_OPERATION_FIELDS))


def query_values(raw: str) -> dict[str, list[str]]:
    if re.search(r"%(?![0-9a-fA-F]{2})", raw):
        raise invalid()
    try:
        values = parse_qs(raw, keep_blank_values=True, encoding="utf-8", errors="strict", max_num_fields=12)
    except (ValueError, UnicodeError):
        raise invalid() from None
    if any(len(items) != 1 for items in values.values()):
        raise invalid()
    return values


def header_id(headers: Mapping[str, str], name: str) -> str | None:
    if name not in headers:
        return None
    raw = headers[name]
    if re.search(r"%(?![0-9a-fA-F]{2})", raw):
        raise invalid()
    try:
        value = unquote(raw, encoding="utf-8", errors="strict")
    except UnicodeError:
        raise invalid() from None
    return review_target(value, allow_general=False)


def validate_image_change(value: dict[str, Any]) -> tuple[int, str | None, list[str]]:
    if set(value) != {"expected_version", "selected_image_id", "candidate_image_ids"}:
        raise invalid()
    version, selected, candidates = (value[k] for k in
                                    ("expected_version", "selected_image_id", "candidate_image_ids"))
    if type(version) is not int or version < 1 or version >= 2**63 - 1:
        raise invalid()
    if selected is not None and not valid_id(selected):
        raise invalid()
    if not isinstance(candidates, list) or len(candidates) > 100:
        raise invalid()
    if not all(valid_id(item) for item in candidates) or len(set(candidates)) != len(candidates):
        raise invalid("备选图片必须唯一，且最多为 100 张。")
    return version, selected, candidates


def filename(headers: Mapping[str, str]) -> str:
    raw = headers.get("x-file-name", "")
    if not raw or len(raw) > 4096 or re.search(r"%(?![0-9a-fA-F]{2})", raw):
        raise invalid("缺少有效文件名。")
    try:
        name = unquote(raw, encoding="utf-8", errors="strict")
    except UnicodeError:
        raise invalid("文件名编码无效。") from None
    if (len(name) > 255 or name in {".", ".."} or name != name.strip()
            or any(c in name for c in '/\\:') or any(ord(c) < 32 for c in name)):
        raise invalid("请使用有效的文件名。")
    return name


def object_body(headers: Mapping[str, str], body: bytes, limit: int = JSON_LIMIT) -> dict[str, Any]:
    if headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/json":
        raise ApiFault(415, "UNSUPPORTED_TYPE", "请求必须为 JSON。")
    if len(body) > limit:
        raise ApiFault(413, "TOO_LARGE", "请求内容过大。")
    try:
        value = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeError):
        raise invalid() from None
    if not isinstance(value, dict):
        raise invalid()
    return value


def stream_response(asset: StreamAsset) -> Response:
    if type(asset.length) is not int or asset.length <= 0 or asset.length > UPLOAD_LIMIT:
        asset.stream.close()
        raise ApiFault(413, "TOO_LARGE", "下载内容超过大小限制。")
    if asset.media_type not in IMAGE_TYPES:
        asset.stream.close()
        raise ApiFault(415, "UNSUPPORTED_TYPE", "下载内容类型不受支持。")
    try:
        # Validate the seekable image stream's reported length before sending headers.
        if asset.stream.tell() != 0:
            raise ValueError("Stream must start at zero")
        asset.stream.seek(0, io.SEEK_END)
        if asset.stream.tell() != asset.length:
            raise ValueError("Length mismatch")
        asset.stream.seek(0)
    except (OSError, ValueError):
        asset.stream.close()
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "图片内容暂不可用。") from None
    return Response(200, asset.stream, {"Content-Type": asset.media_type}, asset.length)


class ProjectsApi:
    def __init__(self, security: Security, services: WorkbookServices,
                 reviews: ReviewServices | None = None, workflow: WorkflowServices | None = None,
                 scene_reviews: ReviewServices | None = None, upload_limit: int = UPLOAD_LIMIT):
        self.security, self.services = security, services
        self.reviews = reviews
        self.workflow, self.scene_reviews = workflow, scene_reviews
        self._event_stream_slots = threading.BoundedSemaphore(12)
        if type(upload_limit) is not int or upload_limit < 1:
            raise ValueError("upload_limit must be a positive integer")
        self.upload_limit = upload_limit

    def _workflow(self) -> WorkflowServices:
        if self.workflow is None:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "项目工作流尚未接入宿主。")
        return self.workflow

    @staticmethod
    def owns(target: str) -> bool:
        path = urlsplit(target).path
        return path == ROOT or path.startswith(ROOT + "/")

    def dispatch(self, method: str, target: str, headers: Mapping[str, str],
                 body: bytes = b"") -> Response | None:
        """For hosts that already enforce a streaming body limit and HTTP framing."""
        if not self.owns(target):
            return None
        normalized = {key.lower(): value for key, value in headers.items()}
        try:
            actor = self.security.authenticate(normalized)
            return self._route(actor, method, target, normalized, body)
        except ApiFault as error:
            return error_response(error)

    def _route(self, actor: Any, method: str, target: str,
               headers: Mapping[str, str], body: bytes) -> Response:
        parsed = urlsplit(target)
        upload = method == "POST" and parsed.path.rstrip("/") == ROOT + "/image-assets"
        if upload and len(body) > self.upload_limit:
            raise ApiFault(413, "TOO_LARGE", "文件超过上传限制。")
        path = parsed.path[len(ROOT):].strip("/")
        parts = path.split("/") if path else []
        if len(parts) > 7 or any(re.search(r"%(?![0-9a-fA-F]{2})", p) for p in parts):
            raise invalid()
        try:
            parts = [unquote(p, encoding="utf-8", errors="strict") for p in parts]
        except UnicodeError:
            raise invalid() from None
        if any(not valid_id(p) or "/" in p or "\\" in p for p in parts):
            raise invalid()
        svc = self.services
        query = query_values(parsed.query)
        is_restore_route = len(parts) == 2 and parts[1] == "restore" and method == "POST"
        if parts and parts[0] not in {"create", "image-assets", "projects"} and not is_restore_route:
            active_check = getattr(self._workflow(), "assert_active_workbook", None)
            if callable(active_check):
                active_check(parts[0])
        if method == "GET" and parts == ["projects"]:
            if query:
                raise invalid()
            return json_response(200, {"items": self._workflow().projects(actor)})
        if method == "GET" and not parts:
            if set(query) - {"project_id", "include_deleted"} or "project_id" not in query:
                raise invalid("列出工作簿需要指定项目范围。")
            project_id = review_target(query["project_id"][0], allow_general=False)
            raw_deleted = query.get("include_deleted", ["false"])[0]
            if raw_deleted not in {"true", "false"}:
                raise invalid("删除项筛选无效。")
            if raw_deleted == "true":
                items = self._workflow().list_files(actor, project_id, include_deleted=True)
            else:
                items = self._workflow().list_files(actor, project_id)
            return json_response(200, {"items": items})
        if parts == ["create"] and method == "POST":
            value = object_body(headers, body)
            if (not {"template_kind", "project_id", "operation_id"}.issubset(value)
                    or set(value) - {"template_kind", "project_id", "operation_id", "name"}
                    or value["template_kind"] not in ("scene_beats", "asset_review")):
                raise invalid("请选择一种内置模板。")
            name_val = value.get("name")
            if name_val is not None:
                name_val = str(name_val).strip() or None
            return json_response(201, self._workflow().create(actor, value["template_kind"],
                review_target(value["project_id"], allow_general=False),
                review_target(value["operation_id"], allow_general=False), name=name_val))
        if parts == ["image-assets"]:
            if method == "GET":
                if (set(query) - {"project_id", "q", "category", "cursor", "page_size"}
                        or "project_id" not in query):
                    raise invalid("素材检索需要项目范围。")
                project_id = review_target(query["project_id"][0], allow_general=False)
                q = optional_text(query.get("q", [None])[0], 200)
                category = query.get("category", [None])[0]
                if category not in (None, "角色", "场景"):
                    raise invalid()
                raw_size = query.get("page_size", ["40"])[0]
                if not re.fullmatch(r"[0-9]{1,3}", raw_size) or not 1 <= int(raw_size) <= 100:
                    raise invalid()
                cursor = optional_text(query.get("cursor", [None])[0], 4096)
                return json_response(200, {"items": self._workflow().list_images(actor, project_id, q,
                    category, cursor, int(raw_size))})
            if method == "POST":
                if "x-project-id" not in headers:
                    raise invalid("上传图片需要指定项目范围。")
                return self._upload_route(actor, target, headers, io.BytesIO(body), len(body))
        if len(parts) == 3 and parts[0] == "image-assets" and parts[2] == "name" and method == "PUT":
            value = object_body(headers, body)
            if query or set(value) != {"expected_version", "display_name"}:
                raise invalid()
            return json_response(200, self._workflow().rename_image(actor, parts[1],
                review_version(value["expected_version"]), value["display_name"]))
        if len(parts) == 2 and parts[0] == "image-assets" and method == "GET":
            if query:
                raise invalid()
            return json_response(200, self._workflow().image_metadata(actor, parts[1]))
        if len(parts) == 3 and parts[0] == "image-assets" and parts[2] == "content" and method == "GET":
            query = parse_qs(parsed.query, keep_blank_values=True)
            if set(query) - {"thumbnail"} or query.get("thumbnail", ["true"]) not in (["true"], ["false"]):
                raise invalid()
            thumbnail = query.get("thumbnail", ["true"]) == ["true"]
            return stream_response(self._workflow().image_content_stream(actor, parts[1], thumbnail))
        # Reserved static names must never fall through as workbook IDs.
        if parts and parts[0] not in {"create", "image-assets", "projects"}:
            if len(parts) == 2 and parts[1] == "restore" and method == "POST":
                if query:
                    raise invalid()
                return json_response(200, self._workflow().restore_workbook(actor, parts[0]))
            if len(parts) == 2 and parts[1] == "name" and method == "PUT":
                value = object_body(headers, body)
                if query or set(value) != {"expected_name_version", "display_name"}:
                    raise invalid()
                return json_response(200, self._workflow().rename_workbook(actor, parts[0],
                    review_version(value["expected_name_version"]), value["display_name"]))
            if len(parts) == 2 and parts[1] in {"scene-rows", "asset-rows"} and method == "POST":
                value = object_body(headers, body)
                if query or set(value) != {"sheet_id"}:
                    raise invalid()
                creator = self._workflow().add_scene_row if parts[1] == "scene-rows" else self._workflow().add_asset_row
                return json_response(201, creator(actor, parts[0], review_target(value["sheet_id"], allow_general=False)))
            if len(parts) == 3 and parts[1] in {"scene-rows", "asset-rows"} and method == "DELETE":
                value = object_body(headers, body)
                if query or set(value) != {"expected_version"}:
                    raise invalid()
                kind = "scene_beats" if parts[1] == "scene-rows" else "asset_review"
                return json_response(200, self._workflow().delete_row(actor, parts[0], parts[2],
                    review_version(value["expected_version"]), kind))
            if len(parts) >= 4 and parts[1] in {"asset-rows", "scene-rows"} and parts[3] in {"reviews", "review-summaries"}:
                return self._review_route(actor, method, parts, parsed.query, headers, body)
            if len(parts) == 1 and method == "GET":
                return json_response(200, svc.document(actor, parts[0]))
            if len(parts) == 1 and method == "DELETE":
                return json_response(200, self._workflow().delete_workbook(actor, parts[0]))
            if len(parts) == 2 and parts[1] == "sheets" and method == "POST":
                value = object_body(headers, body)
                sheet_name = review_text(value.get("sheet_name"))
                return json_response(201, self._workflow().add_sheet(actor, parts[0], sheet_name))
            if len(parts) in (3, 4) and parts[1] == "scene-rows" and method == "PUT":
                value = object_body(headers, body, SCENE_JSON_LIMIT)
                if query:
                    raise invalid()
                if len(parts) == 3:
                    if "expected_version" in value and "expected_versions" in value:
                        raise invalid("只能指定一种节拍版本条件。")
                    if "expected_versions" in value:
                        expected = value["expected_versions"]
                        if (not isinstance(expected, dict) or not expected
                                or set(expected) - SCENE_ROW_VERSION_KEYS
                                or any(type(item) is not int or not 1 <= item <= 2**63 - 1
                                       for item in expected.values())):
                            raise invalid("字段版本无效。")
                    elif "expected_version" in value:
                        expected = review_version(value["expected_version"])
                    else:
                        expected = None
                    if "video" in value and "video_action" not in value:
                        raise invalid("视频必须通过明确的视频操作关联或更新。")
                    if "video" in value:
                        value["video"] = video_item_payload(value["video"])
                    if "video_action" in value:
                        action = value["video_action"]
                        if not isinstance(action, str) or action not in SCENE_ROW_VIDEO_ACTIONS:
                            raise invalid("视频操作无效。")
                    if "allow_multiple" in value and type(value["allow_multiple"]) is not bool:
                        raise invalid("视频列表选项无效。")
                    if "video_id" in value and value["video_id"] is not None and not valid_id(value["video_id"]):
                        raise invalid("视频标识无效。")
                    if "video_name" in value:
                        video_name = value["video_name"]
                        if (not isinstance(video_name, str) or not video_name.strip() or len(video_name) > 255
                                or any(unicodedata.category(char) == "Cc" or 0xD800 <= ord(char) <= 0xDFFF
                                       for char in video_name)
                                or "/" in video_name or "\\" in video_name):
                            raise invalid("视频名称无效。")
                    if "expected_versions" in value:
                        required_versions = scene_row_update_version_fields(value)
                        if not required_versions.issubset(expected):
                            raise invalid("字段版本必须覆盖本次更新涉及的所有字段。")
                    elif expected is None and not is_pure_video_operation(value):
                        raise invalid("节拍字段更新必须携带版本；无版本请求只允许纯视频操作。")
                    return json_response(200, self._workflow().update_scene_row(actor, parts[0], parts[2],
                        expected, value))
                if len(parts) == 4 and parts[3] == "story-content" and set(value) == {"expected_version", "story_content"}:
                    return json_response(200, self._workflow().update_story_content(actor, parts[0], parts[2],
                        review_version(value["expected_version"]), optional_text(value["story_content"], 20000)))
                raise invalid()
            if len(parts) == 3 and parts[1] == "asset-rows" and method == "PUT":
                value = object_body(headers, body)
                if query or set(value) != {"expected_version", "category", "name"}:
                    raise invalid()
                return json_response(200, self._workflow().update_asset_row(actor, parts[0], parts[2],
                    review_version(value["expected_version"]), asset_component(value["category"], 200, "类别"),
                    asset_component(value["name"], 500, "名称")))
            if len(parts) == 4 and parts[1] == "asset-rows" and parts[3] == "images" and method == "PUT":
                value = object_body(headers, body)
                change = validate_image_change(value)
                return json_response(200, self._workflow().replace_images(actor, parts[0], parts[2], *change))
        raise ApiFault(404, "NOT_FOUND", "接口不存在。")

    def _review_route(self, actor: Any, method: str, parts: list[str], raw_query: str,
                      headers: Mapping[str, str], body: bytes) -> Response:
        scene = parts[1] == "scene-rows"
        service = self.scene_reviews if scene else self.reviews
        if service is None:
            raise ApiFault(503, "STORAGE_UNAVAILABLE", "评价功能尚未接入。")
        try:
            query = parse_qs(raw_query, keep_blank_values=True, encoding="utf-8", errors="strict")
        except UnicodeError:
            raise invalid() from None
        file_id, row_id = parts[0], parts[2]
        tail = parts[3:]

        def validate_row() -> None:
            validator = getattr(self.workflow, "assert_active_row", None)
            if callable(validator):
                validator(file_id, row_id, "scene_beats" if scene else "asset_review")

        if method == "GET" and tail == ["review-summaries"]:
            if query or body:
                raise invalid()
            validate_row()
            return json_response(200, {"items": service.summaries(actor, file_id, row_id)})
        if method == "GET" and tail == ["reviews"]:
            if set(query) - {"image_id"} or body or ("image_id" in query and len(query["image_id"]) != 1):
                raise invalid()
            target = review_target(query["image_id"][0], allow_general=False) if "image_id" in query else None
            if scene and target is not None:
                raise invalid("节拍讨论不接受图片目标。")
            validate_row()
            return json_response(200, service.thread(actor, file_id, row_id, target))
        if method == "DELETE" and len(tail) == 3 and tail[:2] == ["reviews", "comments"]:
            if body or set(query) != {"expected_version"} or len(query["expected_version"]) != 1:
                raise invalid()
            raw_version = query["expected_version"][0]
            if not re.fullmatch(r"[0-9]{1,19}", raw_version):
                raise invalid("评论版本无效。")
            version = review_version(int(raw_version))
            validate_row()
            return json_response(200, service.delete_comment(actor, file_id, row_id, tail[2], version))
        if query:
            raise invalid()
        if method == "PUT" and tail == ["reviews", "vote"]:
            if scene:
                raise invalid("节拍讨论不支持图片投票。")
            value = object_body(headers, body)
            if set(value) != {"image_id", "verdict"} or value["verdict"] not in ("good", "bad", None):
                raise invalid()
            image_id = review_target(value["image_id"], allow_general=False)
            validate_row()
            return json_response(200, service.vote(actor, file_id, row_id, image_id, value["verdict"]))
        if method == "POST" and tail == ["reviews", "comments"]:
            value = object_body(headers, body)
            if set(value) != {"image_id", "text"}:
                raise invalid()
            if scene and value["image_id"] is not None:
                raise invalid("节拍讨论不接受图片目标。")
            validate_row()
            return json_response(201, service.add_comment(actor, file_id, row_id,
                review_target(value["image_id"]), review_text(value["text"])))
        if method == "PUT" and len(tail) == 3 and tail[:2] == ["reviews", "comments"]:
            value = object_body(headers, body)
            if set(value) != {"expected_version", "text"}:
                raise invalid()
            validate_row()
            return json_response(200, service.edit_comment(actor, file_id, row_id, tail[2],
                review_version(value["expected_version"]), review_text(value["text"])))
        raise ApiFault(404, "NOT_FOUND", "接口不存在。")

    def _upload_route(self, actor: Any, target: str, headers: Mapping[str, str],
                      source: BinaryIO, length: int) -> Response:
        workflow = self._workflow()  # Explicit streaming adapter; never read-all fallback.
        if not length:
            raise invalid("上传文件为空。")
        if length > self.upload_limit:
            raise ApiFault(413, "TOO_LARGE", "文件超过上传限制。")
        parsed = urlsplit(target)
        if parsed.query:
            raise invalid()
        path = parsed.path.rstrip("/")
        name = filename(headers)
        project = header_id(headers, "x-project-id")
        media_type = headers.get("content-type", "").split(";", 1)[0].strip().lower()
        if path == ROOT + "/image-assets":
            if media_type not in IMAGE_TYPES:
                raise ApiFault(415, "UNSUPPORTED_TYPE", "请选择 PNG、JPEG 或 WebP 图片。")
            folder_id = header_id(headers, "x-folder-id")
            raw_folder_path = headers.get("x-folder-path")
            folder_path = unquote(raw_folder_path, encoding="utf-8") if raw_folder_path else None
            return json_response(201, workflow.upload_image_stream(
                actor, name, media_type, source, length, project, folder_id=folder_id, folder_path=folder_path))
        raise ApiFault(404, "NOT_FOUND", "接口不存在。")

    def handle(self, handler: Any) -> bool:
        """Call FIRST from host do_GET/do_POST/do_PUT/do_DELETE. True means response sent.

        Supports Content-Length only. Uses host socket timeout/TLS/concurrency caps.
        Authenticate before reading upload bytes. Rejected unread bodies close the
        connection so another request cannot be parsed from the remaining payload.
        """
        if not self.owns(handler.path):
            return False
        if handler.command == "GET" and urlsplit(handler.path).path.rstrip("/") == ROOT + "/events":
            return self._event_stream(handler)
        original_timeout = handler.connection.gettimeout()
        try:
            headers = {key.lower(): value for key, value in handler.headers.items()}
            actor = self.security.authenticate(headers)
            lengths = handler.headers.get_all("Content-Length", [])
            if headers.get("transfer-encoding") or len(lengths) > 1:
                raise invalid("请求长度格式无效。")
            is_upload = handler.command == "POST" and urlsplit(handler.path).path.rstrip("/") == ROOT + "/image-assets"
            if is_upload:
                handler.close_connection = True
                raise ApiFault(409, "TRANSFER_REQUIRED", "请通过统一文件传输服务上传图片。")
            is_scene_update = handler.command == "PUT" and re.fullmatch(
                re.escape(ROOT) + r"/[^/]+/scene-rows/[^/]+(?:/story-content)?/?", urlsplit(handler.path).path) is not None
            limit = self.upload_limit if is_upload else (SCENE_JSON_LIMIT if is_scene_update else JSON_LIMIT)
            raw_length = lengths[0] if lengths else ("0" if handler.command in {"GET", "DELETE"} else "")
            if not re.fullmatch(r"[0-9]{1,12}", raw_length):
                raise invalid("请求需要有效 Content-Length。")
            length = int(raw_length)
            if length > limit:
                raise ApiFault(413, "TOO_LARGE", "请求内容过大。")
            deadline = time.monotonic() + (180 if is_upload else 90)
            # One MiB RAM maximum, then automatic local temp-file spill; source is
            # valid only during adapter call. Adapters must not retain it asynchronously.
            with SpooledTemporaryFile(max_size=1024 * 1024, mode="w+b") as spool:
                remaining = length
                while remaining:
                    if time.monotonic() >= deadline:
                        raise TimeoutError()
                    handler.connection.settimeout(max(0.1, min(20, deadline - time.monotonic())))
                    chunk = handler.rfile.read(min(65536, remaining))
                    if not chunk:
                        raise invalid("上传未完成。")
                    spool.write(chunk)
                    remaining -= len(chunk)
                spool.seek(0)
                if is_upload:
                    response = self._upload_route(actor, handler.path, headers, spool, length)
                else:
                    response = self._route(actor, handler.command, handler.path, headers, spool.read(limit + 1))
        except ApiFault as error:
            handler.close_connection = True
            response = error_response(error)
        except (OSError, TimeoutError):
            handler.close_connection = True
            response = error_response(ApiFault(503, "STORAGE_UNAVAILABLE", "服务暂不可用，请稍后再试。"))
        except Exception:
            # Host may add sanitized server-side diagnostics here. No exception text
            # or request body is sent to the caller. Adapt known host errors explicitly.
            handler.close_connection = True
            response = error_response(ApiFault(503, "STORAGE_UNAVAILABLE", "服务暂不可用，请稍后再试。"))
        streamed = not isinstance(response.body, bytes)
        try:
            length = response.length if streamed else len(response.body)
            if length is None:
                raise ValueError("Stream response requires length")
            handler.send_response(response.status)
            for key, value in response.headers.items():
                handler.send_header(key, value)
            handler.send_header("Content-Length", str(length))
            if handler.close_connection:
                handler.send_header("Connection", "close")
            handler.end_headers()
            deadline = time.monotonic() + response.deadline_seconds
            source = response.body if streamed else io.BytesIO(response.body)
            remaining = length
            while remaining:
                if time.monotonic() >= deadline:
                    raise TimeoutError()
                handler.connection.settimeout(max(0.1, min(20, deadline - time.monotonic())))
                chunk = source.read(min(65536, remaining))
                if not chunk:
                    raise OSError("Incomplete host stream")
                handler.wfile.write(chunk)
                remaining -= len(chunk)
            if source.read(1):
                raise OSError("Host stream exceeds declared length")
        except (OSError, ValueError):
            handler.close_connection = True  # Already sent headers: do not append JSON to binary.
        finally:
            try:
                if streamed:
                    response.body.close()
            finally:
                try:
                    handler.connection.settimeout(original_timeout)
                except OSError:
                    pass  # Socket already closed; no host state remains to restore.
        return True

    def _event_stream(self, handler: Any) -> bool:
        if not self._event_stream_slots.acquire(blocking=False):
            body = error_response(ApiFault(503, "EVENT_STREAM_BUSY", "实时更新连接已满，请稍后自动重试。"))
            handler.send_response(body.status)
            for key, value in body.headers.items():
                handler.send_header(key, value)
            handler.send_header("Content-Length", str(len(body.body)))
            handler.send_header("Retry-After", "5")
            handler.send_header("Connection", "close")
            handler.end_headers(); handler.wfile.write(body.body)
            handler.close_connection = True
            return True
        try:
            return self._event_stream_impl(handler)
        finally:
            self._event_stream_slots.release()

    def _event_stream_impl(self, handler: Any) -> bool:
        """Authenticated Server-Sent Events feed; notifications follow committed writes."""
        original_timeout = handler.connection.gettimeout()
        headers = {key.lower(): value for key, value in handler.headers.items()}
        try:
            actor = self.security.authenticate(headers)
            parsed = urlsplit(handler.path)
            query = query_values(parsed.query)
            if set(query) - {"project_id", "after"} or "project_id" not in query:
                raise invalid("实时更新需要项目范围。")
            project_id = review_target(query["project_id"][0], allow_general=False)
            workflow = self._workflow()
            workflow.list_files(actor, project_id)  # Project and directory availability are checked before streaming.
            last_event = headers.get("last-event-id")
            after_values = query.get("after")
            raw_cursor = after_values[0] if after_values else last_event
            if raw_cursor is None:
                cursor = workflow.latest_event_sequence(project_id)
                send_ready = True
            elif re.fullmatch(r"[0-9]{1,19}", raw_cursor):
                cursor = int(raw_cursor)
                send_ready = False
            else:
                raise invalid("实时更新游标无效。")
            if cursor < 0 or cursor > 2**63 - 1:
                raise invalid("实时更新游标无效。")
        except ApiFault as error:
            response = error_response(error)
            handler.send_response(response.status)
            for key, value in response.headers.items():
                handler.send_header(key, value)
            handler.send_header("Content-Length", str(len(response.body)))
            handler.send_header("Connection", "close")
            handler.end_headers()
            handler.wfile.write(response.body)
            handler.close_connection = True
            return True
        except Exception:
            response = error_response(ApiFault(503, "STORAGE_UNAVAILABLE", "实时更新服务暂不可用。"))
            handler.send_response(response.status)
            for key, value in response.headers.items():
                handler.send_header(key, value)
            handler.send_header("Content-Length", str(len(response.body)))
            handler.send_header("Connection", "close")
            handler.end_headers()
            handler.wfile.write(response.body)
            handler.close_connection = True
            return True

        handler.send_response(200)
        handler.send_header("Content-Type", "text/event-stream; charset=utf-8")
        handler.send_header("Cache-Control", "no-cache, no-transform")
        handler.send_header("X-Accel-Buffering", "no")
        handler.send_header("Connection", "keep-alive")
        handler.end_headers()

        def write_event(event_id: int, event_name: str, payload: Any) -> None:
            message = (f"id: {event_id}\nevent: {event_name}\ndata: "
                       + json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n\n")
            handler.wfile.write(message.encode("utf-8"))
            handler.wfile.flush()

        try:
            handler.connection.settimeout(20)
            if send_ready:
                write_event(cursor, "ready", {"sequence": cursor})
            while not handler.close_connection:
                events = workflow.wait_for_events(project_id, cursor, timeout=10.0)
                if not events:
                    handler.wfile.write(b": keep-alive\n\n")
                    handler.wfile.flush()
                    continue
                for event in events:
                    cursor = int(event["sequence"])
                    write_event(cursor, "change", event)
        except (BrokenPipeError, ConnectionResetError, TimeoutError, OSError):
            handler.close_connection = True
        finally:
            try:
                handler.connection.settimeout(original_timeout)
            except OSError:
                pass
        return True
