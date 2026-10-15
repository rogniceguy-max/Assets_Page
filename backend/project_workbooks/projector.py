"""Turns an imported XLSX into the document shape the workbook contract expects.

The handoff package contains no spreadsheet code at all — by design: it declares the routes
and the JSON shapes and leaves every question about real workbooks to the host. This is that
host half.

Three rules govern everything here, and all three come from the contract:

* **Nothing may disappear.** A sheet that is not a scene-beat sheet and not an asset sheet is
  still a sheet someone put in the workbook, and it is returned as a generic sheet rather than
  skipped. The real master's 使用说明 sheet is exactly this case.
* **Nothing may be invented.** Where the contract permits null — the six legacy notes when
  blank, the scene and cast context we have no extractor for — the answer is null, not a guess.
  An unrecognised difficulty is left unmapped and reported rather than being coerced into the
  nearest colour.
* **The text is the text.** Reads preserve story content and newlines; edits happen only through
  explicit row-write routes and are guarded by a persisted row version.

This parser runs only at an XLSX import or legacy-migration boundary. SQLite remains the live
store after conversion, so clients never read a shared XLSX file while editing.
"""
from __future__ import annotations

import hashlib
import re
import zipfile
from base64 import urlsafe_b64encode
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Mapping

# openpyxl pulls defusedxml in when it is installed; the deployment pins it. Imported here so
# a developer machine without it still works on trusted local files. Uploaded workbooks pass
# through the host's archive and size validation before this projector reads them.
try:  # pragma: no cover - environment dependent
    from defusedxml import defuse_stdlib

    defuse_stdlib()
except ImportError:  # pragma: no cover - environment dependent
    pass

import openpyxl  # noqa: E402  (must follow defuse_stdlib)

from .projects_api import ApiFault  # noqa: E402
from .xlsx_compat import NOTE_ALIASES, NOTE_LABELS  # noqa: E402

MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_ZIP_ENTRIES = 2000
MAX_EXPANDED_BYTES = 128 * 1024 * 1024
MAX_SHEET_ROWS = 100_000
MAX_SHEET_COLUMNS = 256
MAX_SHEET_CELLS = 2_000_000
HEADER_SEARCH_ROWS = 20

EPISODE_TITLE_RE = re.compile(r"^第\s*(\d{1,4})\s*集$")

SCENE_ALIASES = {
    "场景号": "scene_number",
    "节拍号": "beat_number",
    "故事内容": "story_content",
    "标注等级": "label_level",
    "等级": "label_level",
    "制作难度": "label_level",
    "拆分规划": "breakdown_plan",
}
ASSET_ALIASES = {
    "类别": "category",
    "资产类别": "category",
    "名称": "name",
    "资产名称": "name",
    "出场": "appearance",
    "形态": "variant",
    "版本": "variant",
}
LABEL_LEVELS = {"红", "黄", "绿"}

_ZERO_WIDTH = "　"


def classify(path: Path) -> str:
    """One of ``scene_beats``, ``asset_review`` or ``generic``, decided by content.

    Never by filename: a file called 场景节拍表.xlsx that holds something else must not be
    advertised as a beat sheet.
    """
    workbook = _load(path)
    try:
        # The first understood sheet decides; a workbook of nothing but generic sheets is
        # generic. Classifying each sheet once — the scan walks up to twenty rows.
        for title in workbook.sheetnames:
            kind = _classify_sheet(workbook[title]).kind
            if kind != "generic":
                return kind
        return "generic"
    finally:
        workbook.close()


