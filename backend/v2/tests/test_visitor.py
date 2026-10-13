"""Tests for the login-free visitor channel.

The privileged helper itself is exercised on the NAS (it needs root and setuid); these
cover the service-side contract that the HTTP layer depends on.
"""
import unittest

from backend.v2.service import ApiError
from backend.v2.visitor import SPACES, VisitorReader, VisitorService, VisitorUnavailable


class FakeReader:
    """Stands in for the privileged helper."""

    def __init__(self):
        self.calls = []
        self.fail = None

    def list(self, space, relative_path):
        self.calls.append(("list", space, relative_path))
        if self.fail:
            raise self.fail
        return {"entries": [{"name": "a", "isDirectory": False, "size": 1, "modifiedUtc": 0}],
                "truncated": False}

    def read(self, space, relative_path, offset, length):
        self.calls.append(("read", space, relative_path, offset, length))
        if self.fail:
            raise self.fail
        return b"x" * min(length, 4), 100


class VisitorSessionTests(unittest.TestCase):
    def setUp(self):
        self.reader = FakeReader()
        self.service = VisitorService(self.reader)

    def test_session_is_read_only_and_exposes_only_the_three_spaces(self):
        session = self.service.start_session()
        self.assertEqual(session["role"], "visitor")
        self.assertTrue(session["capabilities"]["read"])
        self.assertFalse(session["capabilities"]["write"])
        self.assertEqual([space["id"] for space in session["spaces"]],
                         [space["id"] for space in SPACES])
        # Personal spaces are not part of the visitor contract at all.
        self.assertNotIn("personal", [space["id"] for space in session["spaces"]])

    def test_missing_or_unknown_session_is_rejected(self):
        with self.assertRaises(ApiError) as missing:
            self.service.authorize(None)
        self.assertEqual(missing.exception.status, 401)
        with self.assertRaises(ApiError) as unknown:
            self.service.authorize("not-a-token")
        self.assertEqual(unknown.exception.status, 401)

    def test_ended_session_is_rejected(self):
        token = self.service.start_session()["session_token"]
        self.service.end_session(token)
        with self.assertRaises(ApiError):
            self.service.authorize(token)

    def test_expired_session_is_rejected(self):
        service = VisitorService(self.reader, ttl_seconds=-1)
        token = service.start_session()["session_token"]
        with self.assertRaises(ApiError):
            service.authorize(token)

    def test_unknown_space_is_rejected_before_reaching_the_reader(self):
        token = self.service.start_session()["session_token"]
        for space in ("personal", "", None, "film/../writing"):
            with self.assertRaises(ApiError):
                self.service.list(token, space, "")
        self.assertEqual(self.reader.calls, [])

    def test_traversal_and_absolute_paths_are_rejected_before_the_reader(self):
        token = self.service.start_session()["session_token"]
        for path in ("../03_个人空间", "a/../../etc/passwd", "/etc/passwd", "a\\..\\b", "C:file"):
            with self.assertRaises(ApiError):
                self.service.list(token, "film", path)
        self.assertEqual(self.reader.calls, [])

    def test_list_and_read_reach_the_reader_for_valid_requests(self):
        token = self.service.start_session()["session_token"]
        listing = self.service.list(token, "public", "子目录")
        self.assertEqual(listing["space"], "public")
        self.assertEqual(len(listing["entries"]), 1)
        body, size = self.service.read(token, "public", "a/b.mp4", 10, 20)
        self.assertEqual(size, 100)
        self.assertEqual(self.reader.calls[1], ("read", "public", "a/b.mp4", 10, 20))

    def test_unavailable_reader_propagates_as_unavailable(self):
        self.reader.fail = VisitorUnavailable("down")
        token = self.service.start_session()["session_token"]
        with self.assertRaises(VisitorUnavailable):
            self.service.list(token, "film", "")

    def test_reader_without_a_socket_fails_closed(self):
        service = VisitorService(VisitorReader(None))
        token = service.start_session()["session_token"]
        with self.assertRaises(VisitorUnavailable):
            service.list(token, "film", "")


if __name__ == "__main__":
    unittest.main()
