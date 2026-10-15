"""Environment-backed V2 service configuration."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    bind_host: str
    port: int
    data_dir: Path
    database_path: Path
    tls_certificate: Path | None
    tls_private_key: Path | None
    update_releases_dir: Path | None
    installer_dir: Path | None
    transfer_base_url: str | None = None
    transfer_ticket_secret: str | None = None
    media_worker_token: str | None = None


def load_settings() -> Settings:
    data_dir = Path(os.environ.get("MUYING_V2_DATA_DIR", "backend/data")).resolve()
    certificate = os.environ.get("MUYING_V2_TLS_CERT")
    private_key = os.environ.get("MUYING_V2_TLS_KEY")
    update_releases_dir = os.environ.get("MUYING_V2_UPDATE_RELEASES_DIR")
    installer_dir = os.environ.get("MUYING_V2_INSTALLER_DIR")
    transfer_base_url = os.environ.get("MUYING_V2_TRANSFER_BASE_URL")
    transfer_ticket_secret = os.environ.get("MUYING_V2_TRANSFER_TICKET_SECRET")
    media_worker_token = os.environ.get("MUYING_V2_MEDIA_WORKER_TOKEN")
    return Settings(
        bind_host=os.environ.get("MUYING_V2_BIND_HOST", "127.0.0.1"),
        port=int(os.environ.get("MUYING_V2_PORT", "8443")),
        data_dir=data_dir,
        database_path=Path(os.environ.get("MUYING_V2_DB", str(data_dir / "muying-v2.db"))),
        tls_certificate=Path(certificate) if certificate else None,
        tls_private_key=Path(private_key) if private_key else None,
        # Keep the original path so the release store can reject symlinked roots.
        update_releases_dir=Path(update_releases_dir).absolute() if update_releases_dir else None,
        # Same treatment: an unset directory means the download routes answer 503.
        installer_dir=Path(installer_dir).absolute() if installer_dir else None,
        transfer_base_url=transfer_base_url,
        transfer_ticket_secret=transfer_ticket_secret,
        media_worker_token=media_worker_token,
    )