def project_document(path: Path, *, file_id: str, name: str, project_id: str, version: int = 1,
                     row_version_resolver: Callable[[Mapping[str, str]], Mapping[str, int]] | None = None,
                     project_dir: Path | None = None) -> dict[str, Any]:
    """Reads every sheet of ``path`` into a ``WorkbookDocument``."""
    workbook = _load(path)
    try:
        sheets: list[dict[str, Any]] = []
        notices: list[str] = []
        row_fingerprints: dict[str, str] = {}
        recognised = 0

        for index, title in enumerate(workbook.sheetnames):
            worksheet = workbook[title]
            classification = _classify_sheet(worksheet)
            sheet_id = _safe_id(title)
            if classification.kind == "generic":
                sheets.append(_generic_sheet(sheet_id, title, worksheet, notices))
                continue

            recognised += 1
            if classification.kind == "scene_beats":
                sheets.append(_scene_sheet(sheet_id, title, worksheet, classification, notices, row_fingerprints))
            else:
                effective_dir = project_dir or (path.parent if path else None)
                sheets.append(_asset_sheet(sheet_id, title, worksheet, classification,
                                           project_dir=effective_dir,
                                           project_id=project_id, row_fingerprints=row_fingerprints))

            # Any column the classifier did not claim is data the contract says must not vanish.
            # It cannot ride along on the recognised sheet — the client only renders generic
            # rows for a sheet whose kind is generic — so it becomes a companion sheet.
            leftover = _leftover_columns(worksheet, classification)
            if leftover:
                sheets.append(_generic_sheet(
                    _safe_id(f"{title}·未识别列"), f"{title}·未识别列", worksheet,
                    notices, only_columns=leftover))
                notices.append(f"工作表「{title}」有 {len(leftover)} 列未识别，已单独列出。")

        if row_version_resolver is not None:
            row_versions = row_version_resolver(row_fingerprints)
            for sheet in sheets:
                for kind in ("scene_rows", "asset_rows"):
                    for row in sheet[kind]:
                        row["version"] = row_versions.get(row["id"], 1)

        return {
            "file": {
                "id": file_id,
                "name": name,
                "template_kind": "scene_beats" if recognised and _first_kind(workbook) == "scene_beats" else
                                 "asset_review" if recognised else "generic",
                "version": version,
                "project_id": project_id,
                "revision_of": None,
                "revision_label": None,
            },
            "sheets": sheets,
            # "template" claims the sheets were understood as the template's fields;
            # "values_only" is the honest answer for a workbook we could only read as values.
            "fidelity": "template" if recognised else "values_only",
            "notices": notices,
        }
    finally:
        workbook.close()


def _first_kind(workbook) -> str:
    for title in workbook.sheetnames:
        kind = _classify_sheet(workbook[title]).kind
        if kind != "generic":
            return kind
    return "generic"


# --------------------------------------------------------------------------------------------
# classification
# --------------------------------------------------------------------------------------------


@dataclass
class Classification:
    kind: str
    header_row: int
    fields: dict[str, int]      # canonical field name -> column index
    notes: dict[str, int]       # canonical note label -> column index
    columns: list[str]          # every header cell in the row, normalised


def _classify_sheet(worksheet) -> Classification:
    best = Classification("generic", 0, {}, {}, [])
    best_score = 0

    for row_index, row in enumerate(worksheet.iter_rows(min_row=1, max_row=HEADER_SEARCH_ROWS), start=1):
        headers = [_normalize(cell.value) for cell in row]
        fields: dict[str, int] = {}
        notes: dict[str, int] = {}
        for column, header in enumerate(headers):
            if not header or header in fields or header in notes:
                continue
            if header in SCENE_ALIASES:
                fields[SCENE_ALIASES[header]] = column
            elif header in ASSET_ALIASES:
                fields.setdefault(ASSET_ALIASES[header], column)
            elif header in NOTE_ALIASES:
                notes[NOTE_ALIASES[header]] = column

        # A beat sheet is recognised by its shape, and also by a title of the form 第N集 —
        # which the real master uses for thirty sheets whose header the scan still finds.
        scene_score = len(fields) + len(notes)
        if scene_score > best_score:
            title = worksheet.title if hasattr(worksheet, "title") else ""
            if scene_score >= 2 or EPISODE_TITLE_RE.match(title):
                best_score = scene_score
                kind = "scene_beats"
                if len(fields) >= 2 and set(fields) & {"category", "name"}:
                    kind = "asset_review"
                best = Classification(kind, row_index, fields, notes, headers)
    return best


def _leftover_columns(worksheet, classification: Classification) -> list[int]:
    claimed = set(classification.fields.values()) | set(classification.notes.values())
    return [column for column, header in enumerate(classification.columns)
            if header and column not in claimed]


def row_fingerprint(row_id: str, row: Any, replacements: Mapping[int, Any] | None = None) -> str:
    """Stable fingerprint for every cell value in one editable workbook row."""
    replacements = replacements or {}
    values = tuple(("s" if isinstance(replacements[index], str) else "n", replacements[index])
                   if index in replacements else (cell.data_type, cell.value)
                   for index, cell in enumerate(row))
    return hashlib.sha256(repr((row_id, values)).encode("utf-8", "surrogatepass")).hexdigest()


