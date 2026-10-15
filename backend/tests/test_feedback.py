from __future__ import annotations

import base64
import io
import json
import sqlite3
import tempfile
import unittest
from contextlib import closing
from email.parser import BytesParser
from pathlib import Path
from unittest.mock import patch

from PIL import Image, PngImagePlugin

from backend.feedback import FeedbackService, MAX_IMAGE_BYTES, MAX_BODY_BYTES, sanitize_image
from backend.server import create_handler
from backend.service import ApiError


def screenshot(format="PNG", **save):
    output = io.BytesIO()
    Image.new("RGB", (24, 16), (110, 100, 220)).save(output, format=format, **save)
    return {"mime": "image/png" if format == "PNG" else "image/jpeg", "data": base64.b64encode(output.getvalue()).decode("ascii")}


def submission(**changes):
    value = dict(kind="problem", category="upload", title="上传暂停后无法继续", description="点击继续按钮后，没有恢复上传。", images=[], privacy_confirmed=True)
    value.update(changes)
    return value


class TestIdentity:
    def __init__(self, root):
        self.db_path = root / "identity.db"
        self.valid = {"employee-session": 1, "maintainer-session": 2}

    def session(self, token):
        if token not in self.valid:
            raise ApiError(401, "SESSION_INVALID", "请先登录。")
        return {"authenticated": True, "user": {"id": self.valid[token], "username": "must-never-be-stored", "department": "private-department"}}


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.identity = TestIdentity(self.root)
        self.service = FeedbackService(self.identity.session, self.root / "feedback", {2})

    def tearDown(self):
        self.temp.cleanup()

    def error(self, status, operation):
        with self.assertRaises(ApiError) as raised:
            operation()
        self.assertEqual(raised.exception.status, status)

    def test_login_required_for_all_feedback_operations_and_revocation(self):
        for token in (None, "invalid"):
            for call in (lambda: self.service.list(token, {}), lambda: self.service.create(token, submission()), lambda: self.service.capabilities(token), lambda: self.service.detail(token, "a" * 32), lambda: self.service.attachment(token, "a" * 32), lambda: self.service.update(token, "a" * 32, {})):
                self.error(401, call)
        self.assertFalse((self.root / "feedback").exists())
        del self.identity.valid["employee-session"]
        self.error(401, lambda: self.service.create("employee-session", submission()))

    def test_two_kinds_anonymous_schema_response_and_durable_storage(self):
        problem = self.service.create("employee-session", submission())
        suggestion = self.service.create("maintainer-session", submission(kind="suggestion", title="希望支持文件收藏"))
        self.assertNotEqual(problem["feedback_id"], suggestion["feedback_id"])
        values = self.service.list("employee-session", {})["items"]
        self.assertEqual({v["status"] for v in values}, {"pending", "evaluating"})
        permitted = {"feedback_id", "kind", "category", "title", "description", "status", "created_at", "updated_at", "attachments"}
        self.assertTrue(all(set(v) == permitted for v in values))
        recreated = FeedbackService(self.identity.session, self.root / "feedback", {2})
        self.assertEqual(len(recreated.list("employee-session", {})["items"]), 2)
        with closing(sqlite3.connect(self.root / "feedback/feedback.db")) as db:
            tables = db.execute("SELECT name,sql FROM sqlite_master WHERE type='table'").fetchall()
            self.assertEqual({r[0] for r in tables}, {"feedback", "attachments"})
            dump = "\n".join(db.iterdump())
            for sensitive in ("must-never-be-stored", "private-department", "employee-session", "maintainer-session", "user_id", "author", "reporter", "device_id"):
                self.assertNotIn(sensitive, dump)

    def test_reject_identity_fields_sensitive_text_and_unconfirmed_privacy(self):
        for changes in ({"user_id": 1}, {"author": "a"}, {"privacy_confirmed": False}, {"kind": []}, {"category": []}, {"title": ""}, {"description": "x" * 4001}):
            self.error(400, lambda: self.service.create("employee-session", submission(**changes)))
        for text in ("Bearer abc", "password=secret", "token:abcdef", "密码：1234", "C:\\Users\\name", "\\\\nas\\private", "/volume1/Assets", "10.0.0.1", "someone@example.com", "https://nas/private"):
            self.error(400, lambda: self.service.create("employee-session", submission(description=text)))

    def test_png_and_jpeg_metadata_removed_and_pixels_preserved(self):
        info = PngImagePlugin.PngInfo()
        info.add_text("Author", "secret-name")
        png = screenshot(pnginfo=info)
        exif = Image.Exif()
        exif[315] = "secret-name"
        exif[274] = 6
        for payload in (png, screenshot("JPEG", exif=exif)):
            _, cleaned, w, h = sanitize_image(payload)
            self.assertNotIn(b"secret-name", cleaned)
            with Image.open(io.BytesIO(cleaned)) as result:
                self.assertEqual(result.format, "PNG")
                self.assertFalse(result.info)
                self.assertFalse(result.getexif())
                self.assertEqual(result.size, (w, h))
        self.assertEqual(sanitize_image(png)[2:], (24, 16))

    def test_bad_images_forged_mime_oversize_dimensions_and_paths_rejected(self):
        image = screenshot()
        for payload in (dict(image, filename="../../private"), dict(image, mime="image/jpeg"), dict(image, mime=[]), dict(image, data="!invalid"), {"mime": "image/png", "data": base64.b64encode(b"not an image").decode()}, dict(image, data=base64.b64encode(b"a" * (MAX_IMAGE_BYTES + 1)).decode())):
            with self.assertRaises(ApiError):
                sanitize_image(payload)
        out = io.BytesIO()
        Image.new("RGB", (4097, 1)).save(out, format="PNG")
        self.error(400, lambda: sanitize_image({"mime": "image/png", "data": base64.b64encode(out.getvalue()).decode()}))
        self.error(400, lambda: self.service.create("employee-session", submission(images=[image] * 5)))

    def test_quarantine_publish_hide_and_attachment_authorization(self):
        created = self.service.create("employee-session", submission(images=[screenshot()]))
        fid = created["feedback_id"]
        self.assertTrue(created["pending_review"])
        self.assertFalse(self.service.list("employee-session", {})["items"])
        self.error(404, lambda: self.service.detail("employee-session", fid))
        self.error(403, lambda: self.service.list("employee-session", {"scope": ["pending"]}))
        detail = self.service.detail("maintainer-session", fid)
        attachment = detail["attachments"][0]["attachment_id"]
        self.error(404, lambda: self.service.attachment("employee-session", attachment))
        self.error(403, lambda: self.service.update("employee-session", fid, dict(status="resolved", visibility="public")))
        self.error(400, lambda: self.service.update("maintainer-session", fid, dict(status="accepted", visibility="public")))
        self.service.update("maintainer-session", fid, dict(status="in_progress", visibility="public"))
        self.assertEqual(len(self.service.list("employee-session", {})["items"]), 1)
        self.assertTrue(self.service.attachment("employee-session", attachment).startswith(b"\x89PNG"))
        self.service.update("maintainer-session", fid, dict(status="resolved", visibility="hidden"))
        self.error(404, lambda: self.service.attachment("employee-session", attachment))
        with closing(sqlite3.connect(self.root / "feedback/maintenance.db")) as audit:
            rows = audit.execute("SELECT maintainer,feedback_id FROM operations").fetchall()
            self.assertEqual(rows, [(2, fid), (2, fid)])

    def test_search_kind_pagination_literal_query_and_default_role_denied(self):
        for n in range(22):
            self.service.create("employee-session", submission(title=f"分页测试 {n}", kind="problem" if n % 2 else "suggestion"))
        first = self.service.list("employee-session", {})
        second = self.service.list("employee-session", {"page": ["2"]})
        self.assertTrue(first["has_more"])
        self.assertEqual(len(second["items"]), 2)
        self.assertFalse({i["feedback_id"] for i in first["items"]} & {i["feedback_id"] for i in second["items"]})
        self.assertEqual(len(self.service.list("employee-session", {"kind": ["suggestion"]})["items"]), 11)
        self.assertFalse(self.service.list("employee-session", {"q": ["%' OR 1=1--"]})["items"])
        self.error(400, lambda: self.service.list("employee-session", {"page": ["-1"]}))
        self.error(400, lambda: self.service.list("employee-session", {"q": ["a", "b"]}))
        closed = FeedbackService(self.identity.session, self.root / "feedback")
        self.assertFalse(closed.capabilities("maintainer-session")["can_manage"])

    def test_global_short_window_rate_limit_has_no_identity_keys(self):
        with patch("backend.feedback.time.monotonic", return_value=100):
            for _ in range(30): self.service.create("employee-session", submission())
            self.error(429, lambda: self.service.create("maintainer-session", submission()))
        self.assertTrue(all(isinstance(t, (int, float)) for t in self.service._recent))
        with patch("backend.feedback.time.monotonic", return_value=701):
            self.service.create("employee-session", submission())

    def test_storage_fault_is_safe_and_independent_and_bad_id_is_not_a_path(self):
        blocked = self.root / "blocked"
        blocked.write_text("a file", encoding="utf-8")
        service = FeedbackService(self.identity.session, blocked)
        self.error(503, lambda: service.create("employee-session", submission()))
        self.assertTrue(self.identity.session("employee-session")["authenticated"])
        self.error(404, lambda: self.service.attachment("employee-session", "../../identity.db"))
        self.error(404, lambda: self.service.detail("employee-session", "../../identity.db"))


