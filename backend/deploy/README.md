# V2 service deployment notes

These files are source templates. This work did not install, restart, or remove anything on a
NAS. Production deployment and retirement of previously installed units are separate actions.

## Services

- `muying-v2.service` serves the authenticated API and stores application users, sessions,
  projects, workbooks, file metadata, and change events in SQLite. Employee accounts are
  application records only; registration does not create Unix/Samba accounts or home folders.
- The Rust transfer service is the only employee-file path to the private NAS object store.
  It uses a dedicated service identity and signed tickets from the API. WPF and the API do not
  mount or write SMB shares.
- Visitor sessions are short-lived in-memory read-only tokens. They use the same SQLite-backed
  project and file APIs as signed-in users; they do not mount SMB or run a separate NAS reader.
- There is no project/account root-helper unit in the current source tree.

## Environment and data

- `/var/lib/muying-v2/` contains the API SQLite databases and WAL files; the API service owns
  this directory with mode `0700`.
- `/etc/muying-v2/server.env` configures the listener, database, transfer-service URL and HMAC
  ticket secret, optional Mac media-worker token, updater releases, and installer downloads.
- The Rust transfer service has its own environment and restricted object/staging roots. Keep
  its HMAC secret consistent with the API and outside the repository.
- TLS private keys and transfer secrets must be provisioned outside source control.

The new application-auth database schema does not contain `system_username` or
`provisioning_token`. Starting the new source against an old account-helper schema discards the
old `users` and `sessions` tables and creates a clean application-account schema. This is a
source behavior only; do not run it against a production database as part of code review.

## First deployment outline

1. Review and back up the existing service configuration and data using the operator's normal
   procedure. Do not change legacy hub, NAS user, Samba, or ACL state as part of this release.
2. Stage a reviewed application release and create the non-login API service user plus its
   private SQLite data directory.
3. Configure `server.env`, HTTPS credentials, and the Rust service URL/HMAC secret. Configure
   the transfer daemon's own restricted NAS identity and object/staging roots independently.
4. Install only the reviewed templates required for the release. The API does not require an
   account-provisioner socket or direct SMB permission.
5. Test account registration/login/session restore, file upload/download, interruption and
   cancellation, revisions, deletion/restore, change-feed sync, and visitor read-only access
   to department, public, and project data in an isolated environment before scheduling any
   production deployment.

## Publishing an installer

`GET /download` is unauthenticated so a colleague without an installed client can fetch the
first installer. It serves only the newest correctly named immutable package from
`MUYING_V2_INSTALLER_DIR`. Publish by copying to a temporary name and atomically renaming into
place; optionally include a matching `.sha256` file. The API reads the directory per request, so
publishing a package does not require a service restart.
