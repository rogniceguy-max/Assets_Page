"""v3 host integration interfaces. No default successful NAS/export implementations."""
from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from typing import Any, BinaryIO, Protocol


@dataclass(frozen=True)
class StreamAsset:
    stream: BinaryIO
    length: int
    media_type: str
    name: str
    # Returned stream belongs to HTTP response; sender closes it even on disconnect.


class ExportTouch(Protocol):
    def __call__(self, connection: sqlite3.Connection, actor: Any, file_id: str) -> None:
        """Bump authoritative export snapshot version inside THIS write transaction.

        Never commit here. If host cannot maintain this invariant, raise safe 503.
        """
        ...


class WorkflowServices(Protocol):
    """Every method enforces host project/file ACL. Missing adapters must raise 503.

    Inputs with None project/operation are legacy v2 uploads; stream adapters may
    support them using explicit host scope, or reject ambiguity. Never infer admin.
    """
    def projects(self, actor: Any) -> list[dict[str, Any]]: ...
    def list_files(self, actor: Any, project_id: str) -> list[dict[str, Any]]: ...
    def create(self, actor: Any, template_kind: str, project_id: str,
               operation_id: str) -> dict[str, Any]: ...
    def import_xlsx_stream(self, actor: Any, name: str, source: BinaryIO, length: int,
                           project_id: str | None, operation_id: str | None,
                           revision_of: str | None) -> dict[str, Any]: ...
    def list_images(self, actor: Any, project_id: str, q: str | None,
                    category: str | None, cursor: str | None, page_size: int) -> dict[str, Any]: ...
    def upload_image_stream(self, actor: Any, name: str, media_type: str, source: BinaryIO,
                            length: int, project_id: str | None) -> dict[str, Any]: ...
    def image_content_stream(self, actor: Any, asset_id: str, thumbnail: bool) -> StreamAsset: ...
    def original_stream(self, actor: Any, file_id: str) -> StreamAsset: ...
    def revisions(self, actor: Any, file_id: str) -> list[dict[str, Any]]: ...
    def export_current(self, actor: Any, file_id: str, expected_version: int) -> StreamAsset:
        """Consistent complete current XLSX snapshot; NOT original bytes fallback."""
        ...
    def update_scene(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                     label_level: str | None, breakdown_plan: str | None) -> dict[str, Any]: ...
    def update_origins(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                       origins: list[dict[str, str]]) -> dict[str, Any]: ...
    def replace_images(self, actor: Any, file_id: str, row_id: str, expected_version: int,
                        selected_image_id: str | None, candidate_image_ids: list[str],
                        selection_reason: str | None) -> dict[str, Any]: ...
    def selection_history(self, actor: Any, file_id: str, row_id: str) -> list[dict[str, Any]]: ...
