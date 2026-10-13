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
import tempfile
import unittest
from email.parser import BytesParser
from pathlib import Path

from backend.v2.installer_store import newest_setup_name
from backend.v2.release_store import OpenRelease, ReleaseNotFound, ReleaseUnavailable
from backend.v2.server import create_handler
from backend.v2.service import ApiError


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
        return hashlib.sha256(self._files[name]).hexdigest()

    def open(self, name: str) -> OpenRelease:
        payload = self._files[name]
        path = self._root / f"payload-{len(payload)}.bin"
        path.write_bytes(payload)
        # O_BINARY matters only on Windows, where os.open defaults to text mode and would
        # stop the stream at the first \x1a. The real store never runs there, but these
        # tests do.
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_BINARY", 0))
        return OpenRelease(descriptor, len(payload), 0, 0, 0, 0)


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

    def request(self, path, headers=None, installer=None):
        # No Authorization header anywhere: these routes must never need one.
        values = {"Host": "localhost"}
        values.update(headers or {})
        raw = (f"GET {path} HTTP/1.1\r\n" + "".join(f"{k}: {v}\r\n" for k, v in values.items()) + "\r\n").encode("ascii")
        handler_type = create_handler(
            self.app, installer=installer if installer is not None else FakeInstallerStore(self.root, self.files))
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
        status, _headers, body = self.request("/download")
        self.assertEqual(status, 200)
        text = body.decode("utf-8")
        self.assertIn("MuYingDrive.Setup-1.0.15.exe", text)
        self.assertIn(hashlib.sha256(self.payload).hexdigest(), text)

    def test_nothing_published_yet_is_a_clear_answer_not_a_crash(self):
        self.files.clear()
        self.assertEqual(self.request("/download/muying-setup.exe")[0], 404)
        self.assertEqual(self.request("/download/muying-setup.sha256")[0], 404)
        self.assertEqual(self.request("/download")[0], 200)

    def test_an_unconfigured_directory_serves_nothing(self):
        for path in ("/download", "/download/muying-setup.exe", "/download/muying-setup.sha256"):
            self.assertEqual(self.request(path, installer=UnconfiguredInstallerStore())[0], 503)

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
