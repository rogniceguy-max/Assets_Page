#!/usr/bin/env python3
"""Service-driven Mac media worker for revision-bound 640px WebP thumbnails.

The worker never mounts SMB shares and never scans logical paths. It receives a
revision-specific job from the V2 API, downloads the original through the ticketed
transfer service, generates a WebP locally with FFmpeg (VideoToolbox first for
video), and publishes the derivative through the same transfer service.
"""

from __future__ import annotations

import hashlib
import functools
import hmac
import http.client
import json
import logging
import os
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any, Callable


MAX_JSON_BYTES = 1024 * 1024
MAX_TRANSFER_CHUNK_BYTES = 64 * 1024 * 1024
BUNDLED_NAS_CA_SHA256 = "4790b534847c67bce7f3a6b82d66518ee1bc3c37c97dff1edab446445de883d5"
MAX_ATTEMPTS = 3
MAX_DOWNLOAD_CACHE_BYTES = 4 * 1024 * 1024 * 1024
MAX_DOWNLOAD_CACHE_AGE_SECONDS = 14 * 24 * 60 * 60
JOB_RETRY_BASE_SECONDS = 5 * 60
JOB_RETRY_MAX_SECONDS = 24 * 60 * 60
JOB_RETRY_STATE_MAX_ROWS = 4096
JOB_RETRY_STATE_TTL_SECONDS = 30 * 24 * 60 * 60
SAFE_ID_RE = re.compile(r"^[A-Za-z0-9._-]{1,128}$")
RETRYABLE_HTTP_STATUSES = {408, 425, 429, 500, 502, 503, 504}


class WorkerError(Exception):
    """A safe-to-log worker error that never contains request secrets or URLs."""


class RetryableError(WorkerError):
    pass


class HttpStatusError(WorkerError):
    def __init__(self, status: int, *, retryable: bool = False):
        super().__init__(f"HTTP {status}")
        self.status = status
        self.retryable = retryable


class CertificatePinMismatch(ssl.SSLError):
    pass


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        # Authenticated API requests and transfer tickets must not be forwarded to
        # a redirect target. A deployment must configure the final HTTPS URL.
        return None


class PinnedHTTPSConnection(http.client.HTTPSConnection):
    def __init__(self, *args: Any, expected_pin: bytes | None, **kwargs: Any):
        # urllib passes this to its HTTPSConnection factory, while http.client's
        # constructor uses the equivalent setting already held by the SSLContext.
        kwargs.pop("check_hostname", None)
        super().__init__(*args, **kwargs)
        self._expected_pin = expected_pin

    def connect(self) -> None:
        super().connect()
        certificate = self.sock.getpeercert(binary_form=True) if self.sock is not None else None
        if self._expected_pin is not None and not _certificate_matches_pin(certificate, self._expected_pin):
            self.close()
            raise CertificatePinMismatch("NAS TLS certificate pin mismatch")


class PinnedHTTPSHandler(urllib.request.HTTPSHandler):
    def __init__(self, context: ssl.SSLContext, expected_pin: bytes | None):
        super().__init__(context=context)
        self._expected_pin = expected_pin

    def https_open(self, request: urllib.request.Request):
        connection = functools.partial(PinnedHTTPSConnection, expected_pin=self._expected_pin)
        return self.do_open(connection, request, context=self._context,
                            check_hostname=self._context.check_hostname)


def _parse_sha256_pin(fingerprint: str) -> bytes:
    normalized = "".join(character for character in fingerprint if character in "0123456789abcdefABCDEF")
    if len(normalized) != 64 or any(
            character not in "0123456789abcdefABCDEF:- " for character in fingerprint):
        raise WorkerError("MUYING_V2_CERT_SHA256 must be a 32-byte SHA-256 fingerprint")
    try:
        return bytes.fromhex(normalized)
    except ValueError as exc:
        raise WorkerError("MUYING_V2_CERT_SHA256 is invalid") from exc


def _certificate_matches_pin(certificate: bytes | None, expected_pin: bytes) -> bool:
    return certificate is not None and hmac.compare_digest(
        hashlib.sha256(certificate).digest(), expected_pin)


def _create_tls_context(ca_file: Path) -> ssl.SSLContext:
    try:
        if ca_file.is_symlink() or not ca_file.is_file() or ca_file.stat().st_size > MAX_JSON_BYTES:
            raise WorkerError("MUYING_V2_CA_FILE must point to a regular NAS CA certificate")
        ca_contents = ca_file.read_bytes()
        if not hmac.compare_digest(hashlib.sha256(ca_contents).hexdigest(), BUNDLED_NAS_CA_SHA256):
            raise WorkerError("MUYING_V2_CA_FILE does not match the bundled NAS CA certificate")
        # Load the exact bytes that were fingerprinted so a path replacement
        # between validation and SSL setup cannot change the trust anchor.
        tls_context = ssl.create_default_context(cadata=ca_contents.decode("ascii"))
    except WorkerError:
        raise
    except (OSError, ssl.SSLError, UnicodeDecodeError) as exc:
        raise WorkerError("the configured NAS CA certificate could not be loaded") from exc
    if tls_context.verify_mode != ssl.CERT_REQUIRED or not tls_context.check_hostname:
        raise WorkerError("NAS TLS certificate and hostname verification must remain enabled")
    return tls_context


