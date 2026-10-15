"""Tests for read-only visitor sessions used by the normal SQLite APIs."""

import unittest

from backend.project_spaces import ProjectSpaceService
from backend.project_workbooks.host_adapters import HostSecurity
from backend.service import ApiError
from backend.visitor import VisitorService


class VisitorSessionTests(unittest.TestCase):
    def setUp(self):
        self.service = VisitorService()

    def test_session_is_ephemeral_and_read_only(self):
        session = self.service.start_session()
        self.assertEqual(session["role"], "visitor")
        self.assertTrue(session["capabilities"]["read"])
        self.assertFalse(session["capabilities"]["write"])
        token = session["session_token"]
        self.assertTrue(self.service.has_session(token))
        self.service.end_session(token)
        self.assertFalse(self.service.has_session(token))

    def test_missing_or_unknown_session_is_rejected(self):
        for token in (None, "not-a-token"):
            with self.assertRaises(ApiError) as error:
                self.service.authorize(token)
            self.assertEqual(error.exception.status, 401)

    def test_expired_session_is_rejected(self):
        service = VisitorService(ttl_seconds=1)
        token = service.start_session()["session_token"]
        # Expiry uses monotonic time; advancing the stored deadline avoids a sleep.
        service._sessions[token] = 0
        with self.assertRaises(ApiError):
            service.authorize(token)

    def test_visitor_token_authenticates_as_read_only_for_shared_apis(self):
        token = self.service.start_session()["session_token"]

        def mock_session(_token):
            raise ApiError(401, "SESSION_INVALID", "invalid")

        security = HostSecurity(mock_session, visitor_authenticate=self.service.has_session)
        principal = security.authenticate({"authorization": f"Bearer {token}"})
        self.assertEqual(principal["role"], "visitor")
        self.assertFalse(principal["can_write"])

        spaces = ProjectSpaceService(mock_session, visitor_authenticate=self.service.has_session)
        actor = spaces.authorize(token)
        self.assertEqual(actor["role"], "visitor")
        with self.assertRaises(ApiError) as error:
            spaces.create_project(actor, "NewProject")
        self.assertEqual(error.exception.status, 403)


if __name__ == "__main__":
    unittest.main()
