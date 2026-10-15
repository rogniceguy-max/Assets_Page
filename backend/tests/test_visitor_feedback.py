"""Feedback authorization for the login-free tour.

The tour may read public feedback and post anonymously, and must never reach a
maintenance action. The last case is the one worth guarding: it is the only thing
separating "a tour can report a problem" from "anyone on the LAN can resolve or hide
other people's feedback".
"""
import unittest

from backend.feedback import FeedbackService
from backend.service import ApiError

EMPLOYEE_TOKEN = "employee-token"
VISITOR_TOKEN = "visitor-token"
MAINTAINER_TOKEN = "maintainer-token"


def employee_session(token):
    if token == EMPLOYEE_TOKEN:
        return {"user": {"id": 4242}}
    if token == MAINTAINER_TOKEN:
        return {"user": {"id": 7}}
    raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")


def is_visitor(token):
    return token == VISITOR_TOKEN


class VisitorFeedbackAuthorizationTests(unittest.TestCase):
    def setUp(self):
        self.service = FeedbackService(
            employee_session,
            root=__import__("pathlib").Path("."),
            maintainers={7},
            visitor_authenticate=is_visitor)

    def test_visitor_may_read(self):
        # No exception means the read path is open to a tour session.
        self.service.authorize(VISITOR_TOKEN)

    def test_visitor_may_not_manage(self):
        with self.assertRaises(ApiError) as denied:
            self.service.authorize(VISITOR_TOKEN, manage=True)
        self.assertEqual(denied.exception.status, 401)

    def test_employee_reads_and_maintenance_is_separate(self):
        self.assertFalse(self.service.authorize(EMPLOYEE_TOKEN))       # reads, not a maintainer
        self.assertTrue(self.service.authorize(MAINTAINER_TOKEN))      # reads, is a maintainer
        self.assertTrue(self.service.authorize(MAINTAINER_TOKEN, manage=True))

    def test_employee_without_the_maintainer_id_cannot_manage(self):
        with self.assertRaises(ApiError) as denied:
            self.service.authorize(EMPLOYEE_TOKEN, manage=True)
        self.assertEqual(denied.exception.status, 403)

    def test_unknown_token_is_still_rejected(self):
        with self.assertRaises(ApiError):
            self.service.authorize("not-a-token")
        with self.assertRaises(ApiError):
            self.service.authorize("not-a-token", manage=True)

    def test_missing_token_is_rejected(self):
        with self.assertRaises(ApiError):
            self.service.authorize(None)

    def test_without_a_visitor_authority_the_tour_is_shut_out(self):
        # A service that was not wired for the tour must not open by accident.
        service = FeedbackService(employee_session, root=__import__("pathlib").Path("."), maintainers={7})
        with self.assertRaises(ApiError):
            service.authorize(VISITOR_TOKEN)


if __name__ == "__main__":
    unittest.main()