class FeedbackHttpTests(unittest.TestCase):
    def setUp(self):
        FeedbackTests.setUp(self)

    def tearDown(self):
        FeedbackTests.tearDown(self)

    def request(self, method, path, payload=None, token="employee-session", headers=None):
        values = {"Content-Type": "application/json"}
        if token: values["Authorization"] = f"Bearer {token}"
        values.update(headers or {})
        body = json.dumps(payload).encode("utf-8") if payload is not None else b""
        values.setdefault("Content-Length", str(len(body)))
        raw = (f"{method} {path} HTTP/1.1\r\nHost: localhost\r\n" + "".join(f"{k}: {v}\r\n" for k, v in values.items()) + "\r\n").encode("ascii") + body
        # Exercise the real HTTP parser and handler without needing network permissions.
        handler_type = create_handler(self.identity, feedback=self.service)
        handler = handler_type.__new__(handler_type)
        handler.rfile = io.BytesIO(raw)
        handler.wfile = io.BytesIO()
        handler.close_connection = True
        handler.handle_one_request()
        envelope, response_body = handler.wfile.getvalue().split(b"\r\n\r\n", 1)
        status_line, response_headers = envelope.split(b"\r\n", 1)
        return int(status_line.split()[1]), dict(BytesParser().parsebytes(response_headers).items()), response_body

    def test_http_auth_body_limits_headers_routes_and_safe_errors(self):
        status, headers, _ = self.request("GET", "/api/v2/feedback", token=None)
        self.assertEqual(status, 401)
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(headers["X-Content-Type-Options"], "nosniff")
        self.assertEqual(self.request("POST", "/api/v2/feedback", submission(), headers={"Content-Length": str(MAX_BODY_BYTES + 1)})[0], 413)
        status, _, body = self.request("POST", "/api/v2/feedback", submission())
        self.assertEqual(status, 201)
        fid = json.loads(body)["feedback_id"]
        self.assertEqual(self.request("GET", f"/api/v2/feedback/{fid}")[0], 200)
        self.assertEqual(self.request("PATCH", f"/api/v2/feedback/{fid}", dict(status="resolved", visibility="public"))[0], 403)
        self.assertEqual(self.request("PATCH", f"/api/v2/feedback/{fid}", dict(status="resolved", visibility="public"), token="maintainer-session")[0], 200)
        self.assertEqual(self.request("GET", "/api/v2/feedback?q=a&q=b")[0], 400)
        self.assertEqual(self.request("GET", "/api/v2/health")[0], 200)


if __name__ == "__main__":
    unittest.main()
