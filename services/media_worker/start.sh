#!/bin/zsh
# MuYing Assets Hub - Media Worker Startup Wrapper for macOS
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
NAS_MOUNT="/Users/muying12345/NAS_Assets"

# Ensure NAS SMB share is mounted
if [ ! -d "$NAS_MOUNT/01_影视资源库" ]; then
    mkdir -p "$NAS_MOUNT"
    mount_smbfs //19945022511:Andy66668888@192.168.1.153/Assets "$NAS_MOUNT"
fi

exec /usr/bin/python3 -u /Users/muying12345/media_worker/worker.py
