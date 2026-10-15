# Project workbook storage

## Provenance

`projects_api.py` and `workflow_services.py` began as copies from `codex/project-workbooks` at
`8ad6ba2` (`handoff/project-workbooks/backend/`). They now contain local route and contract
extensions for SQLite workbooks, row editing, image display names, and live change events.

## Authoritative storage

`sqlite_store.py` is the active workbook and workflow service wired by `server.py`. The SQLite
database lives in the V2 service data directory. It is opened only by the backend; WPF clients
read and write through the authenticated API. SQLite WAL permits concurrent readers and
serializes short writes. XLSX is used for legacy import, explicit import, and export snapshots;
it is no longer the live collaborative table.

The database stores projects, workbooks, sheets, rows, per-field versions, image metadata,
asset folders, selection history, filesystem move journals, and a durable project event log.
Workbook, sheet, row, folder, and image IDs remain stable when their display names or parent
folder paths change. Workbook and sheet renames update labels only, with independent name
versions so concurrent renames conflict without reacting to unrelated row edits. Asset
category/name edits move the associated directory through the privileged helper, then update
folder and image paths in SQLite while retaining image IDs. A destination conflict does not
overwrite existing files.

Image display names are also separate from their physical filenames. Renaming an image updates
only `image_assets.display_name`; the file bytes, physical filename, and relative path stay put.
The display-name field has its own version, so simultaneous renames return a conflict instead of
silently overwriting one another. Search matches both display and physical names. Moving an asset
directory changes image paths without changing image IDs or display names.

Legacy `成员批注一` … `成员批注六` columns are import/export compatibility data, not editable
SQLite table fields. Their labels and XLSX serialization live in `xlsx_compat.py`; imported
values remain attached to their source rows so an explicit export can preserve them. Newly
created SQLite rows do not contain these fields. To convert a saved workbook API JSON snapshot
manually, run from `backend`:

```powershell
python -m project_workbooks.xlsx_compat document.json output.xlsx
```

Every committed workbook or metadata write appends a project event in its SQLite transaction.
The authenticated SSE route delivers those events to open WPF clients, which re-read the affected
data and keep local drafts. On a normal single backend instance, a connected client refreshes
after the write commits; reconnecting clients resume from their last event sequence.

New projects create the logical `节拍表` and `资产表` workbooks in SQLite. Both start with an
empty sheet and the UI provides explicit row creation. Existing project XLSX files are imported
once; when the privileged helper is configured, the originals are retained under the hidden
`.legacy-workbooks` directory.

## Host integration

`server.py` injects `SqliteProjectWorkbookStore` as the active workbook and workflow service,
wraps review operations to map imported legacy IDs, and uses `project_helper.py` for project
creation and safe asset-folder moves. Authentication remains supplied by the existing V2 host.
Image indexing reads files already present in project asset folders. Image upload, thumbnail
generation, full host ACL policy, and production rollout remain host responsibilities and are
not implemented by this store.