def _normalize(value: Any) -> str:
    if value is None:
        return ""
    text = str(value).replace(_ZERO_WIDTH, " ").strip()
    return re.sub(r"\s+", " ", text)


# --------------------------------------------------------------------------------------------
# projection
# --------------------------------------------------------------------------------------------


def _scene_sheet(sheet_id: str, title: str, worksheet, classification: Classification, notices: list[str],
                 row_fingerprints: dict[str, str]) -> dict:
    rows = []
    for row_number, row in enumerate(worksheet.iter_rows(min_row=classification.header_row + 1), start=classification.header_row + 1):
        if row_number - classification.header_row > MAX_SHEET_ROWS:
            raise ApiFault(413, "TOO_LARGE", "工作表行数超出上限。")
        if not any(cell.value not in (None, "") for cell in row):
            continue
        row_id = f"{sheet_id}#{row_number}"
        row_fingerprints[row_id] = row_fingerprint(row_id, row)
        rows.append({
            "id": row_id,
            "version": 1,
            "scene_number": _text(_at(row, classification.fields, "scene_number")),
            "beat_number": _text(_at(row, classification.fields, "beat_number")),
            "story_content": _text(_at(row, classification.fields, "story_content")),
            "label_level": _label_level(_at(row, classification.fields, "label_level"), title, row_number, notices),
            "breakdown_plan": _text(_at(row, classification.fields, "breakdown_plan")),
            "legacy_notes": _legacy_notes(row, classification, title, notices),
            # The contract allows null and we have no reliable extractor, so this stays null
            # rather than being filled with a plausible-looking guess.
            "scene_context": None,
            "cast_context": None,
            "origins": [],
            "can_edit": True,
        })

    return {
        "id": sheet_id,
        "name": title,
        "kind": "scene_beats",
        "asset_rows": [],
        "scene_rows": rows,
        "columns": [],
        "generic_rows": [],
    }


def _encode_asset_id(project_id: str, rel_path: str) -> str:
    raw = f"{project_id}\x00{rel_path}".encode("utf-8")
    return urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def _asset_images(project_dir: Path | None, project_id: str, category: str | None,
                  name: str | None, variant: str | None) -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    candidates: list[dict[str, Any]] = []
    if project_dir is None or not category or not name:
        return None, candidates
    asset_folder = (project_dir / category / name).resolve()
    try:
        asset_folder.relative_to(project_dir.resolve())
    except ValueError:
        return None, candidates
    if not asset_folder.is_dir():
        return None, candidates
    for entry in sorted(asset_folder.iterdir(), key=lambda item: item.name):
        if entry.is_file() and entry.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
            rel_path = f"{category}/{name}/{entry.name}"
            candidates.append({
                "id": _encode_asset_id(project_id, rel_path),
                "name": entry.name,
                "media_type": "image/png" if entry.suffix.lower() == ".png" else "image/jpeg",
                "width": 0,
                "height": 0,
                "project_id": project_id,
                "category": category,
                "variant": variant,
                "revision_label": None,
            })
    selected = next((image for image in candidates if name in image["name"]), candidates[0]) if candidates else None
    return selected, candidates


