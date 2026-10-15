"""Opt-in stdlib HTTP adapter. Nothing starts or registers routes on import."""
from __future__ import annotations

import json
import re
import io
import time
import threading
from tempfile import SpooledTemporaryFile
from dataclasses import dataclass, field
from typing import Any, BinaryIO, Mapping, Protocol
from urllib.parse import parse_qs, quote, unquote, urlsplit

from .workflow_services import StreamAsset, WorkflowServices

ROOT = "/api/v2/project-workbooks"
XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
IMAGE_TYPES = frozenset({"image/png", "image/jpeg", "image/webp"})
UPLOAD_LIMIT = 64 * 1024 * 1024
JSON_LIMIT = 64 * 1024
SCENE_JSON_LIMIT = 256 * 1024
JSON_RESPONSE_LIMIT = 25 * 1024 * 1024
DOWNLOAD_LIMIT = 256 * 1024 * 1024


class ApiFault(Exception):
    """Only safe, user-facing text belongs in message."""
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


@dataclass(frozen=True)
class BinaryAsset:
    data: bytes
    media_type: str
    name: str


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
    """Every method MUST enforce current resource ACL, including all binary GETs.

    All returned mappings conform to ../contracts/API.md. Never expose disk paths.
    Import/create commit bytes and metadata atomically, and return only when GET
    can produce a supported document (or use a separately versioned async API).
    """
    def list_files(self, actor: Any) -> list[dict[str, Any]]: ...
    def create(self, actor: Any, template_kind: str) -> dict[str, Any]: ...
    def import_xlsx(self, actor: Any, name: str, data: bytes) -> dict[str, Any]: ...
    def document(self, actor: Any, file_id: str) -> dict[str, Any]: ...
    def original(self, actor: Any, file_id: str) -> BinaryAsset: ...
    def list_images(self, actor: Any) -> list[dict[str, Any]]: ...
    def upload_image(self, actor: Any, name: str, media_type: str, data: bytes) -> dict[str, Any]:
        """Decode/validate bytes, pixel limits and actual type BEFORE persistence."""
        ...
    def image_content(self, actor: Any, asset_id: str, thumbnail: bool) -> BinaryAsset:
        """Authorize each GET. Serve PNG/JPEG renditions for WebP to native WPF."""
        ...
    def replace_images(self, actor: Any, file_id: str, row_id: str,
                       expected_version: int, selected_image_id: str | None,
                       candidate_image_ids: list[str]) -> dict[str, Any]: ...


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
    def resolve_comment(self, actor: Any, file_id: str, row_id: str, comment_id: str,
                        expected_version: int, resolved: bool,
                        resolution_note: str | None) -> dict[str, Any]: ...


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


def origins_payload(value: Any, file_id: str, row_id: str) -> list[dict[str, str]]:
    if not isinstance(value, list) or len(value) > 100:
        raise invalid("来源最多 100 项。")
    seen = set()
    for origin in value:
        if not isinstance(origin, dict) or set(origin) != {"file_id", "row_id"}:
            raise invalid()
        if not valid_id(origin["file_id"]) or not valid_id(origin["row_id"]):
            raise invalid()
        key = (origin["file_id"], origin["row_id"])
        if key in seen or origin["file_id"] == file_id:
            raise invalid("来源必须是其他修订的唯一真实节拍行。")
        seen.add(key)
    return value


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


def binary_response(asset: BinaryAsset, *, original: bool = False) -> Response:
    # Byte storage and filenames originate from trusted host adapters, not URLs.
    if not asset.data or len(asset.data) > (DOWNLOAD_LIMIT if original else UPLOAD_LIMIT):
        raise ApiFault(413, "TOO_LARGE", "下载内容为空或超过大小限制。")
    if original:
        return Response(200, asset.data, {"Content-Type": XLSX,
            "Content-Disposition": "attachment; filename*=UTF-8''" + quote(asset.name, safe="")})
    if asset.media_type not in IMAGE_TYPES:
        raise ApiFault(415, "UNSUPPORTED_TYPE", "图片类型不受支持。")
    return Response(200, asset.data, {"Content-Type": asset.media_type})


