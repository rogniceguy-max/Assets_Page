# Parallel V2 NAS service

These units install a separate API and account helper. They do not replace or
modify `muying-hub.service`, its code, or its database.

## Release layout

- `/opt/muying-v2/releases/<release-id>/` contains this repository's `backend/` tree.
- `/opt/muying-v2/current` points to the selected release.
- `/var/lib/muying-v2/` contains the independent SQLite database and WAL.
- `/etc/muying-v2/server.env` and `helper.env` contain non-secret settings.
- `/etc/muying-v2/server.crt` and `server.key` are the V2-only TLS certificate.
- `/etc/samba/muying-v2-users.map` and `.json` are owned by the root helper.

## First installation outline

1. Back up the current Samba configuration, share ACLs, and the contents of
   `/etc/systemd/system/muying-hub.service`; do not edit the legacy unit or DB.
2. Stage a reviewed release under `/opt/muying-v2/releases/`, create the
   non-login `muying-v2` service user, and create `/var/lib/muying-v2` as
   `muying-v2:muying-v2` mode `0700`.
3. Create `/etc/muying-v2/`, install the example environment files with mode
   `0600`, and set the fixed LAN bind address. Generate a V2-specific TLS leaf
   certificate with that address in its IP subject alternative name. Keep the
   private key root-owned mode `0600`; the API receives it using systemd
   credentials. Pin the public certificate in the Windows client.
4. Install these three units into `/etc/systemd/system/`, run
   `systemd-analyze verify` against them, then enable only
   `muying-v2.service` and `muying-v2-account.socket`.
5. Before enabling registration, validate the Samba username-map file syntax
   and effective share configuration with `testparm`; preserve the old
   `valid users` and `write list` entries while adding the V2 groups.
6. Reload Samba only after the backed-up config passes `testparm`. Apply the
   separately reviewed minimal ACL changes, then verify them with `getfacl`.

The privileged helper uses one systemd socket-activated process per request.
It verifies the connecting service UID, accepts only create, transaction-scoped
rollback, and transaction-status queries, and serializes all manifest/account
changes with an advisory lock. Status queries verify the Unix account, Samba
passdb entry, department group, private directory, and published username map
before startup recovery can activate an account. The helper never accepts
shell commands.

The V2 API has no file proxy; the native client uses the Windows SMB redirector
for file bytes. The 30-day transfer-checkpoint cleanup timer is intentionally
not included until the Windows transfer journal and account scoping are in
place.
