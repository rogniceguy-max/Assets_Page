"""The LAN download page for the first-install package.

A colleague with nothing installed has no client, so no session to present and no
update channel to receive anything through. The download therefore has to work before
any of that exists, which is what makes it worth pinning down: the routes must serve
without a session, must not become a way to read anything else in the directory, and
must behave when the operator has published nothing at all.
"""
from __future__ import annotations

import hashlib
import io
import os
import stat
import tempfile
import threading
import unittest
from email.parser import BytesParser
from pathlib import Path

from backend.installer_store import _has_write_bit, newest_setup_name
from backend.release_store import OpenRelease, ReleaseNotFound, ReleaseUnavailable
from backend.server import INSTALLER_FILE_PATH, MAX_CONCURRENT_DOWNLOADS, create_handler
from backend.service import ApiError


class NewestSetupNameTests(unittest.TestCase):
    def test_picks_the_highest_version_not_the_last_listed(self):
        self.assertEqual(newest_setup_name(
            ["MuYingDrive.Setup-1.0.9.exe", "MuYingDrive.Setup-1.0.2.exe", "MuYingDrive.Setup-1.0.5.exe"]),
            "MuYingDrive.Setup-1.0.9.exe")

    def test_orders_versions_numerically_not_as_text(self):
        # Lexically "1.0.9" sorts after "1.0.10", which would hand out the older build.
        self.assertEqual(newest_setup_name(
            ["MuYingDrive.Setup-1.0.10.exe", "MuYingDrive.Setup-1.0.9.exe"]),
            "MuYingDrive.Setup-1.0.10.exe")

    def test_ignores_names_that_do_not_follow_the_convention(self):
        self.assertEqual(newest_setup_name(
            ["MuYingDrive.Setup.exe", "MuYingDrive.Setup-1.0.exe", "MuYingDrive.Setup-1.0.0.zip",
             "notes.txt", "MuYingDrive.Setup-1.0.4.exe"]),
            "MuYingDrive.Setup-1.0.4.exe")

    def test_returns_nothing_when_there_is_nothing_to_offer(self):
        self.assertIsNone(newest_setup_name([]))
        self.assertIsNone(newest_setup_name(["MuYingDrive.Setup-1.0.0.exe.tmp"]))

    def test_a_rejected_name_cannot_be_selected_by_being_newest(self):
        # No version at all, so it is not a candidate however it is named.
        self.assertIsNone(newest_setup_name(["MuYingDrive.Setup-9.9.9.exe.bak"]))


class ImmutabilityRuleTests(unittest.TestCase):
    """The rule that keeps a half-copied package from being handed out.

    Testable here even though the store itself is not: it is a pure decision over a
    stat result, and it is the difference between refusing an upload in progress and
    serving a truncated executable with a checksum that vouches for it.
    """

    def test_a_writable_file_is_not_publishable(self):
        # 0644/0664/0666 are what a plain copy or an upload leaves behind.
        for mode in (0o644, 0o664, 0o666, 0o600, 0o444 | stat.S_IWOTH):
            self.assertTrue(_has_write_bit(mode), oct(mode))

    def test_a_read_only_file_is_publishable(self):
        for mode in (0o444, 0o555, 0o400):
            self.assertFalse(_has_write_bit(mode), oct(mode))


class FakeInstallerStore:
    """Stands in for InstallerStore, which is built on POSIX-only directory handles."""

    def __init__(self, root: Path, files: dict[str, bytes] | None = None):
        self._root = root
        self._files = files or {}

    @property
    def configured(self) -> bool:
        return True

    def latest_name(self) -> str:
        name = newest_setup_name(self._files)
        if name is None:
            raise ReleaseNotFound("no installer")
        return name

    def sha256(self, name: str) -> str:
        # Via open_verified, like the real store: the immutability rule has to apply to
        # the digest route too, or a half-written package would be endorsed rather than
        # refused.
        opened, digest = self.open_verified(name)
        opened.close()
        return digest

    def open_verified(self, name: str) -> tuple[OpenRelease, str]:
        payload = self._files[name]
        path = self._root / f"payload-{len(payload)}.bin"
        path.write_bytes(payload)
        # O_BINARY matters only on Windows, where os.open defaults to text mode and would
        # stop the stream at the first \x1a. The real store never runs there, but these
        # tests do.
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_BINARY", 0))
        return OpenRelease(descriptor, len(payload), 0, 0, 0, 0), hashlib.sha256(payload).hexdigest()


