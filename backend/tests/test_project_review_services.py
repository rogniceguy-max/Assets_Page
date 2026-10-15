import tempfile
import unittest
from pathlib import Path
from backend.project_workbooks.host_adapters import SqliteReviewServices
from backend.project_workbooks.projects_api import ApiFault

class TestSqliteReviewServices(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.db_path = Path(self.temp_dir.name) / "reviews.db"
        self.service = SqliteReviewServices(self.db_path, row_kind="scene-rows")
        self.actor1 = {"user": {"id": 1, "username": "alice"}}
        self.actor2 = {"user": {"id": 2, "username": "bob"}}

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_empty_thread(self):
        res = self.service.thread(self.actor1, "file1", "row1", None)
        self.assertEqual(res["comment_count"], 0)
        self.assertEqual(len(res["comments"]), 0)

    def test_add_and_get_comment(self):
        res = self.service.add_comment(self.actor1, "file1", "row1", None, "第一条批注")
        self.assertEqual(res["comment_count"], 1)
        comment = res["comments"][0]
        self.assertEqual(comment["text"], "第一条批注")
        self.assertEqual(comment["user_name"], "alice")
        self.assertTrue(comment["can_edit"])
        self.assertTrue(comment["can_delete"])

        # Bob views thread
        res_bob = self.service.thread(self.actor2, "file1", "row1", None)
        self.assertEqual(res_bob["comment_count"], 1)
        comment_bob = res_bob["comments"][0]
        self.assertFalse(comment_bob["can_edit"])
        self.assertFalse(comment_bob["can_delete"])

    def test_edit_comment(self):
        res = self.service.add_comment(self.actor1, "file1", "row1", None, "原内容")
        c_id = res["comments"][0]["id"]
        version = res["comments"][0]["version"]

        res2 = self.service.edit_comment(self.actor1, "file1", "row1", c_id, version, "修改后内容")
        self.assertEqual(res2["comments"][0]["text"], "修改后内容")
        self.assertEqual(res2["comments"][0]["version"], version + 1)

    def test_delete_comment(self):
        res = self.service.add_comment(self.actor1, "file1", "row1", None, "待删除")
        c_id = res["comments"][0]["id"]
        version = res["comments"][0]["version"]

        res2 = self.service.delete_comment(self.actor1, "file1", "row1", c_id, version)
        self.assertEqual(res2["comment_count"], 0)

    def test_visitor_can_read_reviews_but_cannot_change_comments_or_votes(self):
        visitor = {"authenticated": True, "role": "visitor", "can_write": False,
                   "user": {"id": 0, "username": "访客", "group_type": "visitor",
                            "role": "visitor", "can_write": False}}
        created = self.service.add_comment(self.actor1, "file1", "row1", "image1", "员工评论")
        comment_id = created["comments"][0]["id"]
        self.assertEqual(1, self.service.thread(visitor, "file1", "row1", "image1")["comment_count"])
        for write in (
            lambda: self.service.add_comment(visitor, "file1", "row1", "image1", "访客评论"),
            lambda: self.service.edit_comment(visitor, "file1", "row1", comment_id, 1, "改写"),
            lambda: self.service.delete_comment(visitor, "file1", "row1", comment_id, 1),
            lambda: self.service.vote(visitor, "file1", "row1", "image1", "good"),
        ):
            with self.subTest(write=write), self.assertRaises(ApiFault) as denied:
                write()
            self.assertEqual(403, denied.exception.status)
