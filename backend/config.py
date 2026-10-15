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
    provisioner_socket: Path | None
    visitor_socket: Path | None
    project_socket: Path | None
    public_root: Path
    update_releases_dir: Path | None
    installer_dir: Path | None
    project_docs_root: Path | None


def load_settings() -> Settings:
    data_dir = Path(os.environ.get("MUYING_V2_DATA_DIR", "backend/data")).resolve()
    certificate = os.environ.get("MUYING_V2_TLS_CERT")
    private_key = os.environ.get("MUYING_V2_TLS_KEY")
    socket_path = os.environ.get("MUYING_V2_PROVISIONER_SOCKET")
    visitor_socket = os.environ.get("MUYING_V2_VISITOR_SOCKET")
    project_socket = os.environ.get("MUYING_V2_PROJECT_SOCKET")
    update_releases_dir = os.environ.get("MUYING_V2_UPDATE_RELEASES_DIR")
    installer_dir = os.environ.get("MUYING_V2_INSTALLER_DIR")
    project_docs_root = os.environ.get("MUYING_V2_PROJECT_DOCS_ROOT")
    return Settings(
        bind_host=os.environ.get("MUYING_V2_BIND_HOST", "127.0.0.1"),
        port=int(os.environ.get("MUYING_V2_PORT", "8443")),
        data_dir=data_dir,
        database_path=Path(os.environ.get("MUYING_V2_DB", str(data_dir / "muying-v2.db"))),
        tls_certificate=Path(certificate) if certificate else None,
        tls_private_key=Path(private_key) if private_key else None,
        provisioner_socket=Path(socket_path) if socket_path else None,
        visitor_socket=Path(visitor_socket) if visitor_socket else None,
        project_socket=Path(project_socket) if project_socket else None,
        public_root=Path(os.environ.get("MUYING_V2_PUBLIC_ROOT", "/volume1/Assets")),
        # Keep the original path so the release store can reject symlinked roots.
        update_releases_dir=Path(update_releases_dir).absolute() if update_releases_dir else None,
        # Same treatment: an unset directory means the download routes answer 503.
        installer_dir=Path(installer_dir).absolute() if installer_dir else None,
        # Unset means the project-workbook routes are not served at all. Set it to the
        # directory holding one folder per project.
        project_docs_root=Path(project_docs_root).absolute() if project_docs_root else None,
    )
