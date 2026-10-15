import base64
import io
import sqlite3
import unittest
from contextlib import closing
from unittest.mock import patch

from PIL import Image
from backend.feedback import sanitize_image
from backend.tests import test_feedback as fixtures
from backend.tests.test_feedback import submission, screenshot


class FeedbackLimitsTests(unittest.TestCase):
    setUp = fixtures.FeedbackTests.setUp
    tearDown = fixtures.FeedbackTests.tearDown
    error = fixtures.FeedbackTests.error
    def test_image_bomb_animation_and_truncation(self):
        image = Image.new("RGB", (10, 10))
        animated = io.BytesIO()
        image.save(animated, format="PNG", save_all=True, append_images=[Image.new("RGB", (10, 10), "red")])
        self.error(400, lambda: sanitize_image({"mime": "image/png", "data": base64.b64encode(animated.getvalue()).decode()}))
        raw = base64.b64decode(screenshot()["data"])
        self.error(400, lambda: sanitize_image({"mime": "image/png", "data": base64.b64encode(raw[:30]).decode()}))
        bomb = io.BytesIO()
        Image.new("RGB", (4096, 4096)).save(bomb, format="PNG")
        self.error(400, lambda: sanitize_image({"mime": "image/png", "data": base64.b64encode(bomb.getvalue()).decode()}))

    def test_quota_failed_write_leaves_no_partial_record_and_no_raw_file(self):
        with patch("backend.feedback.MAX_STORAGE_BYTES", 1):
            self.error(507, lambda: self.service.create("employee-session", submission(images=[screenshot()])))
        self.assertFalse(self.service.list("employee-session", {})["items"])
        self.assertFalse(self.service.list("maintainer-session", {"scope": ["pending"]})["items"])
        self.assertEqual({f.name for f in (self.root / "feedback").iterdir()}, {"feedback.db"})

    def test_storage_version_and_role_payload_fail_closed(self):
        self.service.create("employee-session", submission())
        fid = self.service.list("employee-session", {})["items"][0]["feedback_id"]
        self.error(400, lambda: self.service.update("maintainer-session", fid, {"status": [], "visibility": []}))
        with closing(sqlite3.connect(self.root / "feedback/feedback.db")) as db:
            db.execute("PRAGMA user_version=2")
        self.error(503, lambda: self.service.list("employee-session", {}))
