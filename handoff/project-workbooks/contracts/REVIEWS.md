# Review contract v2 — 好 / 不好 + independent comments

v3 expands this contract to isolated scene-row discussions and comment resolution. See `WORKFLOW_V3.md` and `openapi-workflow-v3.json` for overriding fields, routes and export-version invalidation. Asset image voting still follows the good/bad rules below.

User confirmed good/bad, not stars/numeric scores. Additive proposal to API.md; not deployed or executed. All endpoints use the same authentication, envelope, bounded JSON transport and opaque ID rules.

Scope: `(file_id, row_id, image_id)` for image review. `image_id=null` is the asset record's general discussion, NOT a review of whichever image happens to be selected. Row-general discussion has comments only. Ratings follow stable image IDs from candidate to selected, and never transfer to a replacement image.

## DTO

`ReviewSummary`: `{image_id:string|null, good:int, bad:int, comment_count:int, my_verdict:"good"|"bad"|null}`.

`ReviewComment`: `{id:string, user_id:string, user_name:string, text:string, version:int, created_at:string, updated_at:string, can_edit:bool, can_delete:bool}`. Times are ISO8601 UTC, flags are authoritative for the current actor; server still enforces ownership on mutations.

`ReviewThread`: `{image_id:string|null, good:int, bad:int, comment_count:int, my_verdict:"good"|"bad"|null, comments:ReviewComment[]}`. Comments chronological (created_at, id). No silent truncation in this version. No ratings or comments from unrelated files/rows leak into counts.

Let `R=/api/v2/project-workbooks/{file_id}/asset-rows/{row_id}`.

| Method | Suffix | Request | Success |
|---|---|---|---|
| GET | `R/review-summaries` | — | 200 `{items:ReviewSummary[]}`; include current selected/candidate IDs (unique) and null general target, even if counts zero |
| GET | `R/reviews` | optional query `image_id`; omit for general discussion | 200 ReviewThread |
| PUT | `R/reviews/vote` | `{image_id:string,verdict:"good"|"bad"|null}` | 200 ReviewThread; null withdraws only caller's vote |
| POST | `R/reviews/comments` | `{image_id:string|null,text:string}` | 201 ReviewThread; append independent comment, no vote required |
| PUT | `R/reviews/comments/{comment_id}` | `{expected_version:int,text:string}` | 200 ReviewThread for comment's stored target |
| DELETE | `R/reviews/comments/{comment_id}` | query `expected_version` positive integer; no body | 200 ReviewThread for comment's stored target |

`text` must be non-whitespace, trimmed, 1–4000 Unicode characters, no unpaired surrogates. Versions positive signed 64-bit integers. Stale edit/delete is 409 VERSION_CONFLICT, never blind-retry. Vote upsert is idempotent per current actor and target; last explicit submission by that actor wins. One actor has at most one vote per image within a row. Vote writes do not change comments; comment writes do not change votes or image relationship versions.

Backend obtains user identity/name exclusively from the authenticated principal, not request fields. Ordinary authorized reviewers can comment/vote without permission to replace asset images. Enforce separate review ACL, original file read ACL and image visibility; do not reuse metadata-write permission as the only review permission. Only the original author may edit/delete their comment in this version. Other members see author names, content and timestamps, not a delete control; server independently rejects forged requests.

Mutations and returned counts/comments should be atomic in the SQLite reference. Removing a candidate reference or choosing it as selected does not delete review records. Existing `asset_feedback` rows belong to asset-wide historical feedback and cannot be silently copied as votes on a specific image. Keep them as legacy data until an explicit mapping/migration is chosen.

Summary aggregation should run in batch per row/file in the host. The WPF handoff reads one summary request per asset row; do not misrepresent missing/failed statistics as zero. Thread refresh is manual in this package, no polling or realtime sync. Large threads need a coordinated pagination extension.
