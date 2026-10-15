# File Storage V2 Architecture

## Goals

- WPF clients never access NAS shares directly. User actions and local sync changes go through authenticated services.
- SQLite owns logical paths, project/workbook relationships, revisions, tombstones, operations, and sync cursors.
- NAS stores immutable file content objects. Rename, move, logical deletion, and restore update metadata without moving NAS content.
- A Rust transfer agent runs outside the WPF process. A separate Rust transfer service handles file bytes so long transfers do not consume the control API worker pool.
- Initial implementation keeps one NAS object per revision. Small-file pack storage is deferred until measurements show NAS metadata I/O is the bottleneck.

## Services and responsibilities

```text
Native WPF (.NET 8)
  ├─ UI, authentication and transfer display
  └─ Rust per-user agent (watch queue, hash, batch, resumable transfer)
       └─ HTTPS ── Rust transfer service ── private NAS object store

Python control API
  ├─ session and application authorization
  ├─ SQLite metadata transactions and change feed
  └─ issues/commits transfer operations for the Rust service
```

The per-user Rust agent has no NAS credentials and runs with the logged-in Windows user's permissions. The Rust service is an independent, non-root systemd service with a dedicated NAS identity restricted to the private object-store and staging roots. The Python API remains the authority for user identity, project scope, and metadata. The API signs short-lived HMAC-SHA256 operation tickets with a deployment secret; the transfer service has the same verification secret. Tickets are opaque to WPF and bound to an operation, opaque object ID, object kind, direction, expected generation, optional size/hash constraints, and expiry. They never contain an arbitrary NAS path. The key is stored only in service environment files, never in the repository or SQLite.

The WPF app and agent communicate over a same-user-only named pipe using a four-byte little-endian length-prefixed UTF-8 JSON protocol with a protocol version and request ID. UI commands and progress are asynchronous. MVP messages are `hello`, `configure`, `get_state`, `enqueue_upload`, `enqueue_batch`, `enqueue_download`, `pause`, `pause_for_shutdown`, `resume`, `resume_sync_roots`, `cancel`, `list_tasks`, `rescan`, `stop_watching`, `logout`, and `shutdown`; `configure` installs watchers, while `rescan` starts one background reconciliation pass. `stop_watching` removes filesystem listeners while preserving root configuration needed by already queued sync transfers. Events include `progress`, `task_state`, `scan_warning`, and `error`. The agent persists its operation queue and resumable offsets locally, so a client or agent restart can resume work. If the connection drops after a commit request is sent, the UI marks the outcome as unknown, disables cancel, and retries the same idempotent operation until the service confirms completion or conflict. It does not expose a generic filesystem path operation to the server. The bearer credential is provided to the running agent through the pipe and kept in memory; it is not written into the agent queue or configuration.

## Metadata and NAS layout

SQLite tables (names may follow existing store conventions):

- `files`: stable `file_id`, `scope_id`, nullable `project_id`, nullable `owner_user_id`, normalized `logical_path`, `current_revision_id`, `deleted_at`, `deleted_by`, `version`.
- `file_revisions`: immutable revision ID, file ID, relative NAS `storage_key`, SHA-256, byte length, media type, actor, source (`wpf`/`sync`), timestamp, operation ID.
- `file_operations`: idempotent operation ID, action, actor, client ID, base revision, state, timestamps, and result/error.
- `sync_clients`: client ID, last acknowledged change sequence, and status.
- `file_events`: monotonic sequence, file ID, action, revision/path change, and actor/source metadata.
- `file_directories`: logical directory ID, scope/project, normalized logical path, version, and tombstone metadata. Empty directories remain visible without any NAS directory.

`scope_id` covers existing department, public, personal, and project scopes. API list/mutation checks remain based on the authenticated user's current space access; the client sends a logical scope and relative path, never a UNC root. Employee `scope_id=all` listings include public, the employee's department, that employee's personal files, and project media; they never expose the other department's metadata. Visitor `scope_id=all` results include public, both departments, and all project media, while excluding every personal scope. Project records and workbook records retain stable IDs and soft-delete state. Project media is queryable by project and media kind for the global Pictures/Videos pages. Listings return direct-child file/directory union items; directories are metadata-only, including empty directories. Directory tombstones record a `deleted_operation_id` so restore affects only descendants deleted by that directory operation. A file or directory path is unique only among active entries: a tombstoned path can be reused by a new file ID, and restoring the old entry conflicts if the active path is occupied.

