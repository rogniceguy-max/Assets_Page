# Merge acceptance checklist — not executed

## v3.1 UI acceptance — not executed in WPF

- Long scene story/plan renders in full, including all line breaks; no 180/140-character preview clipping or expand button. Click a row or use Enter/Space to open its own notes. Clicking child actions never opens a second unintended window.
- Opening row details exposes original notes first. Editing and discussion remain reachable; leaving an unsaved draft still prompts.
- No routine refresh buttons, home drop-description sentence, or old long create-button labels. Create labels are 新建节拍表 and 新建资产表; actual xlsx import/drop restrictions remain.
- Error recovery remains available after load failures, 409 conflicts and uncertain mutation results. Recovery preserves drafts and requires explicit resubmission, with no automatic mutation retry or hidden version overwrite.

## v3 real-workflow regression — not executed

- Use both actual 孟婆 workbooks: master has 31 sheets/209 beats; episode 26 master has 7 beats, independent revision has 8. Never infer row identity or move comments solely from beat number or row position. Opening a revision leaves originals/old discussions accessible.
- Each scene retains all six member-note labels and source cells, including blank values. Nonblank imported notes do not acquire a fabricated logged-in author. Imported original text and other worksheets/columns remain intact.
- Save breakdown plan and red/yellow/green level with expected_version. Another member's edit must produce409 without discarding the user's draft. Original story text remains read-only; do not recompute manual difficulty from good/bad votes.
- Link multiple source rows explicitly and inspect their original discussions; missing/unreadable/cross-project/self/cyclic links are rejected. Linking does not copy or merge comments. Asset and scene rows with the same ID remain isolated.
- Create/import requires a selected project and operation ID. Unknown result can be retried explicitly with the same operation ID and identical bytes/metadata. Changed payload on the same key returns409. Repeated create/import cannot silently duplicate files.
- Scene discussion requires no picture or vote. Resolved comments show resolution notes; body edits reopen them. A stale resolution request returns409; resolution permission does not grant editing someone else's text.
- Search/select images within the selected project, by name/variant/category, with cursor paging. Next-page items remain reachable; a failed page is not treated as an empty final page. Metadata is visible before thumbnails finish; stale search results cannot replace a later search.
- Real 26,910,388-byte 幽冥道.png fits the default64MiB upload limit. Validate actual contents; larger-than-limit/chunked/short-body inputs reject without allocating unbounded memory or publishing partial files. WebP previews stay WPF-decodable.
- Compare2–4 images and zoom/pan; preserve aspect ratio and distinguish names/variants. Comparison does not mutate selections or votes. Selecting a candidate keeps candidates and records selection actor/time/previous/new image/reason atomically; candidates-only changes do not create false selection events.
- Open table text before loading every image; switching project/file/sheet during async work cannot apply stale images/statistics to the new target. Verify real107-image sample interactively when running is allowed; static source review does not prove performance.
- Export current workbook with expected snapshot version: saved plans, levels, legacy notes, pictures and review discussion must appear in the agreed workbook layout; unknown source sheets and columns remain. Missing exporter returns503, never the original disguised as current. Verify original download hash still matches original input.
- Failed/cancelled download/export preserves any pre-existing destination and removes only its own temporary file. Byte limits and deadlines cover the full transfer. Host increments/invalidates file snapshot version for every export-affecting transaction.
- Top episode switcher, query/level filters, row detail, legacy notes and source context work at narrow widths without hiding required fields. Dark/light resources continue following the host.

The user prohibited running the application while another AI refactors it. No build, test server, migration, or GUI run was performed for this package. Run these checks only after that restriction is lifted.

## Compile and wiring

- Build the isolated WPF class library using .NET 8 Windows, then build the host after integration. Resolve XAML bindings/resources and nullable/compiler diagnostics.
- Navigation entry is after “其他部门”, not between “文件” and “传输”. Existing navigation, theme switching, and files continue working.
- Host composes authenticated service, view model and view; page activation loads files. API route ownership and relative BaseAddress agree.
- Verify source byte validators, backend ACL/storage/template adapters and row image store have real implementations. No success response may be returned by a placeholder adapter.

