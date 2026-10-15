"""Employee identity validation shared by the V2 API and account helper."""

from __future__ import annotations

import unicodedata


MAX_USERNAME_CODEPOINTS = 255
MAX_USERNAME_UTF8_BYTES = 768
VALID_DEPARTMENTS = {"film", "writing"}


def normalize_username(raw: object) -> str | None:
    """Normalize a display/SMB login without changing internal whitespace.

    The outer whitespace trim and rejection rules match the confirmed product
    behavior. NFC makes canonically equivalent Unicode names use one account.
    """
    if not isinstance(raw, str):
        return None
    try:
        raw.encode("utf-8")
    except UnicodeEncodeError:
        return None

    username = unicodedata.normalize("NFC", raw).strip()
    if not username or len(username) > MAX_USERNAME_CODEPOINTS:
        return None
    if len(username.encode("utf-8")) > MAX_USERNAME_UTF8_BYTES:
        return None
    if any(unicodedata.category(char) == "Cc" or char in {"/", "\\"} for char in username):
        return None
    return username


def username_key(username: str) -> str:
    """Return a collision key consistent with Samba's case-insensitive mapping."""
    return unicodedata.normalize("NFC", username).casefold()


def valid_department(value: object) -> bool:
    return isinstance(value, str) and value in VALID_DEPARTMENTS


def valid_internal_username(value: object) -> bool:
    """Validate the generated POSIX account name accepted by the helper."""
    if not isinstance(value, str) or len(value) > 32:
        return False
    if not value.startswith("myv2_"):
        return False
    suffix = value[5:]
    return len(suffix) == 24 and all(char in "0123456789abcdef" for char in suffix)
