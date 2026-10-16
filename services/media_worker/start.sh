#!/bin/zsh
# Service-driven Mac M4 thumbnail worker. The bearer token is supplied by launchd
# or the operator's protected environment and is never written to this repository.
export PATH=/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin

: "${MUYING_V2_API_BASE_URI:?set MUYING_V2_API_BASE_URI to the V2 HTTPS API origin}"
: "${MUYING_V2_MEDIA_WORKER_TOKEN:?set MUYING_V2_MEDIA_WORKER_TOKEN in the protected launch environment}"
: "${MUYING_V2_CERT_SHA256:?set MUYING_V2_CERT_SHA256 to the pinned NAS leaf certificate SHA-256}"
: "${MUYING_V2_CA_FILE:?set MUYING_V2_CA_FILE to the bundled NAS CA certificate}"

SCRIPT_DIR="${0:A:h}"
WORKER="${MUYING_MEDIA_WORKER:-$SCRIPT_DIR/worker.py}"
PYTHON="${MUYING_MEDIA_WORKER_PYTHON:-/usr/bin/python3}"

exec "$PYTHON" -u "$WORKER"
