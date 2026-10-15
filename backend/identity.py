"""Application identity validation for the V2 API."""

from __future__ import annotations

import unicodedata


MAX_USERNAME_CODEPOINTS = 255
MAX_USERNAME_UTF8_BYTES = 768
VALID_DEPARTMENTS = {"film", "writing"}


def normalize_username(raw: object) -> str | None:
    """Normalize a display username without changing internal whitespace.

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
    """Return a case-insensitive application-account collision key."""
    return unicodedata.normalize("NFC", username).casefold()


def valid_department(value: object) -> bool:
    return isinstance(value, str) and value in VALID_DEPARTMENTS