NAS stores bytes under `_objects/<kind>/<prefix>/<compact-uuid>`, where `kind` is `image`, `video`, `other`, or `thumbnail`, and `prefix` is the first two hex characters of the compact UUID. `MUYING_V2_TRANSFER_OBJECTS_ROOT` points to the `_objects` directory itself. SQLite `storage_key` is relative to the NAS root and includes the `_objects/` prefix. The user-visible project path is never the physical object path. The service does not expose `_objects` through SMB or directory listing APIs. Sharding avoids a single huge directory; the filename extension is metadata, not identity.

This release does not migrate prior database rows or previously published objects into this layout. The agreed first deployment starts with an empty file catalog and `_objects` store; do not import old NAS media or workbook data. This is a rollout decision, not a cleanup performed by this source change: production catalogs, objects, and NAS configuration remain untouched.

The Mac M4 media worker receives pending image/video revisions from public, department, personal, and project scopes through the control API using a separate worker bearer secret. Personal-space thumbnails are included by decision; the worker therefore processes those bytes under its service credential. It downloads the exact immutable revision through a short-lived transfer ticket, generates a 640px WebP with FFmpeg/VideoToolbox, and uploads the derivative through the Rust transfer service using `object_kind=thumbnail`. The API records each thumbnail in `file_thumbnails`, keyed by file ID, revision ID, and generator version. Thumbnail objects remain immutable and share the same object store, checksums, tickets, and transfer limits as normal revisions. The worker never mounts SMB or scans user-visible directories. It runs one media job at a time by default to avoid random I/O pressure on the 8 TB HDD.

WPF asks the control API for a thumbnail ticket for the exact revision, downloads it through the Rust agent, and caches it by file ID plus revision ID. A visible gallery retries after a missing derivative was temporarily cached as unavailable, so thumbnails appear when the Mac worker finishes. Project image previews may request the original when a thumbnail is not ready.

Overwrites create a new immutable object and point `files.current_revision_id` at it. Deletes set a tombstone and keep the logical path for history/restore. Moves and renames only update `logical_path`. Directory create/move/delete operate only on `file_directories`; a directory delete tombstones its descendants in SQLite. Restore selects an existing immutable revision as current and emits a new restore event; it does not copy bytes on the NAS. If the logical path is occupied at restore time, the API returns a conflict for the WPF UI to resolve.

Project and workbook deletes set soft-delete metadata. Foreign-key relationships and workbook rows remain. Default list endpoints hide deleted entries; restore endpoints make them active again. Permanent content purge is separate from soft delete and is not implicit.

## Transfer protocol

1. WPF asks the control API to begin an operation with an operation ID and expected current revision.
2. The API checks the actor/scope, records a pending operation, and returns a short-lived transfer ticket and transfer URL.
3. The Rust agent sends data to the Rust service. Large files use bounded streaming chunks with persisted offsets; small files are sent in manifest-based batches with per-file hashes and item results.
4. The transfer service writes only to an operation-specific staging location, streams SHA-256 calculation, flushes/validates, and reports the staged object to the control API.
5. The control API commits the revision/current pointer or tombstone in SQLite. Retries are idempotent by operation ID. Stale `base_revision` values fail as conflicts rather than silently overwriting another user's change.
6. Other clients consume events after their last sequence. A delete event removes the active local copy; a restore or content update downloads the selected revision if it is not already present locally.

SQLite and filesystem changes cannot share one transaction. Operation states (`receiving`, `staged`, `committing`, `committed`, `failed`, `aborted`) reconcile interrupted uploads. Aborted or expired transfers may leave staged bytes with no `file_revisions` row. The Rust service keeps staging separate from `_objects`, enforces a configured staging quota and per-user admission limit, and runs a TTL collector only against expired staging entries keyed by upload ID/operation ID. Collector logs retain the operation ID, byte count, age, and removal result for audit. The collector never deletes immutable `_objects`; committed objects remain until an explicit revision-purge policy exists.

The control API and transfer service use separate listeners/concurrency limits. The NAS has a mechanical disk, so the transfer service uses a bounded global scheduler with separate small-batch and large-stream queues, fair per-user admission, and backpressure. Initial concurrency is conservative and tuned on the actual NAS. The v1 storage layer keeps one object per revision; a packed-small-object store is a later optimization if measurements show per-file NAS create/metadata cost dominates.

### HTTP/control contract