## File and template flow

- Drop one/multiple actual .xlsx files onto the project list; no conversion wizard; original filenames remain visible. Mixed non-xlsx input yields an explicit error and does not import silently as another type.
- New menu has exactly the two approved templates. Each creates an actual empty xlsx, with all expected original template fields, not merely a JSON stub.
- Original file bytes remain downloadable. Verify hash against uploaded bytes; distinguish original download from export of current image edits.
- Open all worksheets, including extra/unrecognized sheets/columns. No data disappears when converting to business rows. values_only mode shows its concise notice.
- Import corrupt ZIP, huge decompressed workbook, unsupported encrypted workbook: reject safely, retain any previously saved project state.

## Images and concurrency

- Choose one backend image as selected; switch it; clear it; verify authoritative reload after each mutation.
- Candidates: select/upload multiple, preserve order, remove one, set candidate as selected while retaining candidate reference.
- Drop two images onto selected: reject explicitly without silently discarding one. Drop xlsx onto an image cell: do not trigger project import. Image-cell drops do not bubble to parent.
- Image upload success followed by relationship failure leaves previous row relationships intact and displays a safe error. Retrying does not delete unrelated media.
- Two clients read version N; first PUT returns N+1; second PUT at N returns 409 without overwriting.
- A member cannot list/read/associate another member's unauthorized media or edit a read-only workbook. Authorization applies to original download, thumbnails, full images, and row mutations.
- Session token expiry/refresh works for JSON and image requests. No external image URLs bypass the authenticated service.
- PNG/JPEG/WebP decoding works end-to-end. If WPF lacks WebP support, thumbnail adapter supplies PNG/JPEG or explicit decoder is wired; do not swallow failure as an empty image.

## Layout

- Both templates at host window widths 1280, 1366, 1920 and relevant DPI settings: complete column labels, readable body text, no horizontal scrollbar in template views.
- Narrow view reflows records vertically; all labels/data/image actions remain accessible. Long filenames and Chinese text wrap without forcing the window wider.
- One selected image remains distinguishable from multiple candidates. Images retain aspect ratio; previews show full image; large candidate sets remain accessible vertically.
- Dark and light theme: no black text on dark cell backgrounds; hover, focus, disabled, selected and errors remain readable.
- Switching files/sheets during loads, returning home, replacing DataContext and closing views does not apply stale image responses to another record.

## Persistence

- Restart server and reload: file identities, sheet ordering, stable row IDs and image references persist.
- Renaming/moving a workbook does not change row-media identity; deleting references never deletes source assets.
- Compatibility migration from boolean/string selected/alternative fields is explicit; ambiguous values do not become guessed image IDs.

## v2 good/bad and comments — not executed

- Two users vote on the same row/image: one vote per user, good/bad totals accurate; switching good to bad removes old vote; withdrawing changes neither comments nor other members' votes.
- User can submit a comment without ever voting. Multiple comments append chronologically instead of overwriting the user's previous comment.
- Only author sees edit/delete capability; forged edits/deletes by another actor are rejected by backend, including requests for a comment ID from a different file or row.
- Stale comment edit/delete returns 409 and keeps current content. Text draft is retained after network errors/conflict; do not blindly retry POST or discard user input.
- General asset discussion has comments but no vote controls or image dependency. Unauthorized reviewers are rejected even if the file is readable; authorized reviewers need not have permission to replace asset images.
- Candidate to selected preserves votes/comments. Replacing image A with B does not transfer A's votes. Same media reused in another project/row has a separate review scope.
- Removing a candidate reference does not delete its historical review records. Restoring the same image association recovers its prior review scope subject to current ACL.
- Image-load failure does not prevent reading or submitting accessible text comments. Review-load failure is not displayed as zero votes; rating/comment buttons must not operate against an unknown target.
- Summary and thread scopes remain fixed across asynchronous loads; closing a dialog or switching worksheets cannot post a draft to a different image/row.
- API DELETE comment request with no body works with stdlib HTTP handler; JSON POST/PUT still send Content-Length, not chunked.
- All new migration SQL remains unapplied until explicitly integrated; restarting server preserves votes/comments and authorship.
