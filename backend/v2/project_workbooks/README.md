# project_workbooks — what is vendored, what the host supplies

## Provenance

Two files are copied **verbatim** from the source-handoff package:

| File | Source |
|---|---|
| `projects_api.py` | `codex/project-workbooks` @ `8ad6ba2`, `handoff/project-workbooks/backend/projects_api.py` |
| `workflow_services.py` | same commit, `handoff/project-workbooks/backend/workflow_services.py` |

They are kept byte-identical on purpose: the package was delivered as a frozen snapshot, and
keeping them unchanged means a future diff against the package is one command rather than a
review. **Do not edit them.** Host behaviour goes in the adapters below, or in
`../project_workbooks_http.py`.

## What was deliberately not taken

`workflow_store.py`, `image_relations.py`, `review_store.py`, `migrate_reviews_v2_to_v3.sql`,
`images_schema.sql`, `reviews_schema.sql`, `reviews_v3_schema.sql`, `workflow_schema.sql`.

They implement editing, review and image-state storage — none of which the read-only first cut
uses — and they were never compiled or run by their author. When an editing cut arrives, bring
them in with the schema work they need rather than copying them in as decoration.

## What this package needs from the host

`projects_api.py` defines the routes and the JSON shapes and nothing else: it hands every
question to protocols it is given. Four things must exist before anything works.

| Protocol | Where | Host implementation |
|---|---|---|
| `Security` | `projects_api.py` | `nas_adapter.NasSecurity` |
| `WorkbookServices` (owns `document`) | `projects_api.py` | `nas_adapter.NasWorkbookServices` |
| `WorkflowServices` (owns `projects`, `list_files`) | `workflow_services.py` | `nas_adapter.NasWorkflowServices` |
| the xlsx projector | — | `projector.py` |

Two protocols, not one: `document` is on `WorkbookServices` and `projects`/`list_files` are on
`WorkflowServices`, and `ProjectsApi` takes the former as a required positional. There is no
configuration in which "open a workbook" is served by the workflow adapter.

Passing `None` where a protocol belongs is the package's own way of saying "not implemented" —
the routes then answer 503 `STORAGE_UNAVAILABLE`. That is what the eleven methods this cut does
not implement do, and it is correct behaviour rather than a gap.

## Scope of the first cut

Read-only: **choose a project → list its workbook files → open one and read every sheet.**
Creating templates, importing revisions and exporting are out of scope and answer 503.

`do_PUT` is not wired into the host: this cut registers no mutating route, and the client issues
only GETs for these three calls.