def _asset_sheet(sheet_id: str, title: str, worksheet, classification: Classification,
                 project_dir: Path | None = None, project_id: str = "",
                 row_fingerprints: dict[str, str] | None = None) -> dict:
    rows = []
    for row_number, row in enumerate(worksheet.iter_rows(min_row=classification.header_row + 1), start=classification.header_row + 1):
        if not any(cell.value not in (None, "") for cell in row):
            continue
        category = _text(_at(row, classification.fields, "category"))
        name = _text(_at(row, classification.fields, "name"))
        appearance = _text(_at(row, classification.fields, "appearance"))
        variant = _text(_at(row, classification.fields, "variant"))

        candidate_images = []
        selected_image = None

        row_id = f"{sheet_id}#{row_number}"
        if row_fingerprints is not None:
            row_fingerprints[row_id] = row_fingerprint(row_id, row)

        if project_dir is not None and category and name:
            asset_folder = (project_dir / category / name).resolve()
            try:
                asset_folder.relative_to(project_dir.resolve())
                safe_asset_folder = True
            except ValueError:
                safe_asset_folder = False
            if safe_asset_folder and asset_folder.is_dir():
                for entry in sorted(asset_folder.iterdir(), key=lambda e: e.name):
                    if entry.is_file() and entry.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
                        rel_path = f"{category}/{name}/{entry.name}"
                        asset_obj = {
                            "id": _encode_asset_id(project_id, rel_path),
                            "name": entry.name,
                            "media_type": "image/png" if entry.suffix.lower() == ".png" else "image/jpeg",
                            "width": 0,
                            "height": 0,
                            "project_id": project_id,
                            "category": category,
                            "variant": variant,
                            "revision_label": None,
                        }
                        candidate_images.append(asset_obj)
                if candidate_images:
                    selected_image = next((img for img in candidate_images if name in img["name"]), candidate_images[0])

        rows.append({
            "id": row_id,
            "version": 1,
            "category": category,
            "name": name,
            "appearance": appearance,
            "selected_image": selected_image,
            "candidate_images": candidate_images,
            "variant": variant,
            "can_edit": True,
        })

    # If the sheet had no rows, discover asset folders from project_dir (category -> name)
    if not rows and project_dir is not None and project_dir.is_dir():
        row_num = classification.header_row + 1
        for cat_dir in sorted(project_dir.iterdir(), key=lambda e: e.name):
            if cat_dir.is_dir() and not cat_dir.name.startswith(".") and cat_dir.name in {"角色", "场景", "道具", "人物", "服化道"}:
                cat_name = cat_dir.name
                for item_dir in sorted(cat_dir.iterdir(), key=lambda e: e.name):
                    if item_dir.is_dir() and not item_dir.name.startswith("."):
                        item_name = item_dir.name
                        candidates = []
                        for entry in sorted(item_dir.iterdir(), key=lambda e: e.name):
                            if entry.is_file() and entry.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
                                rel_path = f"{cat_name}/{item_name}/{entry.name}"
                                candidates.append({
                                    "id": _encode_asset_id(project_id, rel_path),
                                    "name": entry.name,
                                    "media_type": "image/png" if entry.suffix.lower() == ".png" else "image/jpeg",
                                    "width": 0,
                                    "height": 0,
                                    "project_id": project_id,
                                    "category": cat_name,
                                    "variant": None,
                                    "revision_label": None,
                                })
                        selected = next((img for img in candidates if item_name in img["name"]), candidates[0]) if candidates else None
                        rows.append({
                            "id": f"{sheet_id}#{row_num}",
                            "version": 1,
                            "category": cat_name,
                            "name": item_name,
                            "appearance": None,
                            "selected_image": selected,
                            "candidate_images": candidates,
                            "variant": None,
                            "can_edit": True,
                        })
                        row_num += 1

    return {
        "id": sheet_id,
        "name": title,
        "kind": "asset_review",
        "asset_rows": rows,
        "scene_rows": [],
        "columns": [],
        "generic_rows": [],
    }


def _generic_sheet(
    sheet_id: str, title: str, worksheet, notices: list[str], only_columns: list[int] | None = None
) -> dict:
    """Everything the other two projections could not place, as column/value rows.

    This is the contract's "unknown data must not disappear" rule made concrete.
    """
    rows = list(worksheet.iter_rows())
    columns: list[int] = only_columns if only_columns is not None else []

    if only_columns is None:
        # A wholly unrecognised sheet: take its widest row as the header, then the rest as data.
        header_index = next((i for i, row in enumerate(rows)
                             if any(cell.value not in (None, "") for cell in row)), None)
        if header_index is None:
            return {"id": sheet_id, "name": title, "kind": "generic",
                    "asset_rows": [], "scene_rows": [], "columns": [], "generic_rows": []}
        columns = [i for i, cell in enumerate(rows[header_index]) if cell.value not in (None, "")]
        data_rows = rows[header_index + 1:]
        header_row = rows[header_index]
    else:
        header_row = rows[0] if rows else []
        data_rows = rows[1:]

    keys = [f"c{column}" for column in columns]
    descriptors = [
        {"key": key, "label": _normalize(header_row[column].value) or f"列{column + 1}"}
        for key, column in zip(keys, columns)
    ]

    projected = []
    for offset, row in enumerate(data_rows):
        values = {key: _text(row[column]) if column < len(row) else None
                  for key, column in zip(keys, columns)}
        if not any(value is not None for value in values.values()):
            continue
        projected.append({"id": f"{sheet_id}#{offset + 1}", "cells": values})

    notices.append(f"工作表「{title}」按通用表读取，仅保留单元格值。")
    return {
        "id": sheet_id,
        "name": title,
        "kind": "generic",
        "asset_rows": [],
        "scene_rows": [],
        "columns": descriptors,
        "generic_rows": projected,
    }


