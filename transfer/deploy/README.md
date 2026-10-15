# NAS transfer daemon deployment template

This directory contains templates only. Nothing here installs or starts a service.

Build the daemon with the pinned Rust 1.85 toolchain for the NAS CPU architecture
after confirming whether the NAS runs x86_64 or aarch64. The current template
does not assume either architecture.

Run the process as the dedicated unprivileged muying-transfer account. Give that
account write access only to the object, staging, and state directories. Keep
the staging and object roots on the same filesystem so publishing a verified
upload can use an atomic rename.

The daemon binds to loopback by default. Put it behind the deployment TLS
reverse proxy and expose only the configured transfer routes. Set
MUYING_V2_TRANSFER_TICKET_SECRET to the exact secret used by the Python API;
store its real value in a protected environment file, not in this template.

The daemon owns its small state database and never opens the Python API SQLite
database. The API remains responsible for users, metadata, operation state,
and receipt validation.