def _json_log(level: int, event: str, **fields: Any) -> None:
    record = {"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "level": logging.getLevelName(level),
              "event": event}
    record.update(fields)
    logger.log(level, json.dumps(record, ensure_ascii=False, separators=(",", ":")))


def configure_logging() -> logging.Logger:
    global logger
    logger = logging.getLogger("MediaWorker")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    if logger.handlers:
        return logger

    log_dir = Path.home() / "Library" / "Logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    formatter = logging.Formatter("%(message)s")
    file_handler = RotatingFileHandler(log_dir / "media_worker.log", maxBytes=10 * 1024 * 1024,
                                       backupCount=3, encoding="utf-8")
    file_handler.setFormatter(formatter)
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setFormatter(formatter)
    logger.addHandler(file_handler)
    logger.addHandler(console_handler)
    return logger


logger = logging.getLogger("MediaWorker")


def _origin(url: str) -> tuple[str, str, int | None]:
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme.lower() != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise WorkerError("endpoint must be an HTTPS URL without embedded credentials")
    return parsed.scheme.lower(), parsed.hostname.lower(), parsed.port


def _resolve_url(base_url: str, value: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise WorkerError("service returned an empty endpoint URL")
    resolved = urllib.parse.urljoin(base_url.rstrip("/") + "/", value)
    _origin(resolved)
    return resolved


def _json_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _read_json(response: Any) -> dict[str, Any]:
    length = response.headers.get("Content-Length")
    if length:
        try:
            if int(length) > MAX_JSON_BYTES:
                raise WorkerError("service JSON response exceeded the size limit")
        except ValueError as exc:
            raise WorkerError("service returned an invalid response length") from exc
    body = response.read(MAX_JSON_BYTES + 1)
    if len(body) > MAX_JSON_BYTES:
        raise WorkerError("service JSON response exceeded the size limit")
    if not body:
        return {}
    try:
        parsed = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkerError("service returned invalid JSON") from exc
    if not isinstance(parsed, dict):
        raise WorkerError("service returned an invalid JSON object")
    return parsed


def _open_request(request: urllib.request.Request, timeout: float = 60.0, *,
                  context: ssl.SSLContext, expected_pin: bytes | None = None):
    opener = urllib.request.build_opener(NoRedirectHandler(), PinnedHTTPSHandler(context, expected_pin))
    try:
        return opener.open(request, timeout=timeout)
    except urllib.error.HTTPError as exc:
        status = int(exc.code)
        try:
            exc.close()
        except Exception:
            pass
        raise HttpStatusError(status, retryable=status in RETRYABLE_HTTP_STATUSES) from None
    except urllib.error.URLError as exc:
        if isinstance(exc.reason, CertificatePinMismatch):
            raise WorkerError("NAS TLS certificate pin mismatch") from None
        # Do not include exception text; it can contain the full endpoint URL.
        raise RetryableError(type(exc.reason).__name__) from None
    except (TimeoutError, ConnectionError, OSError) as exc:
        # Do not include exception text; it can contain the full endpoint URL.
        raise RetryableError(type(exc).__name__) from None


def _retry(operation: Callable[[], Any], *, attempts: int, event: str,
           file_id: str | None = None, revision_id: str | None = None) -> Any:
    count = max(1, min(int(attempts), MAX_ATTEMPTS))
    for attempt in range(1, count + 1):
        try:
            return operation()
        except HttpStatusError as exc:
            retryable = exc.retryable
            error = f"HTTP {exc.status}"
        except RetryableError as exc:
            retryable = True
            error = str(exc)
        if not retryable or attempt == count:
            raise WorkerError(error)
        delay = min(8, 2 ** (attempt - 1))
        _json_log(logging.WARNING, "retry", action=event, file_id=file_id,
                  revision_id=revision_id, attempt=attempt, delay_seconds=delay, error=error)
        time.sleep(delay)


@dataclass(frozen=True)
class WorkerConfig:
    api_base_uri: str
    token: str
    tls_context: ssl.SSLContext
    certificate_sha256: bytes
    worker_id: str
    ffmpeg: str
    ffprobe: str
    temp_root: Path
    poll_interval: float = 10.0
    attempts: int = MAX_ATTEMPTS

    @classmethod
    def from_environment(cls) -> "WorkerConfig":
        api_base_uri = os.environ.get("MUYING_V2_API_BASE_URI", "").strip().rstrip("/")
        token = os.environ.get("MUYING_V2_MEDIA_WORKER_TOKEN", "").strip()
        if not api_base_uri or not token:
            raise WorkerError("MUYING_V2_API_BASE_URI and MUYING_V2_MEDIA_WORKER_TOKEN are required")
        _origin(api_base_uri)
        certificate_sha256 = _parse_sha256_pin(os.environ.get("MUYING_V2_CERT_SHA256", "").strip())
        ca_file_value = os.environ.get("MUYING_V2_CA_FILE", "").strip()
        if not ca_file_value:
            raise WorkerError("MUYING_V2_CA_FILE must point to the bundled NAS CA certificate")
        ca_file = Path(ca_file_value).expanduser()
        tls_context = _create_tls_context(ca_file)
        worker_id = os.environ.get("MUYING_V2_MEDIA_WORKER_ID", "").strip() or str(uuid.uuid4())
        if len(worker_id) > 128 or any(ord(ch) < 32 for ch in worker_id):
            raise WorkerError("MUYING_V2_MEDIA_WORKER_ID is invalid")

        ffmpeg = _resolve_binary("MUYING_MEDIA_WORKER_FFMPEG", ("ffmpeg",),
                                 ("/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"))
        ffprobe = _resolve_binary("MUYING_MEDIA_WORKER_FFPROBE", ("ffprobe",),
                                  ("/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe"))
        poll_interval = _bounded_number(os.environ.get("MUYING_MEDIA_WORKER_POLL_INTERVAL", "10"), 10.0, 1.0, 300.0)
        attempts = int(_bounded_number(os.environ.get("MUYING_MEDIA_WORKER_ATTEMPTS", str(MAX_ATTEMPTS)),
                                       float(MAX_ATTEMPTS), 1, MAX_ATTEMPTS))
        temp_root = Path(os.environ.get("MUYING_MEDIA_WORKER_TEMP", str(
            Path.home() / "Library" / "Caches" / "MuYingDrive" / "MediaWorker"))).expanduser()
        temp_root.mkdir(parents=True, exist_ok=True, mode=0o700)
        try:
            os.chmod(temp_root, 0o700)
        except OSError:
            pass
        return cls(api_base_uri, token, tls_context, certificate_sha256, worker_id, ffmpeg, ffprobe, temp_root,
                   poll_interval=poll_interval, attempts=attempts)


def _bounded_number(value: str, default: float, minimum: float, maximum: float) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return default
    if parsed != parsed or parsed in (float("inf"), float("-inf")):
        return default
    return min(maximum, max(minimum, parsed))


def _resolve_binary(env_name: str, candidates: tuple[str, ...], fallbacks: tuple[str, ...]) -> str:
    configured = os.environ.get(env_name, "").strip()
    if configured:
        resolved = shutil.which(configured) if not os.path.isabs(configured) else configured
        if resolved and os.path.isfile(resolved) and os.access(resolved, os.X_OK):
            return resolved
        raise WorkerError(f"{env_name} does not point to an executable")
    for candidate in candidates:
        found = shutil.which(candidate)
        if found:
            return found
    for candidate in fallbacks:
        if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
            return candidate
    raise WorkerError(f"{env_name} is not available")


class MediaWorker:
    def __init__(self, config: WorkerConfig):
        self.config = config
        self._tls_context = config.tls_context
        self._certificate_sha256 = config.certificate_sha256
        self._job_retry_state: dict[str, dict[str, int]] | None = None
        self._job_retry_state_path = config.temp_root / "job-retry-state.json"

    def _load_job_retry_state(self) -> dict[str, dict[str, int]]:
        if self._job_retry_state is not None:
            return self._job_retry_state
        state: dict[str, dict[str, int]] = {}
        try:
            raw = json.loads(self._job_retry_state_path.read_text(encoding="utf-8"))
            if isinstance(raw, dict):
                for key, value in raw.items():
                    if (isinstance(key, str) and re.fullmatch(r"[0-9a-f]{64}", key)
                            and isinstance(value, dict)
                            and type(value.get("attempts")) is int
                            and type(value.get("retry_after")) is int
                            and type(value.get("updated_at")) is int
                            and value["attempts"] > 0):
                        state[key] = {"attempts": value["attempts"],
                                      "retry_after": value["retry_after"],
                                      "updated_at": value["updated_at"]}
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            pass
        self._job_retry_state = state
        return state

    def _persist_job_retry_state(self) -> None:
        state = self._load_job_retry_state()
        now = int(time.time())
        state = {key: value for key, value in state.items()
                 if value["updated_at"] >= now - JOB_RETRY_STATE_TTL_SECONDS}
        if len(state) > JOB_RETRY_STATE_MAX_ROWS:
            state = dict(sorted(state.items(), key=lambda item: item[1]["updated_at"])
                         [-JOB_RETRY_STATE_MAX_ROWS:])
        self._job_retry_state = state
        self.config.temp_root.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self._job_retry_state_path.with_name(
            self._job_retry_state_path.name + "." + uuid.uuid4().hex + ".tmp")
        try:
            with temporary.open("w", encoding="utf-8") as stream:
                json.dump(state, stream, separators=(",", ":"))
                stream.flush()
                os.fsync(stream.fileno())
            try:
                os.chmod(temporary, 0o600)
            except OSError:
                pass
            os.replace(temporary, self._job_retry_state_path)
        except OSError:
            _json_log(logging.WARNING, "job_retry_state_write_failed")
        finally:
            temporary.unlink(missing_ok=True)

    def _job_retry_deferred(self, job: dict[str, Any]) -> bool:
        key = self._job_cache_key(job["file_id"], job["revision_id"])
        entry = self._load_job_retry_state().get(key)
        return entry is not None and int(time.time()) < entry["retry_after"]

    def _record_job_failure(self, job: dict[str, Any]) -> None:
        key = self._job_cache_key(job["file_id"], job["revision_id"])
        state = self._load_job_retry_state()
        attempts = state.get(key, {}).get("attempts", 0) + 1
        delay = min(JOB_RETRY_MAX_SECONDS,
                    JOB_RETRY_BASE_SECONDS * (2 ** min(attempts - 1, 16)))
        now = int(time.time())
        state[key] = {"attempts": attempts, "retry_after": now + delay, "updated_at": now}
        self._persist_job_retry_state()

    def _clear_job_failure(self, job: dict[str, Any]) -> None:
        key = self._job_cache_key(job["file_id"], job["revision_id"])
        state = self._load_job_retry_state()
        if state.pop(key, None) is not None:
            self._persist_job_retry_state()

    def _api_json(self, method: str, path: str, payload: Any | None = None) -> dict[str, Any]:
        url = _resolve_url(self.config.api_base_uri, path)
        if _origin(url) != _origin(self.config.api_base_uri):
            raise WorkerError("API endpoint resolved outside the configured API origin")
        headers = {"Accept": "application/json", "Authorization": f"Bearer {self.config.token}"}
        body = None
        if payload is not None:
            body = _json_bytes(payload)
            headers["Content-Type"] = "application/json"

        def request_once() -> dict[str, Any]:
            request = urllib.request.Request(url, data=body, headers=headers, method=method)
            with _open_request(request, context=self._tls_context,
                               expected_pin=self._certificate_sha256) as response:
                return _read_json(response)

        return _retry(request_once, attempts=self.config.attempts, event=f"api_{method.lower()}")

    def _transfer_json(self, method: str, url: str, payload: Any | None = None,
                       ticket: str | None = None) -> dict[str, Any]:
        _origin(url)
        headers = {"Accept": "application/json"}
        body = None
        if payload is not None:
            body = _json_bytes(payload)
            headers["Content-Type"] = "application/json"
        if ticket:
            headers["X-Transfer-Ticket"] = ticket

        def request_once() -> dict[str, Any]:
            request = urllib.request.Request(url, data=body, headers=headers, method=method)
            with _open_request(request, context=self._tls_context) as response:
                return _read_json(response)

        return _retry(request_once, attempts=self.config.attempts, event=f"transfer_{method.lower()}")

    @staticmethod
    def _validate_job(job: Any) -> dict[str, Any]:
        if not isinstance(job, dict):
            raise WorkerError("job entry is not an object")
        for field in ("file_id", "revision_id", "media_type", "sha256", "download_ticket", "download_url"):
            if not isinstance(job.get(field), str) or not job[field].strip():
                raise WorkerError(f"job is missing {field}")
        for field in ("file_id", "revision_id"):
            if not SAFE_ID_RE.fullmatch(job[field]) or job[field] in (".", ".."):
                raise WorkerError(f"job has an invalid {field}")
        if not isinstance(job.get("size"), int) or job["size"] <= 0:
            raise WorkerError("job has an invalid source size")
        if not job["media_type"].lower().startswith(("image/", "video/")):
            raise WorkerError("job media_type is not a supported image or video")
        if len(job["sha256"]) != 64 or not all(ch in "0123456789abcdefABCDEF" for ch in job["sha256"]):
            raise WorkerError("job has an invalid source SHA-256")
        return job

    def _job_cache_key(self, file_id: str, revision_id: str) -> str:
        return hashlib.sha256(f"{file_id}\0{revision_id}".encode("utf-8")).hexdigest()

    def _cleanup_download_cache(self) -> None:
        download_dir = self.config.temp_root / "downloads"
        try:
            with os.scandir(download_dir) as entries:
                cached = [entry for entry in entries
                          if entry.is_file(follow_symlinks=False)
                          and (entry.name.endswith(".source") or entry.name.endswith(".source.part"))]
        except FileNotFoundError:
            return
        except OSError as exc:
            _json_log(logging.WARNING, "download_cache_scan_failed", error=type(exc).__name__)
            return

        now = time.time()
        file_rows: list[tuple[str, int, float]] = []
        total_bytes = 0
        for entry in cached:
            try:
                stat = entry.stat(follow_symlinks=False)
            except OSError:
                continue
            total_bytes += stat.st_size
            file_rows.append((entry.path, stat.st_size, stat.st_mtime))

        for path, size, modified in sorted(file_rows, key=lambda item: item[2]):
            if now - modified <= MAX_DOWNLOAD_CACHE_AGE_SECONDS and total_bytes <= MAX_DOWNLOAD_CACHE_BYTES:
                continue
            try:
                os.unlink(path)
                total_bytes -= size
            except OSError:
                continue

    def _download_job_source(self, job: dict[str, Any]) -> Path:
        file_id, revision_id = job["file_id"], job["revision_id"]
        key = self._job_cache_key(file_id, revision_id)
        download_dir = self.config.temp_root / "downloads"
        download_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
        try:
            os.chmod(download_dir, 0o700)
        except OSError:
            pass
        completed = download_dir / f"{key}.source"
        partial = download_dir / f"{key}.source.part"
        expected_size = job["size"]
        expected_hash = job["sha256"].lower()

        if completed.is_file():
            if completed.stat().st_size == expected_size and _sha256_file(completed) == expected_hash:
                return completed
            completed.unlink(missing_ok=True)

        download_url = _resolve_url(self.config.api_base_uri, job["download_url"])
        init = self._transfer_json("POST", download_url, {"ticket": job["download_ticket"]})
        download_id = init.get("download_id")
        chunk_size = init.get("chunk_size")
        if not isinstance(download_id, str) or not SAFE_ID_RE.fullmatch(download_id):
            raise WorkerError("transfer service returned an invalid download_id")
        if not isinstance(chunk_size, int) or chunk_size < 1 or chunk_size > MAX_TRANSFER_CHUNK_BYTES:
            raise WorkerError("transfer service returned an invalid download chunk_size")
        if init.get("size") != expected_size or str(init.get("sha256", "")).lower() != expected_hash:
            raise WorkerError("download ticket metadata does not match the queued revision")

        if partial.exists() and partial.stat().st_size > expected_size:
            partial.unlink()
        current = partial.stat().st_size if partial.exists() else 0
        if current < expected_size:
            required_space = expected_size - current + min(1024 * 1024 * 1024,
                                                           max(64 * 1024 * 1024, expected_size // 50))
            if shutil.disk_usage(download_dir).free < required_space:
                raise WorkerError("insufficient local temporary storage for this source revision")

        download_object_url = urllib.parse.urljoin(download_url.rstrip("/") + "/",
                                                   urllib.parse.quote(download_id, safe=""))
        current = self._download_remaining(download_object_url, job["download_ticket"], partial,
                                           current, expected_size, chunk_size, file_id, revision_id)

        with partial.open("r+b") as stream:
            stream.flush()
            os.fsync(stream.fileno())
        if partial.stat().st_size != expected_size or _sha256_file(partial) != expected_hash:
            partial.unlink(missing_ok=True)
            raise WorkerError("downloaded source failed size or SHA-256 verification")
        os.replace(partial, completed)
        _json_log(logging.INFO, "source_downloaded", file_id=file_id, revision_id=revision_id,
                  bytes=expected_size)
        return completed

    def _download_remaining(self, url: str, ticket: str, partial: Path, start: int, total: int,
                            chunk_size: int, file_id: str, revision_id: str) -> int:
        _origin(url)
        current = start
        if current >= total:
            return current
        read_size = min(max(chunk_size, 64 * 1024), MAX_TRANSFER_CHUNK_BYTES)
        for attempt in range(1, self.config.attempts + 1):
            try:
                request = urllib.request.Request(url, headers={
                    "Accept": "application/octet-stream",
                    "X-Transfer-Ticket": ticket,
                    "Range": f"bytes={current}-",
                }, method="GET")
                with _open_request(request, timeout=180.0, context=self._tls_context) as response:
                    if response.status != 206:
                        raise WorkerError("transfer service did not honor the requested byte range")
                    content_range = response.headers.get("Content-Range", "")
                    if content_range != f"bytes {current}-{total - 1}/{total}":
                        raise WorkerError("transfer service returned a mismatched byte range")
                    mode = "r+b" if partial.exists() else "wb"
                    with partial.open(mode) as stream:
                        stream.seek(current)
                        while current < total:
                            block = response.read(read_size)
                            if not block:
                                break
                            if current + len(block) > total:
                                raise WorkerError("transfer service sent more bytes than the queued source")
                            stream.write(block)
                            current += len(block)
                        stream.flush()
                if current == total:
                    return current
                raise RetryableError("range response ended before the queued source was complete")
            except HttpStatusError as exc:
                retryable = exc.retryable
                error = f"HTTP {exc.status}"
            except RetryableError as exc:
                retryable = True
                error = str(exc)
            if not retryable or attempt == self.config.attempts:
                raise WorkerError(error)
            delay = min(8, 2 ** (attempt - 1))
            _json_log(logging.WARNING, "retry", action="download_range", file_id=file_id,
                      revision_id=revision_id, attempt=attempt, delay_seconds=delay,
                      offset=current, error=error)
            time.sleep(delay)
        return current

    def _probe(self, source: Path, job: dict[str, Any]) -> tuple[float, dict[str, Any]]:
        command = [self.config.ffprobe, "-v", "error", "-print_format", "json", "-show_format",
                   "-show_streams", str(source)]
        try:
            completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                       check=True, timeout=45)
            parsed = json.loads(completed.stdout.decode("utf-8"))
            if not isinstance(parsed, dict):
                raise ValueError("ffprobe output is not an object")
            fmt = parsed.get("format", {})
            duration = float(fmt.get("duration", 0) or 0) if isinstance(fmt, dict) else 0.0
            video = next((item for item in parsed.get("streams", [])
                          if isinstance(item, dict) and item.get("codec_type") == "video"), {})
            return max(0.0, duration), video
        except (OSError, subprocess.SubprocessError, UnicodeDecodeError, json.JSONDecodeError,
                TypeError, ValueError):
            _json_log(logging.WARNING, "ffprobe_failed", file_id=job["file_id"],
                      revision_id=job["revision_id"])
            return 0.0, {}

    def _generate_webp(self, source: Path, output: Path, job: dict[str, Any]) -> None:
        media_type = job["media_type"].lower()
        is_video = media_type.startswith("video/")
        duration, _metadata = self._probe(source, job)
        seek = "0"
        if is_video and duration > 10:
            seek = "3.0"
        elif is_video and duration > 2:
            seek = f"{duration * 0.1:.2f}"

        output_part = output.with_suffix(".part.webp")
        base = [self.config.ffmpeg, "-hide_banner", "-loglevel", "error", "-y"]
        seek_args = ["-ss", seek] if is_video else []
        args = [*base, *seek_args]
        if is_video:
            args.extend(["-hwaccel", "videotoolbox"])
        args.extend(["-i", str(source), "-vframes", "1", "-vf", "scale=640:-2",
                     "-c:v", "libwebp", "-quality", "82", "-f", "webp", str(output_part)])

        try:
            try:
                subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               check=True, timeout=90)
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
                if not is_video:
                    raise WorkerError("FFmpeg could not generate a WebP derivative") from None
                # Keep VideoToolbox as the normal M4 decode path while allowing
                # unsupported codecs to fall back to FFmpeg's software decoder.
                output_part.unlink(missing_ok=True)
                software_args = [*base, *seek_args, "-i", str(source), "-vframes", "1",
                                 "-vf", "scale=640:-2", "-c:v", "libwebp", "-quality", "82",
                                 "-f", "webp", str(output_part)]
                _json_log(logging.WARNING, "videotoolbox_fallback", file_id=job["file_id"],
                          revision_id=job["revision_id"], error=type(exc).__name__)
                try:
                    subprocess.run(software_args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   check=True, timeout=90)
                except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                    raise WorkerError("FFmpeg could not generate a WebP derivative") from None

            if not output_part.is_file() or output_part.stat().st_size < 12:
                raise WorkerError("FFmpeg produced an empty WebP derivative")
            with output_part.open("rb") as stream:
                signature = stream.read(12)
            if signature[:4] != b"RIFF" or signature[8:12] != b"WEBP":
                raise WorkerError("FFmpeg output is not a WebP image")
            os.replace(output_part, output)
        finally:
            output_part.unlink(missing_ok=True)

    def _upload_webp(self, output: Path, job: dict[str, Any]) -> dict[str, Any]:
        file_id, revision_id = job["file_id"], job["revision_id"]
        size = output.stat().st_size
        sha256 = _sha256_file(output)
        request_path = ("api/v2/media-worker/thumbnails/" + urllib.parse.quote(file_id, safe="") + "/" +
                        urllib.parse.quote(revision_id, safe="") + "/operations")
        operation = self._api_json("POST", request_path, {
            "worker_id": self.config.worker_id,
            "size": size,
            "sha256": sha256,
            "media_type": "image/webp",
        })
        operation_id = _required_str(operation, "operation_id")
        object_id = _required_str(operation, "object_id")
        ticket = _required_str(operation, "ticket")
        expected_version = operation.get("expected_version")
        if not isinstance(expected_version, int) or expected_version < 0:
            raise WorkerError("thumbnail operation returned an invalid expected_version")
        transfer_url = _resolve_url(self.config.api_base_uri, _required_str(operation, "transfer_url"))

        upload_url = transfer_url.rstrip("/")
        if not upload_url.endswith("/uploads"):
            upload_url += "/uploads"
        manifest = {
            "op_id": operation_id,
            "object_id": object_id,
            "object_kind": "thumbnail",
            "expected_version": expected_version,
            "size": size,
            "sha256": sha256,
            "media_type": "image/webp",
        }
        init = self._transfer_json("POST", upload_url, {"ticket": ticket, "manifest": manifest})
        upload_id = _required_str(init, "upload_id")
        chunk_size = init.get("chunk_size")
        if not isinstance(chunk_size, int) or chunk_size < 1 or chunk_size > MAX_TRANSFER_CHUNK_BYTES:
            raise WorkerError("transfer service returned an invalid upload chunk_size")
        ranges = init.get("missing_ranges", [])
        if not isinstance(ranges, list):
            raise WorkerError("transfer service returned invalid missing_ranges")
        chunks_url = urllib.parse.urljoin(upload_url.rstrip("/") + "/",
                                         urllib.parse.quote(upload_id, safe="") + "/chunks")
        for byte_range in ranges:
            if not isinstance(byte_range, dict):
                raise WorkerError("transfer service returned an invalid byte range")
            start, end = byte_range.get("start"), byte_range.get("end")
            if not isinstance(start, int) or not isinstance(end, int) or start < 0 or end < start or end >= size:
                raise WorkerError("transfer service returned an out-of-bounds byte range")
            offset = start
            while offset <= end:
                final = min(end, offset + chunk_size - 1)
                with output.open("rb") as source:
                    source.seek(offset)
                    chunk = source.read(final - offset + 1)
                if len(chunk) != final - offset + 1:
                    raise WorkerError("local WebP file changed during upload")
                chunk_hash = hashlib.sha256(chunk).hexdigest()
                self._put_chunk(chunks_url, ticket, offset, final, size, chunk_hash, chunk,
                                file_id, revision_id)
                offset = final + 1

        commit_url = urllib.parse.urljoin(upload_url.rstrip("/") + "/",
                                          urllib.parse.quote(upload_id, safe="") + "/commit")
        transfer_commit = self._transfer_json("POST", commit_url, ticket=ticket)
        receipt = transfer_commit.get("receipt")
        if not isinstance(receipt, dict):
            raise WorkerError("transfer service commit response omitted receipt")
        if receipt.get("op_id") != operation_id or receipt.get("object_id") != object_id or \
                receipt.get("size") != size or str(receipt.get("sha256", "")).lower() != sha256:
            raise WorkerError("transfer service receipt does not match the thumbnail operation")

        api_commit_path = ("api/v2/media-worker/thumbnails/" + urllib.parse.quote(file_id, safe="") + "/" +
                           urllib.parse.quote(revision_id, safe="") + "/operations/" +
                           urllib.parse.quote(operation_id, safe="") + "/commit")
        committed = self._api_json("POST", api_commit_path, {"receipt": receipt})
        return {"operation_id": operation_id, "size": size, "sha256": sha256,
                "state": committed.get("state", "committed")}

    def _put_chunk(self, url: str, ticket: str, start: int, end: int, total: int,
                   chunk_hash: str, chunk: bytes, file_id: str, revision_id: str) -> None:
        _origin(url)

        def request_once() -> None:
            request = urllib.request.Request(url, data=chunk, headers={
                "Accept": "application/json",
                "Content-Type": "application/octet-stream",
                "X-Transfer-Ticket": ticket,
                "Content-Range": f"bytes {start}-{end}/{total}",
                "X-Chunk-SHA256": chunk_hash,
            }, method="PUT")
            with _open_request(request, timeout=180.0, context=self._tls_context) as response:
                if not 200 <= response.status < 300:
                    raise HttpStatusError(response.status, retryable=response.status in RETRYABLE_HTTP_STATUSES)
                response.read(1024)

        _retry(request_once, attempts=self.config.attempts, event="upload_chunk",
               file_id=file_id, revision_id=revision_id)

    def process_job(self, job_payload: Any) -> None:
        job = self._validate_job(job_payload)
        file_id, revision_id = job["file_id"], job["revision_id"]
        started = time.monotonic()
        _json_log(logging.INFO, "job_started", file_id=file_id, revision_id=revision_id,
                  media_type=job["media_type"], size=job["size"])
        source = self._download_job_source(job)
        generation_dir = self.config.temp_root / "generated"
        generation_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
        try:
            os.chmod(generation_dir, 0o700)
        except OSError:
            pass
        with tempfile.TemporaryDirectory(prefix="thumbnail-", dir=generation_dir) as temp_dir:
            output = Path(temp_dir) / "thumbnail.webp"
            self._generate_webp(source, output, job)
            result = self._upload_webp(output, job)
        source.unlink(missing_ok=True)
        (source.parent / f"{self._job_cache_key(file_id, revision_id)}.source.part").unlink(missing_ok=True)
        self._clear_job_failure(job)
        _json_log(logging.INFO, "job_completed", file_id=file_id, revision_id=revision_id,
                  operation_id=result["operation_id"], output_bytes=result["size"],
                  elapsed_ms=round((time.monotonic() - started) * 1000))

    def poll_once(self) -> tuple[bool, bool]:
        self._cleanup_download_cache()
        cursor: str | None = None
        visited_cursors: set[str] = set()
        had_jobs = False
        had_failures = False
        while True:
            query = {"limit": "10"}
            if cursor is not None:
                query["cursor"] = cursor
            response = self._api_json("GET", "api/v2/media-worker/jobs?" + urllib.parse.urlencode(query))
            items = response.get("items", [])
            if not isinstance(items, list):
                raise WorkerError("jobs endpoint returned invalid items")
            if items:
                had_jobs = True
            for raw_job in items:
                try:
                    job = self._validate_job(raw_job)
                except WorkerError as exc:
                    had_failures = True
                    _json_log(logging.ERROR, "invalid_job", error=type(exc).__name__)
                    continue
                if self._job_retry_deferred(job):
                    had_failures = True
                    continue
                succeeded = False
                for attempt in range(1, self.config.attempts + 1):
                    try:
                        self.process_job(job)
                        succeeded = True
                        break
                    except (WorkerError, OSError, subprocess.SubprocessError, ValueError) as exc:
                        # Never log exception text; an HTTP library may include endpoint details.
                        _json_log(logging.ERROR, "job_attempt_failed", file_id=job.get("file_id"),
                                  revision_id=job.get("revision_id"), attempt=attempt,
                                  attempts=self.config.attempts, error=type(exc).__name__)
                        if attempt < self.config.attempts:
                            time.sleep(min(8, 2 ** (attempt - 1)))
                if not succeeded:
                    had_failures = True
                    self._record_job_failure(job)
                    # Keep a small, recent cache so a retry can reuse a verified source,
                    # but never let failed jobs consume unbounded worker disk space.
                    self._cleanup_download_cache()

            next_cursor = response.get("next_cursor")
            if next_cursor is None or next_cursor == "":
                return had_jobs, had_failures
            if not isinstance(next_cursor, str):
                raise WorkerError("jobs endpoint returned an invalid next_cursor")
            if next_cursor in visited_cursors or next_cursor == cursor:
                _json_log(logging.ERROR, "jobs_cursor_repeated")
                return had_jobs, True
            visited_cursors.add(next_cursor)
            cursor = next_cursor

    def run(self) -> None:
        _json_log(logging.INFO, "worker_started", worker_id=self.config.worker_id,
                  concurrency=1, poll_interval_seconds=self.config.poll_interval)
        while True:
            try:
                had_job, had_failure = self.poll_once()
                if had_failure or not had_job:
                    time.sleep(self.config.poll_interval)
                else:
                    # A single worker and a single polled job keep an 8TB HDD workload serial.
                    time.sleep(0.25)
            except KeyboardInterrupt:
                _json_log(logging.INFO, "worker_stopping", worker_id=self.config.worker_id)
                return
            except (WorkerError, OSError, ValueError) as exc:
                _json_log(logging.ERROR, "poll_failed", error=type(exc).__name__)
                time.sleep(min(60, self.config.poll_interval))


def _required_str(data: dict[str, Any], name: str) -> str:
    value = data.get(name)
    if not isinstance(value, str) or not value.strip():
        raise WorkerError(f"response omitted {name}")
    return value


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            block = stream.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    configure_logging()
    try:
        config = WorkerConfig.from_environment()
    except WorkerError as exc:
        _json_log(logging.ERROR, "configuration_failed", error=str(exc))
        return 2
    MediaWorker(config).run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