- `GET /api/v2/files?scope_id=&project_id=&parent_path=&media_kind=&include_deleted=&cursor=&limit=` returns `{items,next_cursor}` with a paged direct-child logical file/directory listing, never a NAS listing. Each item includes `is_directory`, `directory_id`, `file_id`, `scope_id`, `project_id`, `logical_path`, `name`, `media_kind`, `media_type`, `size`, `sha256`, `current_revision_id`, `version`, and `deleted_at`. Directory items have `is_directory=true`, `directory_id` set, `file_id=null`, and `size=0`; file rows invert those ID fields. Missing and deleted entries are hidden by default.
- `POST /api/v2/file-operations` begins upload, delete, or move. Upload request fields are `{operation_id,client_id,source,scope_id,project_id?,logical_path,file_id?,expected_version,size,sha256,media_type?}`; a new file uses `file_id=null,expected_version=0`. The upload response includes `{operation_id,state,file_id,revision_id,object_id,expected_version,ticket,transfer_url,expires_at}`. Delete uses `{operation_id,client_id,source:"wpf"|"sync",file_id,expected_version}`; move adds `{target_scope_id,target_project_id?,target_logical_path}` and can include `previous_logical_path` to reject stale watcher events.
- `POST /api/v2/file-operations/{operation_id}/abort` accepts `{client_id,expected_state}` using the state returned by upload begin. The API checks actor, client, and expected state inside the SQLite write transaction. An abort/commit race is resolved by whichever transaction commits first; the other receives HTTP 409. Aborting a new upload soft-deletes its uncommitted path placeholder so the path can be reused. Aborting an overwrite leaves the old revision, path, MIME metadata, and version unchanged.
- `POST /api/v2/file-operations/batch` accepts `{client_id,source,directories:[{scope_id,project_id?,logical_path}],items:[upload request...]}`. Each file receives a narrow ticket and an independent result/error. Directory entries are ensured one by one; they are not an all-or-nothing transaction.
- `POST /api/v2/file-operations/{operation_id}/commit` accepts `{receipt}` after the Rust transfer service stages and flushes bytes. The API verifies the receipt and expected file version, then atomically creates the revision, updates `current_revision_id`, updates image metadata projections when applicable, and appends a change event.
- `POST /api/v2/directories` creates one logical directory with `{operation_id,scope_id,project_id?,logical_path}`. A batch upload manifest can carry multiple relative directory entries. `POST /api/v2/directories/{directory_id}/operations` accepts `{operation_id,client_id,action:"delete"|"move"|"restore",expected_version}`; move additionally takes `{target_scope_id,target_project_id?,target_logical_path}`. Directory deletion tombstones descendant files and directories; restore only revives entries marked by that deletion operation.
- `GET /api/v2/files/{file_id}/revisions` returns `{file_id,current_revision_id,items:[...]}`. `POST /api/v2/files/{file_id}/restore` accepts `{operation_id,client_id,revision_id,expected_version,source}` and selects that immutable revision as current. An occupied active path returns HTTP 409.
- `POST /api/v2/files/{file_id}/thumbnail` accepts `{revision_id,client_id}` and returns a transfer ticket for the matching immutable thumbnail. A missing derivative returns `THUMBNAIL_NOT_READY`.
- The Mac worker uses its separate bearer token with cursor-paged `GET /api/v2/media-worker/jobs?limit=10&cursor=`. It requests a deterministic thumbnail upload intent from `POST /api/v2/media-worker/thumbnails/{file_id}/{revision_id}/operations` with `{worker_id,size,sha256,media_type:"image/webp"}`. It uploads through the standard Rust transfer route and registers the signed receipt with `POST /api/v2/media-worker/thumbnails/{file_id}/{revision_id}/operations/{operation_id}/commit`.
- Project list: `GET /api/v2/project-spaces?include_deleted=false|true` returns `{items:[{id,name,deleted_at?}]}`; project restore is `POST /api/v2/project-spaces/{project_id}/restore` with no body. Workbook list: `GET /api/v2/project-workbooks?project_id={id}&include_deleted=false|true` returns `{items:[{id,name,template_kind,version,name_version,project_id,deleted_at}]}`; restore is `POST /api/v2/project-workbooks/{workbook_id}/restore` with no body. Restoring a project restores only its children tombstoned by that project deletion; individually deleted workbooks remain deleted.
- `GET /api/v2/sync/changes?client_id=&after_sequence=&limit=` returns durable changes and tombstones. Move events contain both old and new scope/project/path; directory events use `entity_kind=directory`. SSE/websocket may wake a client, but the sequence feed is authoritative and survives offline clients.
- `POST /transfer/v1/uploads` accepts a signed upload ticket and manifest; `PUT /transfer/v1/uploads/{id}/chunks` accepts idempotent `Content-Range` chunks and chunk hashes; `HEAD` reports missing ranges; `POST /transfer/v1/uploads/{id}/commit` verifies total length/hash and returns an HMAC-signed object receipt.
- Downloads use scoped tickets and support range/resume. Small-file batches carry a manifest with each logical path, operation ID, length, and hash; results are reported per file, not as one all-or-nothing filesystem transaction.

