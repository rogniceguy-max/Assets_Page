#!/bin/zsh
# MuYing Assets Hub - Media Worker Startup Wrapper for macOS
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin

# Where the share gets mounted and where the worker code lives. Both default to the
# deploying account's home directory so this file carries no machine-specific path.
NAS_MOUNT="${MUYING_NAS_MOUNT:-$HOME/NAS_Assets}"
WORKER="${MUYING_MEDIA_WORKER:-$HOME/media_worker/worker.py}"

# The NAS credentials come from the environment and deliberately have NO defaults.
#
# This file used to carry a live NAS account name and password in plain text, which is
# exactly how they ended up in a public repository. The rule that keeps that from
# happening again is that an unset variable must STOP the worker: a fallback value,
# even a "safe" one, is how a credential ends up written down somewhere again.
#
# Prefer a dedicated, non-administrator account here — this worker only needs to read
# media and write into .muying_cache/.
: "${MUYING_NAS_USER:?set MUYING_NAS_USER (a dedicated, non-admin NAS account)}"
: "${MUYING_NAS_PASSWORD:?set MUYING_NAS_PASSWORD}"
: "${MUYING_NAS_HOST:?set MUYING_NAS_HOST (the NAS address)}"
: "${MUYING_NAS_SHARE:=Assets}"

# Ensure NAS SMB share is mounted
if [ ! -d "$NAS_MOUNT/01_影视资源库" ]; then
    mkdir -p "$NAS_MOUNT"
    # mount_smbfs takes the credentials inside the URL, so the URL itself is a secret.
    # Export MUYING_NAS_SMB_URL directly if the password contains characters that are
    # special in a URL (@ : / ? #), which would otherwise need percent-encoding.
    smb_url="${MUYING_NAS_SMB_URL:-//${MUYING_NAS_USER}:${MUYING_NAS_PASSWORD}@${MUYING_NAS_HOST}/${MUYING_NAS_SHARE}}"
    if ! mount_smbfs "$smb_url" "$NAS_MOUNT" 2>"$NAS_MOUNT/.mount-error"; then
        # Scrub the password before anything reads this: launchd points this script's
        # stderr at a log file, and a mount failure echoes the URL it was given.
        printf '无法挂载 NAS 共享，请检查 MUYING_NAS_* 环境变量。挂载工具的输出：\n' >&2
        sed "s#${MUYING_NAS_PASSWORD}#<redacted>#g" "$NAS_MOUNT/.mount-error" >&2
        rm -f "$NAS_MOUNT/.mount-error"
        exit 1
    fi
    rm -f "$NAS_MOUNT/.mount-error"
fi

exec /usr/bin/python3 -u "$WORKER"
