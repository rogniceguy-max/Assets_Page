# Mac M4 media worker

The worker consumes revision-specific thumbnail jobs from the V2 API. It does not mount SMB, read NAS credentials, traverse logical paths, or write cache files to a share.

For each job, it downloads the source through the ticketed Rust transfer service, creates a 640 px wide WebP at quality 82, uploads it through the chunked transfer protocol, and commits the transfer receipt to the V2 API. Video decoding tries FFmpeg's VideoToolbox path first and falls back to software decoding for unsupported inputs. WebP encoding uses FFmpeg's `libwebp` encoder.

## Configuration

Required environment variables:

| Variable | Description |
| --- | --- |
| `MUYING_V2_API_BASE_URI` | V2 API HTTPS origin, for example `https://nas.example.invalid` |
| `MUYING_V2_MEDIA_WORKER_TOKEN` | Worker-only bearer token; inject it from a protected local launch configuration or secret store. Never commit it. |

Optional variables:

| Variable | Default | Description |
| --- | --- | --- |
| `MUYING_V2_MEDIA_WORKER_ID` | Random UUID per process | Worker identifier sent when starting a thumbnail operation |
| `MUYING_MEDIA_WORKER_FFMPEG` | `ffmpeg` from PATH, then Homebrew locations | FFmpeg executable |
| `MUYING_MEDIA_WORKER_FFPROBE` | `ffprobe` from PATH, then Homebrew locations | FFprobe executable |
| `MUYING_MEDIA_WORKER_TEMP` | `~/Library/Caches/MuYingDrive/MediaWorker` | Private temporary download and generation area |
| `MUYING_MEDIA_WORKER_POLL_INTERVAL` | `10` seconds | Delay between empty polls, bounded to 1–300 seconds |
| `MUYING_MEDIA_WORKER_ATTEMPTS` | `3` | Bounded retry count, 1–3 |

Run one worker instance. It fetches pages of up to ten jobs but processes each media item serially, keeping HDD and transfer load at concurrency 1. A partial source download is retained by the file/revision cache key and resumed with byte ranges; the source is removed after the thumbnail commit succeeds. Temporary files are written locally and the completed source is promoted atomically after its size and SHA-256 match the job.

## Service protocol

1. `GET /api/v2/media-worker/jobs?limit=10&cursor=...`, authenticated with `Authorization: Bearer <MUYING_V2_MEDIA_WORKER_TOKEN>`. The API returns `{items,next_cursor}` in stable file/revision order; the worker advances pages, retries one failed item at most three times, and then continues to the next revision.
2. The worker initializes the source download by posting `{"ticket":"..."}` to the job's `download_url`, then retrieves the returned `download_id` with `X-Transfer-Ticket` and byte ranges.
3. After generating and hashing the WebP, it posts `{worker_id,size,sha256,media_type:"image/webp"}` to `/api/v2/media-worker/thumbnails/{file_id}/{revision_id}/operations`.
4. It posts `{ticket,manifest}` to the operation's `transfer_url`, uploads missing ranges to `/transfer/v1/uploads/{upload_id}/chunks` with `Content-Range`, `X-Chunk-SHA256`, and `X-Transfer-Ticket`, then posts to `/transfer/v1/uploads/{upload_id}/commit`.
5. It posts `{receipt}` to `/api/v2/media-worker/thumbnails/{file_id}/{revision_id}/operations/{operation_id}/commit`.

The upload manifest uses `object_kind: "thumbnail"`, `media_type: "image/webp"`, and the operation's `op_id`, `object_id`, and `expected_version`. After the cursor is exhausted, the worker resets to the first page; uncommitted jobs are eligible for a later retry cycle. Operation and object identities must remain deterministic for a given file revision and generator version.

## launchd

`com.muying.mediaworker.plist` is a non-secret template. Copy it to the Mac, replace `__DEPLOYMENT_HOME__`, and set `MUYING_V2_API_BASE_URI` and `MUYING_V2_MEDIA_WORKER_TOKEN` in the local copy. Protect the deployed plist or secret source from other users; do not add the populated file to this repository. `start.sh` also accepts the required variables from a protected shell environment for a manual run.

Logs are JSON lines at `~/Library/Logs/media_worker.log`, rotated at 10 MB with three backups. Logs include file/revision IDs and operation state, but never bearer tokens, transfer tickets, download URLs, source paths, or credentials.
