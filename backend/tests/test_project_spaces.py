from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from backend.project_spaces import ProjectSpaceService
from backend.project_workbooks.projects_api import ApiFault
from backend.project_workbooks.sqlite_store import SqliteProjectWorkbookStore
from backend.service import ApiError


class StubSession:
    def __init__(self, ok: bool = True):
        self.ok = ok

    def __call__(self, token):
        if not self.ok or token != "good-token":
            raise ApiError(401, "SESSION_INVALID", "请先登录。")
        return {"authenticated": True, "user": {"id": 7, "username": "fixture"}}


class ProjectSpaceServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.catalog = SqliteProjectWorkbookStore(self.root / "catalog.sqlite")
        self.service = ProjectSpaceService(StubSession(), catalog=self.catalog)
        self.actor = {"authenticated": True, "user": {
            "id": 7, "username": "fixture", "group_type": "film"}}

    def tearDown(self):
        self.temp.cleanup()

    def test_physical_folder_is_not_listed_until_sqlite_project_exists(self):
        (self.root / "孟婆").mkdir()
        self.assertEqual([], self.service.list_projects(self.actor))

        project = self.catalog.create_project_record("SQLite 项目", self.actor)
        items = self.service.list_projects(self.actor)
        self.assertEqual([project["id"]], [item["id"] for item in items])
        self.assertEqual("SQLite 项目", items[0]["name"])
        self.assertEqual(2, len(self.catalog.list_files(self.actor, project["id"])))

    def test_create_only_writes_sqlite_and_returns_stable_project_id(self):
        created = self.service.create_project(self.actor, "川渝霸王龙")
        self.assertEqual("川渝霸王龙", created["name"])
        self.assertFalse((self.root / "川渝霸王龙").exists())
        self.assertEqual(created["id"], self.service.list_projects(self.actor)[0]["id"])
        self.assertEqual(2, len(self.catalog.list_files(self.actor, created["id"])))

    def test_project_name_validation_and_active_name_conflict(self):
        self.service.create_project(self.actor, "孟婆")
        with self.assertRaises(ApiError) as duplicate:
            self.service.create_project(self.actor, "孟婆")
        self.assertEqual(409, duplicate.exception.status)
        for invalid_name in ("", "   ", ".", "..", "a/b", "a\\b", "a:b", "x" * 121):
            with self.subTest(name=invalid_name), self.assertRaises(ApiError) as caught:
                self.service.create_project(self.actor, invalid_name)
            self.assertEqual(422, caught.exception.status)

    def test_deleted_project_and_workbooks_keep_their_relationship_and_restore(self):
        project = self.service.create_project(self.actor, "孟婆")
        workbook_ids = {item["id"] for item in self.catalog.list_files(self.actor, project["id"])}
        deleted = self.service.delete_project(self.actor, project["id"])
        self.assertTrue(deleted["deleted"])
        self.assertEqual([], self.service.list_projects(self.actor))
        self.assertEqual(project["id"], self.service.list_projects(self.actor, include_deleted=True)[0]["id"])
        with self.catalog._reading() as conn:
            retained = conn.execute("SELECT id,deleted_at FROM workbooks WHERE project_id=?",
                                     (project["id"],)).fetchall()
        self.assertEqual(workbook_ids, {item["id"] for item in retained})
        self.assertTrue(all(item["deleted_at"] is not None for item in retained))
        with self.assertRaises(ApiFault):
            self.catalog.document(self.actor, next(iter(workbook_ids)))

        restored = self.service.restore_project(self.actor, project["id"])
        self.assertFalse(restored["deleted"])
        self.assertEqual(workbook_ids,
            {item["id"] for item in self.catalog.list_files(self.actor, project["id"])})

    def test_unsigned_caller_is_rejected_before_project_operations(self):
        with self.assertRaises(ApiError) as caught:
            self.service.authorize(None)
        self.assertEqual(401, caught.exception.status)
        self.assertEqual([], self.catalog.projects(self.actor))


if __name__ == "__main__":
    unittest.main()