def stream_response(asset: StreamAsset, *, workbook: bool = False) -> Response:
    limit = DOWNLOAD_LIMIT if workbook else UPLOAD_LIMIT
    if type(asset.length) is not int or asset.length <= 0 or asset.length > limit:
        asset.stream.close()
        raise ApiFault(413, "TOO_LARGE", "下载内容超过大小限制。")
    if (workbook and asset.media_type != XLSX) or (not workbook and asset.media_type not in IMAGE_TYPES):
        asset.stream.close()
        raise ApiFault(415, "UNSUPPORTED_TYPE", "下载内容类型不受支持。")
    try:
        # Host returns a pinned immutable, seekable snapshot (or its temporary
        # spool). Validate length BEFORE sending headers, not after truncation.
        if asset.stream.tell() != 0:
            raise ValueError("Stream must start at zero")
        asset.stream.seek(0, io.SEEK_END)
        if asset.stream.tell() != asset.length:
            raise ValueError("Length mismatch")
        asset.stream.seek(0)
    except (OSError, ValueError):
        asset.stream.close()
        raise ApiFault(503, "STORAGE_UNAVAILABLE", "下载快照尚未就绪。") from None
    headers = {"Content-Type": XLSX if workbook else asset.media_type}
    if workbook:
        headers["Content-Disposition"] = "attachment; filename*=UTF-8''" + quote(asset.name, safe="")
    return Response(200, asset.stream, headers, asset.length, 180 if workbook else 90)


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
        upload = method == "POST" and parsed.path.rstrip("/") in {ROOT + "/import", ROOT + "/image-assets"}
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
        if method == "GET" and parts == ["projects"]:
            if query:
                raise invalid()
            return json_response(200, {"items": self._workflow().projects(actor)})
        if method == "GET" and not parts:
            if query:
                if set(query) != {"project_id"}:
                    raise invalid()
                project_id = review_target(query["project_id"][0], allow_general=False)
                return json_response(200, {"items": self._workflow().list_files(actor, project_id)})
            return json_response(200, {"items": svc.list_files(actor)})
        if parts == ["create"] and method == "POST":
            value = object_body(headers, body)
            if set(value) == {"template_kind", "project_id", "operation_id"}:
                if value["template_kind"] not in ("scene_beats", "asset_review"):
                    raise invalid("请选择一种内置模板。")
                return json_response(201, self._workflow().create(actor, value["template_kind"],
                    review_target(value["project_id"], allow_general=False),
                    review_target(value["operation_id"], allow_general=False)))
            if set(value) != {"template_kind"} or value["template_kind"] not in ("scene_beats", "asset_review"):
                raise invalid("请选择一种内置模板。")
            return json_response(201, svc.create(actor, value["template_kind"]))
        if parts == ["import"] and method == "POST":
            if any(key in headers for key in ("x-project-id", "x-operation-id", "x-revision-of")):
                return self._upload_route(actor, target, headers, io.BytesIO(body), len(body))
            name = filename(headers)
            media_type = headers.get("content-type", "").split(";", 1)[0].strip().lower()
            if not name.lower().endswith(".xlsx") or media_type not in {XLSX, "application/octet-stream"}:
                raise ApiFault(415, "UNSUPPORTED_TYPE", "项目只接收 .xlsx 文件。")
            if not body:
                raise ApiFault(422, "INVALID_WORKBOOK", "表格文件为空。")
            return json_response(201, svc.import_xlsx(actor, name, body))
        if parts == ["image-assets"]:
            if method == "GET":
                if query:
                    if set(query) - {"project_id", "q", "category", "cursor", "page_size"} or "project_id" not in query:
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
                    return json_response(200, self._workflow().list_images(actor, project_id, q,
                        category, cursor, int(raw_size)))
                return json_response(200, {"items": svc.list_images(actor)})
            if method == "POST":
                if "x-project-id" in headers:
                    return self._upload_route(actor, target, headers, io.BytesIO(body), len(body))
                media_type = headers.get("content-type", "").split(";", 1)[0].strip().lower()
                if media_type not in IMAGE_TYPES:
                    raise ApiFault(415, "UNSUPPORTED_TYPE", "请选择 PNG、JPEG 或 WebP 图片。")
                if not body:
                    raise ApiFault(422, "INVALID_IMAGE", "图片为空。")
                return json_response(201, svc.upload_image(actor, filename(headers), media_type, body))
        if len(parts) == 3 and parts[0] == "image-assets" and parts[2] == "name" and method == "PUT":
            value = object_body(headers, body)
            if query or set(value) != {"expected_version", "display_name"}:
                raise invalid()
            rename = getattr(self._workflow(), "rename_image", None)
            if rename is None:
                raise ApiFault(503, "STORAGE_UNAVAILABLE", "图片重命名尚未接入。")
            return json_response(200, rename(actor, parts[1], review_version(value["expected_version"]),
                                              value["display_name"]))
        if len(parts) == 2 and parts[0] == "image-assets" and method == "GET":
            if query:
                raise invalid()
            get_image = getattr(self._workflow(), "image_metadata", None)
            if get_image is None:
                raise ApiFault(503, "STORAGE_UNAVAILABLE", "素材读取尚未接入。")
            return json_response(200, get_image(actor, parts[1]))
        if len(parts) == 3 and parts[0] == "image-assets" and parts[2] == "content" and method == "GET":
            query = parse_qs(parsed.query, keep_blank_values=True)
            if set(query) - {"thumbnail"} or query.get("thumbnail", ["true"]) not in (["true"], ["false"]):
                raise invalid()
            thumbnail = query.get("thumbnail", ["true"]) == ["true"]
            if self.workflow is not None:
                return stream_response(self.workflow.image_content_stream(actor, parts[1], thumbnail))
            return binary_response(svc.image_content(actor, parts[1], thumbnail))
        # Reserved static names must never fall through as workbook IDs.
        if parts and parts[0] not in {"create", "import", "image-assets", "projects"}:
            if len(parts) == 2 and parts[1] == "name" and method == "PUT":
                value = object_body(headers, body)
                if query or set(value) != {"expected_name_version", "display_name"}:
                    raise invalid()
                rename = getattr(self._workflow(), "rename_workbook", None)
                if rename is None:
                    raise ApiFault(503, "STORAGE_UNAVAILABLE", "工作簿重命名尚未接入。")
                return json_response(200, rename(actor, parts[0],
                    review_version(value["expected_name_version"]), value["display_name"]))
            if len(parts) == 4 and parts[1] == "sheets" and parts[3] == "name" and method == "PUT":
                value = object_body(headers, body)
                if query or set(value) != {"expected_name_version", "display_name"}:
                    raise invalid()
                rename = getattr(self._workflow(), "rename_sheet", None)
                if rename is None:
                    raise ApiFault(503, "STORAGE_UNAVAILABLE", "工作表重命名尚未接入。")
                return json_response(200, rename(actor, parts[0], parts[2],
                    review_version(value["expected_name_version"]), value["display_name"]))
            if len(parts) == 2 and parts[1] in {"scene-rows", "asset-rows"} and method == "POST":
                value = object_body(headers, body)
                if query or set(value) != {"sheet_id"}:
                    raise invalid()
                creator = getattr(self._workflow(), "add_scene_row" if parts[1] == "scene-rows" else "add_asset_row", None)
                if creator is None:
                    raise ApiFault(503, "STORAGE_UNAVAILABLE", "新增表格行尚未接入。")
                return json_response(201, creator(actor, parts[0], review_target(value["sheet_id"], allow_general=False)))
            if len(parts) == 3 and parts[1] in {"scene-rows", "asset-rows"} and method == "DELETE":
                value = object_body(headers, body)
                if query or set(value) != {"expected_version"}:
                    raise invalid()
                delete = getattr(self._workflow(), "delete_row", None)
                if delete is None:
                    raise ApiFault(503, "STORAGE_UNAVAILABLE", "删除表格行尚未接入。")
                kind = "scene_beats" if parts[1] == "scene-rows" else "asset_review"
                return json_response(200, delete(actor, parts[0], parts[2],
                    review_version(value["expected_version"]), kind))
            if len(parts) >= 4 and parts[1] in {"asset-rows", "scene-rows"} and parts[3] in {"reviews", "review-summaries"}:
                return self._review_route(actor, method, parts, parsed.query, headers, body)
            if len(parts) == 1 and method == "GET":
                return json_response(200, svc.document(actor, parts[0]))
            if len(parts) == 2 and parts[1] == "sheets" and method == "POST":
                value = object_body(headers, body)
                sheet_name = review_text(value.get("sheet_name"))
                if self.workflow is not None:
                    return json_response(201, self._workflow().add_sheet(actor, parts[0], sheet_name))
                raise ApiFault(503, "STORAGE_UNAVAILABLE", "新增工作表服务暂未接入。")
            if len(parts) == 2 and parts[1] == "original" and method == "GET":
                if self.workflow is not None:
                    return stream_response(self.workflow.original_stream(actor, parts[0]), workbook=True)
                return binary_response(svc.original(actor, parts[0]), original=True)
            if len(parts) == 2 and parts[1] == "revisions" and method == "GET":
                if query:
                    raise invalid()
                return json_response(200, {"items": self._workflow().revisions(actor, parts[0])})
            if len(parts) == 2 and parts[1] == "export" and method == "GET":
                if set(query) != {"expected_version"} or not re.fullmatch(r"[0-9]{1,19}", query["expected_version"][0]):
                    raise invalid()
                version = review_version(int(query["expected_version"][0]))
                return stream_response(self._workflow().export_current(actor, parts[0], version), workbook=True)
            if len(parts) == 4 and parts[1] == "asset-rows" and parts[3] == "selection-history" and method == "GET":
                if query:
                    raise invalid()
                return json_response(200, {"items": self._workflow().selection_history(actor, parts[0], parts[2])})
            if len(parts) in (3, 4) and parts[1] == "scene-rows" and method == "PUT":
                value = object_body(headers, body, SCENE_JSON_LIMIT)
                if query:
                    raise invalid()
                base_fields = {"expected_version", "label_level", "breakdown_plan"}
                versioned_fields = {"expected_versions", "label_level", "breakdown_plan"}
                combined_fields = {"expected_version", "expected_versions", "label_level", "breakdown_plan"}
                if len(parts) == 3 and set(value) in (base_fields, versioned_fields, combined_fields):
                    if value["label_level"] not in (None, "红", "黄", "绿"):
                        raise invalid()
                    expected = value.get("expected_versions", value.get("expected_version"))
                    if isinstance(expected, dict):
                        if set(expected) != {"label_level", "breakdown_plan"} or any(
                                type(item) is not int or item < 1 for item in expected.values()):
                            raise invalid("字段版本无效。")
                    else:
                        expected = review_version(expected)
                    return json_response(200, self._workflow().update_scene(actor, parts[0], parts[2],
                        expected, value["label_level"],
                        optional_text(value["breakdown_plan"], 20000)))
                if len(parts) == 4 and parts[3] == "story-content" and set(value) == {"expected_version", "story_content"}:
                    return json_response(200, self._workflow().update_story_content(actor, parts[0], parts[2],
                        review_version(value["expected_version"]), optional_text(value["story_content"], 20000)))
                if len(parts) == 4 and parts[3] == "origins" and set(value) == {"expected_version", "origins"}:
                    return json_response(200, self._workflow().update_origins(actor, parts[0], parts[2],
                        review_version(value["expected_version"]), origins_payload(value["origins"], parts[0], parts[2])))
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
                has_reason = "selection_reason" in value
                reason = optional_text(value.pop("selection_reason", None), 4000)
                if has_reason or self.workflow is not None:
                    change = validate_image_change(value)
                    return json_response(200, self._workflow().replace_images(actor, parts[0], parts[2], *change, reason))
                change = validate_image_change(value)
                return json_response(200, svc.replace_images(actor, parts[0], parts[2], *change))
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
        if method == "GET" and tail == ["review-summaries"]:
            if query or body:
                raise invalid()
            return json_response(200, {"items": service.summaries(actor, file_id, row_id)})
        if method == "GET" and tail == ["reviews"]:
            if set(query) - {"image_id"} or body or ("image_id" in query and len(query["image_id"]) != 1):
                raise invalid()
            target = review_target(query["image_id"][0], allow_general=False) if "image_id" in query else None
            if scene and target is not None:
                raise invalid("节拍讨论不接受图片目标。")
            return json_response(200, service.thread(actor, file_id, row_id, target))
        if method == "DELETE" and len(tail) == 3 and tail[:2] == ["reviews", "comments"]:
            if body or set(query) != {"expected_version"} or len(query["expected_version"]) != 1:
                raise invalid()
            raw_version = query["expected_version"][0]
            if not re.fullmatch(r"[0-9]{1,19}", raw_version):
                raise invalid("评论版本无效。")
            version = review_version(int(raw_version))
            return json_response(200, service.delete_comment(actor, file_id, row_id, tail[2], version))
        if query:
            raise invalid()
        if method == "PUT" and len(tail) == 4 and tail[:2] == ["reviews", "comments"] and tail[3] == "resolution":
            value = object_body(headers, body)
            if set(value) != {"expected_version", "resolved", "resolution_note"} or type(value["resolved"]) is not bool:
                raise invalid()
            note = optional_text(value["resolution_note"], 4000)
            note = note.strip() if note is not None else None
            if value["resolved"] and not note:
                raise invalid("已解决需要填写说明。")
            return json_response(200, service.resolve_comment(actor, file_id, row_id, tail[2],
                review_version(value["expected_version"]), value["resolved"], note or None))
        if method == "PUT" and tail == ["reviews", "vote"]:
            if scene:
                raise invalid("节拍讨论不支持图片投票。")
            value = object_body(headers, body)
            if set(value) != {"image_id", "verdict"} or value["verdict"] not in ("good", "bad", None):
                raise invalid()
            image_id = review_target(value["image_id"], allow_general=False)
            return json_response(200, service.vote(actor, file_id, row_id, image_id, value["verdict"]))
        if method == "POST" and tail == ["reviews", "comments"]:
            value = object_body(headers, body)
            if set(value) != {"image_id", "text"}:
                raise invalid()
            if scene and value["image_id"] is not None:
                raise invalid("节拍讨论不接受图片目标。")
            return json_response(201, service.add_comment(actor, file_id, row_id,
                review_target(value["image_id"]), review_text(value["text"])))
        if method == "PUT" and len(tail) == 3 and tail[:2] == ["reviews", "comments"]:
            value = object_body(headers, body)
            if set(value) != {"expected_version", "text"}:
                raise invalid()
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
        if path == ROOT + "/import":
            operation = header_id(headers, "x-operation-id")
            revision = header_id(headers, "x-revision-of")
            if (project is None) != (operation is None) or (revision is not None and project is None):
                raise invalid("导入需要项目和操作标识。")
            if not name.lower().endswith(".xlsx") or media_type not in {XLSX, "application/octet-stream"}:
                raise ApiFault(415, "UNSUPPORTED_TYPE", "项目只接收 .xlsx 文件。")
            return json_response(201, workflow.import_xlsx_stream(actor, name, source, length, project, operation, revision))
        if path == ROOT + "/image-assets":
            if media_type not in IMAGE_TYPES:
                raise ApiFault(415, "UNSUPPORTED_TYPE", "请选择 PNG、JPEG 或 WebP 图片。")
            return json_response(201, workflow.upload_image_stream(actor, name, media_type, source, length, project))
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
            is_upload = handler.command == "POST" and urlsplit(handler.path).path.rstrip("/") in {
                ROOT + "/import", ROOT + "/image-assets"}
            if is_upload:
                self._workflow()  # Fail before reading a large body when adapter is missing.
            is_scene_update = handler.command == "PUT" and re.fullmatch(
                re.escape(ROOT) + r"/[^/]+/scene-rows/[^/]+(?:/(?:origins|story-content))?/?", urlsplit(handler.path).path) is not None
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
            if not all(hasattr(workflow, name) for name in ("latest_event_sequence", "wait_for_events")):
                raise ApiFault(503, "STORAGE_UNAVAILABLE", "实时更新服务暂不可用。")
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
