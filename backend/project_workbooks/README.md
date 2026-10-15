# Project workbook and file catalog

## Source of truth

`SqliteProjectWorkbookStore` stores project records, workbook rows, logical file paths,
immutable revision metadata, tombstones, operations, and change events in one server-side
SQLite database. The WPF client uses authenticated HTTP APIs; project folders are not scanned
or created as project records.

File bytes are not stored in SQLite. The Rust transfer service owns immutable NAS objects;
the Python API issues short-lived HMAC tickets and commits signed receipts into SQLite. A
rename, move, delete, or restore changes catalog metadata and does not move or delete the NAS
object.

Projects and workbooks are soft-deleted. Their IDs, workbook rows, and foreign-key relations
remain, and default lists hide deleted entries. Restore is explicit. Active logical paths use
partial unique indexes, so a tombstoned path can be reused; restoring the old file then returns
a conflict if that path is occupied.

## Project images and videos

Project images use the same `files` and `file_revisions` rows as other files. `image_assets`
and `asset_folders` are SQLite projections for the workbook UI, not independent content stores.
The old helper upload/content routes are disabled; image bytes use the Rust transfer service.

New workbook video references set `VideoItem.id == VideoItem.file_id`. The stored path is a
logical project path. Changing a beat updates all linked video paths and emits file events in
the same SQLite transaction; `storage_key` and NAS bytes stay unchanged. A blank
`beat_number` is unnumbered and does not fall back to `scene_number`. Beat numbers must be
unique within an episode, but need not be continuous or complete.

## Removed spreadsheet path

The active implementation does not import, revise, compare, download, or export `.xlsx`
workbooks. Old import and selection-history tables are removed during schema migration; no
prior file bytes or workbook revision history are fabricated.
