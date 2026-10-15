# Current WPF and backend integration

The native WPF application creates `HttpProjectWorkbookService` with the host's authenticated
`HttpClient`, current bearer token, and stable account ID. The module uses the host's TLS policy
and does not retry mutations automatically. Pending operation state is limited to idempotent
workbook creation.

## Visible workflows

- Select a project, list its SQLite workbooks, and create, rename, or delete a workbook.
- Open a workbook; add sheets and scene/asset rows; edit scene text, beat number, planning, level,
  notes, asset names, categories, and image associations.
- Associate local videos with beats, inspect discarded videos, and restore a video.
- Search, upload, rename, select, and compare project images.
- Discuss scene or asset rows, comment on image choices, and vote on images.
- Receive workbook changes through project SSE events; reload affected data while retaining drafts.

## Storage

SQLite is the only workbook source of truth. Project folders contain physical images and videos;
project registration creates the built-in scene-beat and asset workbooks directly in SQLite. No
`.xlsx` import, migration, archive, revision, or export path is part of the application.

The privileged helper runs as a separate root-owned process because the main server is prevented
from writing arbitrary shared folders. It accepts a narrow set of requests: create/delete a project
folder, move an asset folder after category/name changes, and save a streamed image upload into its
validated project folder. Workbook rows and image metadata are changed by the SQLite service. The
helper refuses destination conflicts so an existing folder is not overwritten.

## Transport and layout

Image upload uses bounded streaming with a 64 MiB limit. Reads use the authenticated client. SQLite
writes use field versions and return conflicts when the same field changed concurrently. Committed
writes notify open clients through SSE.

The page reuses the host theme resources, keeps the workbook view free of horizontal scrolling,
and allows image previews to wrap with the available width.

## Verification status

This cleanup has not been compiled or run, and tests have not been executed. Static source
inspection is not a substitute for checking the application against the deployed NAS and WPF
runtime.
