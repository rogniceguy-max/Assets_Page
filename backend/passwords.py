"""Self-describing scrypt password verification for the isolated V2 database."""

from __future__ import annotations

import base64
import hashlib
import hmac
import secrets


SCRYPT_N = 16384
SCRYPT_R = 8
SCRYPT_P = 1
SCRYPT_DKLEN = 64
SCRYPT_SALT_BYTES = 16
MIN_PASSWORD_LENGTH = 8


def hash_password(password: str) -> str:
    if not isinstance(password, str) or len(password) < MIN_PASSWORD_LENGTH:
        raise ValueError("密码至少需要 8 位。")
    if "\n" in password or "\r" in password:
        raise ValueError("密码不能包含换行符。")
    salt = secrets.token_bytes(SCRYPT_SALT_BYTES)
    digest = hashlib.scrypt(
        password.encode("utf-8"),
        salt=salt,
        n=SCRYPT_N,
        r=SCRYPT_R,
        p=SCRYPT_P,
        dklen=SCRYPT_DKLEN,
    )
    return "$".join(
        (
            "scrypt",
            f"N={SCRYPT_N},r={SCRYPT_R},p={SCRYPT_P},dklen={SCRYPT_DKLEN}",
            base64.b64encode(salt).decode("ascii"),
            base64.b64encode(digest).decode("ascii"),
        )
    )


def verify_password(password: str, encoded: str) -> bool:
    if not isinstance(password, str) or not isinstance(encoded, str):
        return False
    try:
        algorithm, params, salt_text, digest_text = encoded.split("$")
        if algorithm != "scrypt" or params != f"N={SCRYPT_N},r={SCRYPT_R},p={SCRYPT_P},dklen={SCRYPT_DKLEN}":
            return False
        salt = base64.b64decode(salt_text, validate=True)
        expected = base64.b64decode(digest_text, validate=True)
        if len(salt) != SCRYPT_SALT_BYTES or len(expected) != SCRYPT_DKLEN:
            return False
        actual = hashlib.scrypt(
            password.encode("utf-8"),
            salt=salt,
            n=SCRYPT_N,
            r=SCRYPT_R,
            p=SCRYPT_P,
            dklen=SCRYPT_DKLEN,
        )
        return hmac.compare_digest(actual, expected)
    except (ValueError, TypeError, UnicodeEncodeError):
        return False