After the daemon has staged and flushed the object, the agent submits the receipt to the control API with the employee bearer token. The API verifies the receipt and performs the SQLite generation check and revision/current-pointer transaction. The daemon does not open the Python SQLite database. Both sides persist state keyed by operation ID; restart reconciliation can finish or safely abandon an interrupted transfer.

Ticket and receipt JSON use UTF-8 canonical JSON (keys sorted lexicographically, no insignificant
whitespace, UTF-8 characters unescaped). An upload/download ticket is
`v1.<base64url-no-padding(payload)>.<base64url-no-padding(HMAC-SHA256(payload))>` with claims
`{v:1,op_id,user_id,action,direction,object_id,object_kind,expected_version,size,sha256,exp,nonce}`; the NAS path
is never a claim. `exp` is Unix seconds. The transfer service receipt is
`{v:1,op_id,object_id,size,sha256,staged_at,nonce,mac}` where `mac` is base64url without padding of
HMAC-SHA256 over the canonical JSON object without `mac`. `staged_at` is a UTC RFC3339 string. The
API verifies it with constant-time comparison before committing SQLite metadata.

## Local synchronization

- The Rust agent watches only configured local sync roots. Native notifications enqueue changed paths; callbacks do no hashing, network transfer, or full scans. A configure followed by one rescan triggers one initial pass, and the watcher loop stays responsive while that pass runs. Feed application and reconciliation scans run serially in the background worker, so an older catalog scan cannot overwrite a newer feed baseline.
- A bounded in-memory channel feeds a durable local SQLite outbox. Repeated events for one path are coalesced. File uploads wait for a quiet/stable file and verify the source did not change during transfer.
- Hashes are calculated for candidate changes, not every file on every scan. Unchanged files are skipped using the local baseline and metadata.
- Initial startup, watcher errors/overflow, reconnect recovery, and a low-frequency reconciliation scan repair missed notifications. A watcher event is a hint, not the durable source of truth.
- Agent-authored downloads/restores update the local baseline before publication so they are not uploaded again as new user edits.
- On app/agent startup, a queued sync transfer waits until its matching root is configured. It remains queued while the sync page is closed or the user is selecting a project. A root identity includes its normalized local path, so changing the directory does not resume tasks that still point into the old directory. When the same root is configured, shutdown-paused tasks resume automatically; tasks the user paused remain paused.
- A local deletion means a server soft-delete operation. Other clients delete their active local copies; the central NAS object remains available for history. A stale offline edit conflicts with the tombstone instead of resurrecting it silently.
- The promised history granularity is each stable version successfully committed by the agent, with short bursts coalesced. Capturing every intermediate save requires editor integration or a lower-level filesystem filter and is outside v1.

## Migration and rollout

1. Add metadata/schema and control endpoints without changing production NAS configuration.
2. Add the independent Rust NAS daemon and per-user Windows agent with pinned toolchains and explicit versioned protocols; package/deploy only to isolated development environments first.
3. Add WPF agent supervision, history UI, soft-delete/restore, and event-feed sync.
4. Route every ordinary WPF and local-sync file mutation through this path. The source no longer includes active WPF SMB file mutations or a privileged visitor directory reader.
5. Start with an empty file catalog and empty `_objects` store, as agreed. Do not index or migrate existing NAS media or workbook data. Validate object roots, service identity, permission boundaries, and disk capacity before a controlled test deployment.
6. Measure initial sync, small-file batches, large resumable files, simultaneous users, watcher overflow/recovery, delete propagation, restore, and stale-client conflicts. Tune transfer concurrency and retention from measured results.

Production NAS services, users, ACLs, and data are out of scope for development changes. Deployment remains a separate explicitly approved step.