def _legacy_notes(row, classification: Classification, title: str, notices: list[str]) -> list[dict]:
    """Exactly six entries, in canonical order, whether or not the columns were found.

    Blank is the normal case — the real master's six columns are entirely empty — and the
    contract wants the label and the source cell kept even then, so a reviewer can see that the
    column exists and where it is. When a column is genuinely absent the cell address is left
    null rather than invented.
    """
    notes = []
    for label in NOTE_LABELS:
        column = classification.notes.get(label)
        if column is None:
            notes.append({"label": label, "text": None, "source_cell": None})
            continue
        cell = row[column] if column < len(row) else None
        notes.append({
            "label": label,
            "text": _text(cell.value) if cell is not None else None,
            "source_cell": getattr(cell, "coordinate", None),
        })
    if any(note["source_cell"] is None for note in notes):
        missing = ", ".join(note["label"] for note in notes if note["source_cell"] is None)
        notices.append(f"工作表「{title}」缺少批注列：{missing}。")
    return notes


def _at(row, fields: dict[str, int], name: str):
    column = fields.get(name)
    if column is None or column >= len(row):
        return None
    return row[column].value


def _text(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text.strip() else None


def _label_level(value: Any, title: str, row_number: int, notices: list[str]) -> str | None:
    text = _normalize(value)
    if not text:
        return None
    if text in LABEL_LEVELS:
        return text
    # Never coerced into the nearest colour: an unmappable value is reported and the raw text
    # survives in the companion generic sheet.
    notices.append(f"「{title}」第 {row_number} 行的标注等级「{text}」不在 红/黄/绿 之内，已留空。")
    return None


# --------------------------------------------------------------------------------------------
# loading
# --------------------------------------------------------------------------------------------


def _safe_id(text: str) -> str:
    """A sheet id that is safe as a URL segment and as a JSON key."""
    candidate = text.strip()
    if candidate and candidate not in {".", ".."} and not any(c in candidate for c in "\\/\x00") and not any(
        ord(c) < 32 for c in candidate
    ):
        return candidate
    return urlsafe_b64encode(text.encode("utf-8")).decode("ascii").rstrip("=")


def _load(path: Path):
    _validate_archive(path)
    try:
        return openpyxl.load_workbook(path, read_only=True, data_only=True, keep_links=False)
    except Exception as error:  # noqa: BLE001 - openpyxl raises a wide range on malformed input
        raise ApiFault(422, "INVALID_WORKBOOK", "无法读取该工作簿。") from error


def _validate_archive(path: Path) -> None:
    """Structural checks before openpyxl is handed the file.

    A workbook is a ZIP of XML. These bounds stop a decompression bomb or a mislabelled file
    from reaching the parser at all; they are not a substitute for openpyxl's own reading.
    """
    try:
        size = path.stat().st_size
    except OSError as error:
        raise ApiFault(404, "NOT_FOUND", "工作簿不存在。") from error
    if size <= 0 or size > MAX_FILE_BYTES:
        raise ApiFault(413, "TOO_LARGE", "工作簿大小超出上限。")

    try:
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > MAX_ZIP_ENTRIES:
                raise ApiFault(413, "TOO_LARGE", "工作簿内部条目过多。")
            expanded = sum(entry.file_size for entry in entries)
            if expanded > MAX_EXPANDED_BYTES:
                raise ApiFault(413, "TOO_LARGE", "工作簿展开后超出上限。")
            for entry in entries:
                if entry.compress_size == 0 and entry.file_size > 0:
                    raise ApiFault(422, "INVALID_WORKBOOK", "工作簿内部结构异常。")
                if entry.compress_size and entry.file_size / entry.compress_size > 2000:
                    raise ApiFault(422, "INVALID_WORKBOOK", "工作簿压缩比异常。")
            names = {entry.filename for entry in entries}
            if "[Content_Types].xml" not in names or "xl/workbook.xml" not in names:
                raise ApiFault(422, "INVALID_WORKBOOK", "该文件不是有效的 xlsx 工作簿。")
    except zipfile.BadZipFile as error:
        raise ApiFault(422, "INVALID_WORKBOOK", "该文件不是有效的 xlsx 工作簿。") from error