class UnconfiguredInstallerStore:
    configured = False


class StubApp:
    def __init__(self, root: Path):
        self.db_path = root / "stub.db"

    def session(self, token):
        raise ApiError(401, "SESSION_INVALID", "请先登录。")


class InstallerDownloadTests(unittest.TestCase):
    payload = b"MZ" + bytes(range(256)) * 8

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.app = StubApp(self.root)
        self.files = {"MuYingDrive.Setup-1.0.15.exe": self.payload}

    def tearDown(self):
        self.temp.cleanup()

    def request(self, path, headers=None, installer=None, download_slots=None):
        # No Authorization header anywhere: these routes must never need one.
        values = {"Host": "localhost"}
        values.update(headers or {})
        raw = (f"GET {path} HTTP/1.1\r\n" + "".join(f"{k}: {v}\r\n" for k, v in values.items()) + "\r\n").encode("ascii")
        handler_type = create_handler(
            self.app,
            installer=installer if installer is not None else FakeInstallerStore(self.root, self.files),
            download_slots=download_slots)
        handler = handler_type.__new__(handler_type)
        handler.rfile = io.BytesIO(raw)
        handler.wfile = io.BytesIO()
        handler.close_connection = True
        handler.handle_one_request()
        envelope, body = handler.wfile.getvalue().split(b"\r\n\r\n", 1)
        status_line, response_headers = envelope.split(b"\r\n", 1)
        return int(status_line.split()[1]), dict(BytesParser().parsebytes(response_headers).items()), body

    def test_someone_with_no_client_and_no_session_can_download(self):
        status, headers, body = self.request("/download/muying-setup.exe")
        self.assertEqual(status, 200)
        self.assertEqual(body, self.payload)
        # Saved under its versioned name so it is obvious which build a copy is.
        self.assertEqual(headers["Content-Disposition"], 'attachment; filename="MuYingDrive.Setup-1.0.15.exe"')
        self.assertEqual(headers["ETag"], f'"{hashlib.sha256(self.payload).hexdigest()}"')
        self.assertEqual(headers["Accept-Ranges"], "bytes")
        self.assertEqual(headers["X-Content-Type-Options"], "nosniff")

    def test_the_download_can_be_resumed(self):
        status, headers, body = self.request("/download/muying-setup.exe", {"Range": "bytes=2-9"})
        self.assertEqual(status, 206)
        self.assertEqual(body, self.payload[2:10])
        self.assertEqual(headers["Content-Range"], f"bytes 2-9/{len(self.payload)}")

    def test_an_unchanged_download_is_not_sent_twice(self):
        etag = f'"{hashlib.sha256(self.payload).hexdigest()}"'
        status, headers, body = self.request("/download/muying-setup.exe", {"If-None-Match": etag})
        self.assertEqual(status, 304)
        self.assertEqual(body, b"")

    def test_the_published_checksum_matches_what_was_downloaded(self):
        status, _headers, body = self.request("/download/muying-setup.sha256")
        self.assertEqual(status, 200)
        digest, name = body.decode("utf-8").strip().split("  ")
        self.assertEqual(name, "MuYingDrive.Setup-1.0.15.exe")
        self.assertEqual(digest, hashlib.sha256(self.payload).hexdigest())

    def test_the_bare_url_explains_itself(self):
        status, headers, body = self.request("/download")
        self.assertEqual(status, 200)
        self.assertEqual(headers["Content-Type"], "text/html; charset=utf-8")
        text = body.decode("utf-8")
        self.assertIn("MuYingDrive.Setup-1.0.15.exe", text)
        self.assertIn(hashlib.sha256(self.payload).hexdigest(), text)

    def test_the_bare_url_offers_something_to_click(self):
        # It shipped as a plain-text index once, which printed the download path as text.
        # Reported from a real browser as "只显示了黑屏和文字" — no download, because there
        # was nothing to click. The page exists to be handed to someone who has nothing
        # installed, so a link is the whole point of it.
        _status, headers, body = self.request("/download")
        text = body.decode("utf-8")
        self.assertIn("text/html", headers["Content-Type"])
        self.assertIn(f'href="{INSTALLER_FILE_PATH}"', text)
        self.assertIn("<a ", text)

    def test_nothing_published_yet_is_a_clear_answer_not_a_crash(self):
        self.files.clear()
        self.assertEqual(self.request("/download/muying-setup.exe")[0], 404)
        self.assertEqual(self.request("/download/muying-setup.sha256")[0], 404)
        self.assertEqual(self.request("/download")[0], 200)

    def test_an_unconfigured_directory_serves_nothing(self):
        for path in ("/download", "/download/muying-setup.exe", "/download/muying-setup.sha256"):
            self.assertEqual(self.request(path, installer=UnconfiguredInstallerStore())[0], 503)

    def test_too_many_downloads_at_once_are_refused_not_queued(self):
        # The cap exists so a roomful of colleagues fetching the installer cannot take
        # the login API down with them. Queuing would be worse than refusing: a queued
        # request keeps holding the worker slot it is waiting for a slot to be freed in.
        slots = threading.BoundedSemaphore(MAX_CONCURRENT_DOWNLOADS)
        for _ in range(MAX_CONCURRENT_DOWNLOADS):
            slots.acquire()
        status, headers, body = self.request("/download/muying-setup.exe", download_slots=slots)
        self.assertEqual(status, 503)
        self.assertEqual(headers.get("Retry-After"), "30")
        self.assertIn("DOWNLOAD_BUSY", body.decode("utf-8"))

    def test_the_api_is_untouched_while_downloads_are_refused(self):
        slots = threading.BoundedSemaphore(MAX_CONCURRENT_DOWNLOADS)
        for _ in range(MAX_CONCURRENT_DOWNLOADS):
            slots.acquire()
        self.request("/download/muying-setup.exe", download_slots=slots)
        self.assertEqual(self.request("/api/v2/health", download_slots=slots)[0], 200)

    def test_a_package_that_is_still_being_written_is_not_served(self):
        # Mirrors the store's immutability rule: a writable published file is a file
        # mid-copy, and a checksum read from it would vouch for a truncated download.
        class MidCopyStore(FakeInstallerStore):
            def open_verified(self, name):
                raise ReleaseUnavailable("installer must be immutable while published")

        store = MidCopyStore(self.root, self.files)
        self.assertEqual(self.request("/download/muying-setup.exe", installer=store)[0], 503)
        self.assertEqual(self.request("/download/muying-setup.sha256", installer=store)[0], 503)

    def test_a_broken_directory_reports_itself_unhealthy(self):
        # 503 rather than a friendly 200 page: an operator who mistyped the directory
        # needs the endpoint to look down to them, not to read as "nothing yet".
        class BrokenStore(FakeInstallerStore):
            def latest_name(self):
                raise ReleaseUnavailable("installer directory unavailable")

        self.assertEqual(self.request("/download", installer=BrokenStore(self.root, self.files))[0], 503)

    def test_neighbouring_paths_are_not_served(self):
        # The download root must not become a general file server.
        for path in ("/download/", "/download/../server.py", "/download/muying-setup.exe.bak",
                     "/download/MuYingDrive.Setup-1.0.15.exe"):
            status, headers, _body = self.request(path)
            self.assertIn(status, (200, 404), path)  # 200 only for the index route
            if status == 200:
                self.assertNotIn("Content-Disposition", headers)


if __name__ == "__main__":
    unittest.main()
