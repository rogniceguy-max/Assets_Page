# -*- coding: utf-8 -*-
"""Local-only filesystem mock for the MuYing Drive backend.

This module never creates operating-system or Samba accounts and never calls
ACL tools. All directories are confined to DATA_DIR/mock_nas.
"""

from pathlib import Path
from typing import Dict

from config import DATA_DIR


PUBLIC_DIR_NAME = "00_公共素材"
FILM_DIR_NAME = "01_影视资源库"
WRITING_DIR_NAME = "02_文学创作库"
PERSONAL_DIR_NAME = "03_个人空间"


def _create_confined_directory(path: Path, data_root: Path) -> Path:
    """Create a directory while rejecting symlinks and escapes from DATA_DIR."""
    relative_parts = path.relative_to(data_root).parts
    current = data_root
    for part in relative_parts:
        current = current / part
        if current.is_symlink():
            raise ValueError("mock storage path contains a symlink")
        current.mkdir(exist_ok=True)
        resolved = current.resolve()
        if not resolved.is_relative_to(data_root):
            raise ValueError("mock storage path escaped DATA_DIR")
    return current.resolve()


def ensure_local_mock_roots() -> Dict[str, Path]:
    """Create and return the four local mock roots keyed by virtual root name."""
    data_root = Path(DATA_DIR).resolve()
    data_root.mkdir(parents=True, exist_ok=True)
    mock_root = _create_confined_directory(data_root / "mock_nas", data_root)
    roots = {
        PUBLIC_DIR_NAME: mock_root / PUBLIC_DIR_NAME,
        FILM_DIR_NAME: mock_root / FILM_DIR_NAME,
        WRITING_DIR_NAME: mock_root / WRITING_DIR_NAME,
        PERSONAL_DIR_NAME: mock_root / PERSONAL_DIR_NAME,
    }
    for path in roots.values():
        _create_confined_directory(path, data_root)
    return roots


def local_mock_upload_staging_directory() -> Path:
    """Return a hidden, confined directory used only for incomplete uploads."""
    data_root = Path(DATA_DIR).resolve()
    data_root.mkdir(parents=True, exist_ok=True)
    mock_root = _create_confined_directory(data_root / "mock_nas", data_root)
    return _create_confined_directory(mock_root / ".uploads", data_root)


def create_local_mock_personal_directory(user_id: int) -> bool:
    """Create a personal mock directory keyed by the internal database ID."""
    if not isinstance(user_id, int) or user_id <= 0:
        return False

    try:
        personal_root = ensure_local_mock_roots()[PERSONAL_DIR_NAME]
        target = personal_root / str(user_id)
        if target.is_symlink():
            return False
        target.mkdir(exist_ok=False)
        resolved = target.resolve()
        if resolved.parent != personal_root.resolve():
            return False
        return True
    except (OSError, ValueError):
        return False
