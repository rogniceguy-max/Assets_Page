# Project workbooks — canonical handoff contract v1

**v3 precedence:** read `WORKFLOW_V3.md` and `openapi-workflow-v3.json` first. They override overlapping project/import/image/search/scene/export rules below, including the obsolete 20 MiB upload limit. This file remains the baseline for unchanged DTO fields and legacy requests. `example-scene-v3.json` uses fictional data and illustrates preservation of all six legacy note columns and explicit multi-row origins.

**Project-space update:** for current navigation and media semantics, read [`PROJECT_SPACE_V1.md`](PROJECT_SPACE_V1.md). Its project/personal-space model, row-folder mapping, aggregate media listing and soft-delete/restore behavior supersede this file's earlier project selector and simple image-detach assumptions. The endpoints below remain useful for unchanged workbook/review behavior.

Additive v2 review routes and DTOs are defined in `REVIEWS.md` and `openapi-reviews.yaml`: good/bad votes per image, independent comments, author-only editing and asset-wide general discussion. Existing file/image endpoints below retain their wire format.

`example-document.json` and `example-update-images.json` are shape examples with fictional IDs, not live data, seeded database records, or evidence of a successful request. The update illustrates selecting a candidate while keeping it in the candidate collection.

Proposed new routes under `/api/v2/project-workbooks`. They are not registered in the live app. All requests, including image GET, require the existing bearer session and per-resource authorization. Reuse existing TLS certificate validation; never disable it.

JSON uses snake_case. IDs are opaque nonempty strings suitable for a single URL segment: no slash, backslash, control characters, `.` or `..`; workbook IDs must not equal reserved route names `create`, `import`, `image-assets`. `version` is a positive integer. Dates are ISO 8601 UTC. Error: `{"error":{"code":"...","message":"safe user-facing text"}}`. Never return stack traces or server paths.

## DTOs

`ProjectFile`: `{id, name, template_kind, version}`. template_kind is `scene_beats`, `asset_review`, or `generic`. Name ends in `.xlsx`.

`ImageAsset`: `{id, name, media_type, width, height}`. IDs are durable; image bytes are fetched by ID with authorization, never by unauthenticated Image.Source URL. No local filesystem path is returned.

Native WPF must reliably decode served bytes. For WebP originals, the host content adapter should return a PNG/JPEG rendition (both thumbnail and full-preview requests) with matching Content-Type; original metadata may continue to identify the stored WebP source. Local WPF upload may initially accept PNG/JPEG only. This is a documented decoder constraint, not a restriction on backend media storage.

`AssetRow`: `{id, version, category, name, appearance, selected_image: ImageAsset|null, candidate_images: ImageAsset[]}`.

`SceneRow`: `{id, version, scene_number, beat_number, story_content, label_level, breakdown_plan}`. All display fields are strings or null. No arbitrary number/string union.

`GenericColumn`: `{key, label}`. `GenericRow`: `{id, cells: {columnKey: string|null}}`. These provide a lossless-value fallback only, not source Excel styling or formula recalculation.

`WorkbookSheet`: `{id, name, kind, asset_rows: [], scene_rows: [], columns: [], generic_rows: []}`. kind = `asset_review` / `scene_beats` / `generic`. Empty arrays are present. Order of sheets/rows/columns is meaningful. Unknown fields/sheets must not disappear: include generic sheets or explicitly return an unsupported response before claiming success.

`WorkbookDocument`: `{file: ProjectFile, sheets: WorkbookSheet[], fidelity: "template"|"values_only", notices: string[]}`. Template means the two business template layouts, NOT pixel-perfect Excel. Generic fallback is values_only and UI must display a concise limitation notice. Original .xlsx is retained independently for download.

## Routes

| Method | Suffix | Request | Success |
|---|---|---|---|
| GET | `/` (no trailing slash required) | — | 200 `{items: ProjectFile[]}` |
| POST | `/create` | JSON `{template_kind:"scene_beats"|"asset_review"}` | 201 ProjectFile; create real empty xlsx from versioned template |
| POST | `/import` | raw xlsx; `X-File-Name` = URI-percent-encoded UTF-8 base filename | 201 ProjectFile; preserve original bytes; index in background or synchronously behind generic UI |
| GET | `/{file_id}` | — | 200 WorkbookDocument |
| GET | `/{file_id}/original` | — | 200 raw original xlsx; content disposition filename |
| GET | `/image-assets` | — | 200 `{items: ImageAsset[]}` visible to current session |
| POST | `/image-assets` | raw image; X-File-Name encoded as above; Content-Type image/png or image/jpeg or image/webp | 201 ImageAsset; bytes validated, no extension-only trust |
| GET | `/image-assets/{asset_id}/content?thumbnail=true|false` | — | 200 validated image bytes; private/no-store |
| PUT | `/{file_id}/asset-rows/{row_id}/images` | JSON `{expected_version:1, selected_image_id:null|string, candidate_image_ids:string[]}` | 200 updated AssetRow |

## Image mutation rules

Single selected reference or null; candidates are ordered, unique IDs (max 100 for this proposal). All IDs checked for existence and current actor permission within the same operation. Max body xlsx 20 MiB; image 20 MiB (host may tighten). Replace all three relationship state fields atomically using expected_version. Stale version: 409 VERSION_CONFLICT without overwriting. One operation cannot erase another member's changes. Retrying after 409 requires reload and a new user intent, not blind retry.

Selecting an existing candidate keeps it in candidate_images. Removing/replacing a reference never deletes source media. Upload completes before relationship PUT. A failed PUT leaves old relations intact; uploaded but unlinked media is reclaimed by host retention policy, not by deleting arbitrary user media.

## Errors and ownership

401 SESSION_REQUIRED; 403 FORBIDDEN; 404 NOT_FOUND; 409 VERSION_CONFLICT; 413 TOO_LARGE; 415 UNSUPPORTED_TYPE; 422 INVALID_WORKBOOK / INVALID_IMAGE / INVALID_REQUEST; 503 STORAGE_UNAVAILABLE. Host authentication and ACL adapters must be supplied explicitly, never default to an admin actor. Static routes such as image-assets/create/import must be matched before file IDs.

File import/create idempotency may use existing transfer operation IDs; retries must not silently create duplicates. No mutation endpoint should be retried automatically by the WPF client. GET list pagination is deliberately not in this first handoff; host adapters must not truncate results silently. If paging is added, version the DTO and implement it end-to-end.
