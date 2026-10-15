from __future__ import annotations

import hashlib
import hmac
import json
import tempfile
import unittest
import uuid
from pathlib import Path

from backend.file_storage import (THUMBNAIL_GENERATOR_VERSION, FileStorageApi,
    FileStorageService, _b64url, _canonical, _unb64url)
from backend.project_workbooks.projects_api import ApiFault
from backend.project_workbooks.sqlite_store import SqliteProjectWorkbookStore


class FileStorageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.store = SqliteProjectWorkbookStore(Path(self.temp.name) / "workbooks.sqlite")
        self.actor = {"authenticated": True, "user": {
            "id": 7, "username": "测试用户", "group_type": "film"}}
        self.secret = b"test-secret-that-is-long-enough-for-hmac"
        self.files = FileStorageService(self.store, transfer_base_url="https://transfer.example",
                                        ticket_secret=self.secret)
        self.project = self.store.create_project_record("测试项目", self.actor)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _upload(self, path: str = "视频/样片.mp4", *, data: bytes = b"sample video bytes",
                media_type: str = "video/mp4", scope_id: str | None = None,
                project_id: str | None = None, actor: dict | None = None) -> tuple[dict, dict]:
        active_actor = actor or self.actor
        operation_id = str(uuid.uuid4())
        client_id = str(uuid.uuid4())
        digest = hashlib.sha256(data).hexdigest()
        scope_id = scope_id or f"project:{self.project['id']}"
        if scope_id.startswith("project:") and project_id is None:
            project_id = scope_id.split(":", 1)[1]
        begin = self.files.begin_upload(active_actor, {
            "operation_id": operation_id,
            "action": "upload",
            "client_id": client_id,
            "scope_id": scope_id,
            "project_id": project_id,
            "logical_path": path,
            "file_id": None,
            "expected_version": 0,
            "size": len(data),
            "sha256": digest,
            "media_type": media_type,
            "source": "sync",
        })
        receipt = {
            "v": 1,
            "op_id": operation_id,
            "object_id": begin["object_id"],
            "size": len(data),
            "sha256": digest,
            "staged_at": "2026-10-16T10:00:00.000Z",
            "nonce": str(uuid.uuid4()),
        }
        receipt["mac"] = _b64url(hmac.new(self.secret, _canonical(receipt), hashlib.sha256).digest())
        committed = self.files.commit_upload(active_actor, operation_id, receipt)
        return begin, committed

    def test_upload_history_delete_restore_and_durable_changes(self) -> None:
        begin, committed = self._upload()
        self.assertEqual(committed["file_id"], begin["file_id"])
        self.assertEqual(committed["version"], 1)
        self.assertEqual("video", begin["object_kind"])
        compact_id = begin["revision_id"].replace("-", "")
        with self.store._reading() as conn:
            storage_key = conn.execute("SELECT storage_key FROM file_revisions WHERE revision_id=?",
                                        (begin["revision_id"],)).fetchone()[0]
        self.assertEqual(f"_objects/video/{compact_id[:2]}/{compact_id}", storage_key)

        root_listing = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}")
        self.assertEqual(len(root_listing["items"]), 1)
        self.assertTrue(root_listing["items"][0]["is_directory"])
        video_listing = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
                                               parent_path="视频")
        self.assertEqual(video_listing["items"][0]["file_id"], begin["file_id"])
        history = self.files.file_history(self.actor, begin["file_id"])
        self.assertEqual([entry["revision_id"] for entry in history["items"]], [begin["revision_id"]])

        deleted = self.files.delete_file(self.actor, {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "file_id": begin["file_id"], "expected_version": 1, "source": "wpf"})
        self.assertEqual(deleted["version"], 2)
        self.assertEqual(self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
                                               parent_path="视频")["items"], [])
        recycle = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
            parent_path="视频", include_deleted=True)
        self.assertEqual(recycle["items"][0]["deleted_at"] is not None, True)

        restored = self.files.restore_file(self.actor, begin["file_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "revision_id": begin["revision_id"], "expected_version": 2, "source": "sync"})
        self.assertEqual(restored["version"], 3)
        self.assertIsNone(self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
                                                 parent_path="视频")["items"][0]["deleted_at"])

        changes = self.files.changes(self.actor, client_id=str(uuid.uuid4()), after_sequence=0)
        self.assertEqual([entry["action"] for entry in changes["items"] if entry["entity_kind"] == "file"],
                         ["upload", "delete", "restore"])
        self.assertGreater(changes["next_sequence"], 0)

    def test_deleted_path_can_be_reused_but_old_file_restore_conflicts(self) -> None:
        old_begin, _ = self._upload("视频/同名.mp4")
        delete_request = {"operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "file_id": old_begin["file_id"], "expected_version": 1, "source": "wpf"}
        deleted = self.files.delete_file(self.actor, delete_request)
        self.assertEqual(deleted, self.files.delete_file(self.actor, delete_request))

        new_begin, new_commit = self._upload("视频/同名.mp4")
        self.assertNotEqual(old_begin["file_id"], new_begin["file_id"])
        self.assertEqual(new_begin["file_id"], new_commit["file_id"])
        active = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
            parent_path="视频")["items"]
        self.assertEqual([new_begin["file_id"]], [item["file_id"] for item in active])

        with self.assertRaises(ApiFault) as caught:
            self.files.restore_file(self.actor, old_begin["file_id"], {
                "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
                "revision_id": old_begin["revision_id"], "expected_version": 2, "source": "wpf"})
        self.assertEqual(409, caught.exception.status)

    def test_file_listing_paginates_sql_direct_children_and_aggregate_media(self) -> None:
        for path in ("root-a.mp4", "root-b.mp4", "nested/child/deep.mp4"):
            self._upload(path)
        listed = []
        cursor = None
        while True:
            page = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
                                         cursor=cursor, limit=1)
            listed.extend(item["name"] for item in page["items"])
            cursor = page["next_cursor"]
            if cursor is None:
                break
        self.assertEqual(["nested", "root-a.mp4", "root-b.mp4"], listed)

        personal, _ = self._upload("private.jpg", media_type="image/jpeg",
                                   scope_id=f"personal:{self.actor['user']['id']}")
        project_image, _ = self._upload("poster.jpg", media_type="image/jpeg")
        aggregate_ids = set()
        cursor = None
        while True:
            page = self.files.list_files(self.actor, scope_id="all", media_kind="image",
                                         cursor=cursor, limit=1)
            aggregate_ids.update(item["file_id"] for item in page["items"])
            cursor = page["next_cursor"]
            if cursor is None:
                break
        self.assertEqual({personal["file_id"], project_image["file_id"]}, aggregate_ids)

    def test_abort_releases_only_uncommitted_placeholder(self) -> None:
        old_begin, old_commit = self._upload("取消/已有.mp4")
        old_history = self.files.file_history(self.actor, old_begin["file_id"])
        operation_id = str(uuid.uuid4())
        client_id = str(uuid.uuid4())
        digest = hashlib.sha256(b"replacement bytes").hexdigest()
        replacing = self.files.begin_upload(self.actor, {
            "operation_id": operation_id, "client_id": client_id, "source": "wpf",
            "scope_id": f"project:{self.project['id']}", "project_id": self.project["id"],
            "logical_path": "取消/已有.mp4", "file_id": old_begin["file_id"],
            "expected_version": 1, "size": len(b"replacement bytes"), "sha256": digest,
            "media_type": "application/octet-stream"})
        cancel_request = {"client_id": client_id, "expected_state": replacing["state"]}
        cancelled = self.files.abort_upload(self.actor, operation_id, cancel_request)
        self.assertEqual("aborted", cancelled["state"])
        self.assertFalse(cancelled["placeholder_retired"])
        self.assertEqual(cancelled, self.files.abort_upload(self.actor, operation_id, cancel_request))
        with self.store._reading() as conn:
            current = conn.execute("SELECT current_revision_id,version,media_type FROM files WHERE file_id=?",
                                    (old_begin["file_id"],)).fetchone()
        self.assertEqual(old_commit["revision_id"], current["current_revision_id"])
        self.assertEqual(1, current["version"])
        self.assertEqual("video/mp4", current["media_type"])
        self.assertEqual(old_history, self.files.file_history(self.actor, old_begin["file_id"]))

        new_op_id = str(uuid.uuid4())
        new_client = str(uuid.uuid4())
        placeholder = self.files.begin_upload(self.actor, {
            "operation_id": new_op_id, "client_id": new_client, "source": "sync",
            "scope_id": f"project:{self.project['id']}", "project_id": self.project["id"],
            "logical_path": "取消/新建.mp4", "expected_version": 0, "size": 1,
            "sha256": hashlib.sha256(b"x").hexdigest(), "media_type": "video/mp4"})
        retired = self.files.abort_upload(self.actor, new_op_id, {
            "client_id": new_client, "expected_state": placeholder["state"]})
        self.assertTrue(retired["placeholder_retired"])
        retry = self.files.begin_upload(self.actor, {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()), "source": "sync",
            "scope_id": f"project:{self.project['id']}", "project_id": self.project["id"],
            "logical_path": "取消/新建.mp4", "expected_version": 0, "size": 1,
            "sha256": hashlib.sha256(b"x").hexdigest(), "media_type": "video/mp4"})
        self.assertNotEqual(placeholder["file_id"], retry["file_id"])

    def test_abort_http_route_uses_expected_state_compare_and_set(self) -> None:
        operation_id = str(uuid.uuid4())
        client_id = str(uuid.uuid4())
        self.files.begin_upload(self.actor, {
            "operation_id": operation_id, "client_id": client_id, "source": "wpf",
            "scope_id": f"project:{self.project['id']}", "project_id": self.project["id"],
            "logical_path": "route-cancel.mp4", "expected_version": 0, "size": 1,
            "sha256": hashlib.sha256(b"x").hexdigest(), "media_type": "video/mp4"})
        api = FileStorageApi(None, self.files)
        with self.assertRaises(ApiFault) as stale:
            api._route(self.actor, "POST", f"/api/v2/file-operations/{operation_id}/abort",
                       {"client_id": client_id, "expected_state": "staged"})
        self.assertEqual(409, stale.exception.status)
        response = api._route(self.actor, "POST", f"/api/v2/file-operations/{operation_id}/abort",
                              {"client_id": client_id, "expected_state": "receiving"})
        self.assertEqual(200, response.status)
        self.assertEqual("aborted", json.loads(response.body)["state"])

    def test_receipt_signature_and_stale_version_are_rejected(self) -> None:
        operation_id = str(uuid.uuid4())
        begin = self.files.begin_upload(self.actor, {
            "operation_id": operation_id, "action": "upload", "client_id": str(uuid.uuid4()),
            "scope_id": f"project:{self.project['id']}", "project_id": self.project["id"],
            "logical_path": "clip.mp4", "expected_version": 0, "size": 1,
            "sha256": hashlib.sha256(b"x").hexdigest(), "media_type": "video/mp4", "source": "wpf"})
        receipt = {"v": 1, "op_id": operation_id, "object_id": begin["object_id"], "size": 1,
                   "sha256": hashlib.sha256(b"x").hexdigest(), "staged_at": "2026-10-16T10:00:00Z",
                   "nonce": str(uuid.uuid4()), "mac": "bad"}
        with self.assertRaises(ApiFault):
            self.files.commit_upload(self.actor, operation_id, receipt)

        valid = dict(receipt)
        valid.pop("mac")
        valid["mac"] = _b64url(hmac.new(self.secret, _canonical(valid), hashlib.sha256).digest())
        self.files.commit_upload(self.actor, operation_id, valid)
        with self.assertRaises(ApiFault) as caught:
            self.files.delete_file(self.actor, {"operation_id": str(uuid.uuid4()),
                "client_id": str(uuid.uuid4()), "file_id": begin["file_id"],
                "expected_version": 9, "source": "sync"})
        self.assertEqual(caught.exception.status, 409)

    def test_directory_tombstone_only_restores_its_own_descendants(self) -> None:
        begin, _ = self._upload("素材/图片.png")
        directory = self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}")["items"][0]
        result = self.files.directory_operation(self.actor, directory["directory_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "action": "delete", "expected_version": directory["version"]})
        self.assertEqual(result["state"], "committed")
        with self.assertRaises(ApiFault):
            self.files.restore_file(self.actor, begin["file_id"], {
                "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
                "revision_id": begin["revision_id"], "expected_version": 2, "source": "wpf"})
        restored = self.files.directory_operation(self.actor, directory["directory_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "action": "restore", "expected_version": 2})
        self.assertEqual(restored["state"], "committed")
        self.assertEqual(len(self.files.list_files(self.actor, scope_id=f"project:{self.project['id']}",
                                                   parent_path="素材")["items"]), 1)

    def test_project_and_workbook_deletion_preserve_relations(self) -> None:
        workbook = self.store.list_files(self.actor, self.project["id"])[0]
        workbook_id = workbook["id"]
        self.store.delete_workbook(self.actor, workbook_id)
        self.assertEqual(self.store.list_files(self.actor, self.project["id"]), [
            item for item in self.store.list_files(self.actor, self.project["id"], include_deleted=True)
            if item["id"] != workbook_id])
        with self.store._reading() as conn:
            before = conn.execute("SELECT COUNT(*) n FROM sheets WHERE workbook_id=?", (workbook_id,)).fetchone()["n"]
            row_count = conn.execute("SELECT COUNT(*) n FROM workbook_rows WHERE sheet_id IN (SELECT id FROM sheets WHERE workbook_id=?)",
                                     (workbook_id,)).fetchone()["n"]
        self.assertGreater(before, 0)
        self.assertGreater(row_count, 0)
        self.store.restore_workbook(self.actor, workbook_id)
        self.assertIn(workbook_id, {item["id"] for item in self.store.list_files(self.actor, self.project["id"])})

        self.store.delete_project_record(self.project["id"], self.actor)
        self.assertEqual(self.store.projects(self.actor), [])
        self.assertEqual(len(self.store.projects(self.actor, include_deleted=True)), 1)
        with self.assertRaises(ApiFault):
            self.store.document(self.actor, workbook_id)
        self.store.restore_project_record(self.project["id"], self.actor)
        self.assertEqual(len(self.store.projects(self.actor)), 1)
        self.assertIsNotNone(self.store.document(self.actor, workbook_id))

    def test_visitor_can_read_public_departments_and_projects_but_cannot_write(self) -> None:
        visitor = {"authenticated": True, "role": "visitor", "can_write": False,
                   "user": {"id": 0, "username": "访客", "group_type": "visitor",
                            "role": "visitor", "can_write": False}}
        project_scope = f"project:{self.project['id']}"
        public_image, _ = self._upload("公开/公共.jpg", media_type="image/jpeg",
                                       scope_id="public:assets")
        film_image, _ = self._upload("影视/剧照.jpg", media_type="image/jpeg",
                                    scope_id="department:film")
        writing_actor = {"authenticated": True, "user": {
            "id": 9, "username": "文学同事", "group_type": "writing"}}
        writing_image, _ = self._upload("文学/插图.jpg", media_type="image/jpeg",
                                       scope_id="department:writing", actor=writing_actor)
        project_image, _ = self._upload("项目/照片.jpg", media_type="image/jpeg",
                                       scope_id=project_scope)
        personal_image, _ = self._upload("私人/照片.jpg", media_type="image/jpeg",
                                        scope_id=f"personal:{self.actor['user']['id']}")
        for scope in ("public:assets", "department:film", "department:writing", project_scope):
            self.files.list_files(visitor, scope_id=scope,
                project_id=self.project["id"] if scope == project_scope else None)
        aggregate = self.files.list_files(visitor, scope_id="all", media_kind="image")
        visible_ids = {item["file_id"] for item in aggregate["items"]}
        self.assertEqual(visible_ids, {public_image["file_id"], film_image["file_id"],
            writing_image["file_id"], project_image["file_id"]})
        self.assertNotIn(personal_image["file_id"], visible_ids)
        employee_aggregate = self.files.list_files(self.actor, scope_id="all", media_kind="image")
        employee_ids = {item["file_id"] for item in employee_aggregate["items"]}
        self.assertIn(film_image["file_id"], employee_ids)
        self.assertIn(personal_image["file_id"], employee_ids)
        self.assertNotIn(writing_image["file_id"], employee_ids)
        self.assertIn(self.project["id"], {item["id"] for item in self.store.projects(visitor)})
        workbooks = self.store.list_files(visitor, self.project["id"])
        self.assertGreaterEqual(len(workbooks), 2)
        self.store.document(visitor, workbooks[0]["id"])
        with self.assertRaises(ApiFault) as personal:
            self.files.list_files(visitor, scope_id="personal:0")
        self.assertEqual(403, personal.exception.status)
        with self.assertRaises(ApiFault) as sync:
            self.files.changes(visitor, client_id="visitor-client", after_sequence=0)
        self.assertEqual(403, sync.exception.status)
        with self.assertRaises(ApiFault) as acknowledge:
            self.files.acknowledge(visitor, {"client_id": "visitor-client", "sequence": 0})
        self.assertEqual(403, acknowledge.exception.status)
        with self.assertRaises(ApiFault) as write:
            self.files.begin_upload(visitor, {})
        self.assertEqual(403, write.exception.status)

    def test_change_feed_redacts_the_inaccessible_side_of_cross_scope_moves(self) -> None:
        other_actor = {"authenticated": True, "user": {
            "id": 8, "username": "另一位同事", "group_type": "writing"}}
        personal_scope = f"personal:{self.actor['user']['id']}"
        private_file, _ = self._upload("个人草稿/不应泄露的名字.mp4", scope_id=personal_scope)
        public_move = self.files.move_file(self.actor, {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "file_id": private_file["file_id"], "expected_version": 1, "source": "wpf",
            "target_scope_id": "public:assets", "target_logical_path": "公开/共享片段.mp4"})
        public_event = self.files.changes(other_actor, client_id=str(uuid.uuid4()),
            after_sequence=public_move["change_sequence"] - 1)["items"][-1]
        self.assertEqual("create", public_event["action"])
        self.assertEqual("公开/共享片段.mp4", public_event["logical_path"])
        self.assertIsNone(public_event["previous_logical_path"])
        self.assertNotIn("个人草稿/不应泄露的名字.mp4", json.dumps(public_event, ensure_ascii=False))

        private_move = self.files.move_file(self.actor, {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "file_id": private_file["file_id"], "expected_version": 2, "source": "wpf",
            "target_scope_id": personal_scope, "target_logical_path": "私人目录/隐藏位置.mp4"})
        private_event = self.files.changes(other_actor, client_id=str(uuid.uuid4()),
            after_sequence=private_move["change_sequence"] - 1)["items"][-1]
        self.assertEqual("delete", private_event["action"])
        self.assertEqual("public:assets", private_event["scope_id"])
        self.assertEqual("公开/共享片段.mp4", private_event["logical_path"])
        self.assertIsNone(private_event["previous_logical_path"])
        self.assertNotIn("私人目录/隐藏位置.mp4", json.dumps(private_event, ensure_ascii=False))

    def test_mac_worker_uploads_revision_thumbnail_through_signed_object_ticket(self) -> None:
        begin, _ = self._upload("预览/样片.mp4")
        jobs = self.files.media_worker_jobs(limit=10)
        self.assertEqual(begin["file_id"], jobs["items"][0]["file_id"])
        source_claims = json.loads(_unb64url(jobs["items"][0]["download_ticket"].split(".")[1]))
        self.assertEqual("media-worker", source_claims["user_id"])
        self.assertEqual("video", source_claims["object_kind"])

        thumbnail = b"RIFFthumbnailWEBP"
        digest = hashlib.sha256(thumbnail).hexdigest()
        intent = self.files.begin_thumbnail_operation(begin["file_id"], begin["revision_id"], {
            "worker_id": "m4-worker-test", "size": len(thumbnail),
            "sha256": digest, "media_type": "image/webp"})
        predictable_old_id = str(uuid.uuid5(uuid.NAMESPACE_URL,
            "muying-thumbnail-operation:" +
            f"{begin['file_id']}:{begin['revision_id']}:{THUMBNAIL_GENERATOR_VERSION}"))
        self.assertNotEqual(predictable_old_id, intent["operation_id"])
        retry_intent = self.files.begin_thumbnail_operation(begin["file_id"], begin["revision_id"], {
            "worker_id": "m4-worker-test", "size": len(thumbnail),
            "sha256": digest, "media_type": "image/webp"})
        self.assertEqual(intent["operation_id"], retry_intent["operation_id"])
        upload_claims = json.loads(_unb64url(intent["ticket"].split(".")[1]))
        self.assertEqual("thumbnail", upload_claims["object_kind"])
        receipt = {"v": 1, "op_id": intent["operation_id"], "object_id": intent["object_id"],
            "size": len(thumbnail), "sha256": digest, "staged_at": "2026-10-16T10:00:00Z",
            "nonce": str(uuid.uuid4())}
        receipt["mac"] = _b64url(hmac.new(self.secret, _canonical(receipt), hashlib.sha256).digest())
        self.files.commit_thumbnail_operation(begin["file_id"], begin["revision_id"],
                                              intent["operation_id"], receipt)
        download = self.files.thumbnail_download_ticket(self.actor, begin["file_id"], {
            "client_id": str(uuid.uuid4()), "revision_id": begin["revision_id"]})
        self.assertEqual("thumbnail", download["object_kind"])
        self.assertEqual([], self.files.media_worker_jobs(limit=10)["items"])

    def test_mac_worker_includes_personal_space_media(self) -> None:
        personal, _ = self._upload("个人照片/样片.jpg", media_type="image/jpeg",
                                   scope_id=f"personal:{self.actor['user']['id']}")
        jobs = self.files.media_worker_jobs(limit=10)
        self.assertIn(personal["file_id"], {item["file_id"] for item in jobs["items"]})

    def test_directory_move_updates_image_projection_and_linked_video_path_atomically(self) -> None:
        self._upload("节拍表/海报.jpg", data=b"image bytes", media_type="image/jpeg")
        video_begin, _ = self._upload("节拍表/第1beat/take.mp4")
        workbook = self.store.create(self.actor, "scene_beats", self.project["id"],
            str(uuid.uuid4()), "改目录测试")
        row = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"][0]
        video = {"id": video_begin["file_id"], "file_id": video_begin["file_id"],
                 "name": "take.mp4", "path": "节拍表/第1beat/take.mp4"}
        self.store.update_scene_row(self.actor, workbook["id"], row["id"], row["version"], {
            "video_action": "attach", "video": video})
        with self.store._reading() as conn:
            directory = conn.execute("""SELECT directory_id,version FROM file_directories
                WHERE scope_id=? AND logical_path='节拍表' AND deleted_at IS NULL""",
                (f"project:{self.project['id']}",)).fetchone()
        moved = self.files.directory_operation(self.actor, directory["directory_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "action": "move", "expected_version": directory["version"],
            "target_scope_id": f"project:{self.project['id']}",
            "target_project_id": self.project["id"], "target_logical_path": "新片场"})
        self.assertEqual("committed", moved["state"])
        scene = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"][0]
        self.assertEqual("新片场/第1beat/take.mp4", scene["video"]["path"])
        self.assertEqual(scene["video"]["path"], scene["videos"][0]["path"])
        self.assertEqual(["新片场/海报.jpg"], [item["logical_path"]
            for item in self.store.list_images(self.actor, self.project["id"])["items"]])

    def test_renamed_linked_video_keeps_its_name_when_beat_changes(self) -> None:
        video_begin, _ = self._upload("节拍表/第1beat/original.mp4")
        workbook = self.store.create(self.actor, "scene_beats", self.project["id"],
            str(uuid.uuid4()), "重命名视频测试")
        rows = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"]
        row = rows[0]
        self.store.delete_row(self.actor, workbook["id"], rows[1]["id"], rows[1]["version"],
                              "scene_beats")
        video = {"id": video_begin["file_id"], "file_id": video_begin["file_id"],
                 "name": "original.mp4", "path": "节拍表/第1beat/original.mp4"}
        self.store.update_scene_row(self.actor, workbook["id"], row["id"], row["version"], {
            "video_action": "attach", "video": video})
        self.files.move_file(self.actor, {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "file_id": video_begin["file_id"], "expected_version": 1, "source": "wpf",
            "target_scope_id": f"project:{self.project['id']}",
            "target_project_id": self.project["id"],
            "target_logical_path": "节拍表/第1beat/renamed.mp4"})

        scene = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"][0]
        self.assertEqual("renamed.mp4", scene["video"]["name"])
        self.assertEqual("节拍表/第1beat/renamed.mp4", scene["video"]["path"])
        changed = self.store.update_scene_row(self.actor, workbook["id"], row["id"], scene["version"], {
            "beat_number": "2"})
        self.assertEqual("renamed.mp4", changed["video"]["name"])
        self.assertEqual("节拍表/第2beat/renamed.mp4", changed["video"]["path"])

    def test_overwrite_and_restore_update_media_kind_and_image_projection(self) -> None:
        image, _ = self._upload("素材/封面.jpg", data=b"old image", media_type="image/jpeg")
        project_id = self.project["id"]
        listed = self.store.list_images(self.actor, project_id)["items"]
        self.assertEqual([image["file_id"]], [item["id"] for item in listed])

        replacement_bytes = b"new video bytes"
        digest = hashlib.sha256(replacement_bytes).hexdigest()
        operation_id = str(uuid.uuid4())
        replacement = self.files.begin_upload(self.actor, {
            "operation_id": operation_id, "action": "upload", "client_id": str(uuid.uuid4()),
            "source": "wpf", "scope_id": f"project:{project_id}", "project_id": project_id,
            "logical_path": "素材/封面.jpg", "file_id": image["file_id"],
            "expected_version": 1, "size": len(replacement_bytes), "sha256": digest,
            "media_type": "video/mp4"})
        receipt = {"v": 1, "op_id": operation_id, "object_id": replacement["object_id"],
            "size": len(replacement_bytes), "sha256": digest,
            "staged_at": "2026-10-16T10:00:00Z", "nonce": str(uuid.uuid4())}
        receipt["mac"] = _b64url(hmac.new(self.secret, _canonical(receipt), hashlib.sha256).digest())
        self.files.commit_upload(self.actor, operation_id, receipt)
        self.assertEqual([], self.store.list_images(self.actor, project_id)["items"])
        current = self.files.list_files(self.actor, scope_id=f"project:{project_id}",
            parent_path="素材")["items"][0]
        self.assertEqual("video", current["media_kind"])
        self.assertEqual("video/mp4", current["media_type"])

        restored_image = self.files.restore_file(self.actor, image["file_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "revision_id": image["revision_id"], "expected_version": 2, "source": "wpf"})
        self.assertEqual("image", self.files.list_files(self.actor,
            scope_id=f"project:{project_id}", parent_path="素材")["items"][0]["media_kind"])
        self.assertEqual([image["file_id"]], [item["id"] for item in
            self.store.list_images(self.actor, project_id)["items"]])

        self.files.restore_file(self.actor, image["file_id"], {
            "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
            "revision_id": replacement["revision_id"], "expected_version": restored_image["version"],
            "source": "wpf"})
        self.assertEqual([], self.store.list_images(self.actor, project_id)["items"])
        self.store.delete_project_record(project_id, self.actor)
        self.store.restore_project_record(project_id, self.actor)
        self.assertEqual([], self.store.list_images(self.actor, project_id)["items"])

    def test_attached_video_cannot_be_overwritten_or_restored_as_an_image(self) -> None:
        path = "节拍表/第1beat/take.mp4"
        image, _ = self._upload(path, data=b"old image", media_type="image/jpeg")
        video_bytes = b"video revision"
        video_digest = hashlib.sha256(video_bytes).hexdigest()
        video_operation = str(uuid.uuid4())
        video = self.files.begin_upload(self.actor, {
            "operation_id": video_operation, "action": "upload", "client_id": str(uuid.uuid4()),
            "source": "wpf", "scope_id": f"project:{self.project['id']}",
            "project_id": self.project["id"], "logical_path": path,
            "file_id": image["file_id"], "expected_version": 1,
            "size": len(video_bytes), "sha256": video_digest, "media_type": "video/mp4"})
        video_receipt = {"v": 1, "op_id": video_operation, "object_id": video["object_id"],
            "size": len(video_bytes), "sha256": video_digest,
            "staged_at": "2026-10-16T10:00:00Z", "nonce": str(uuid.uuid4())}
        video_receipt["mac"] = _b64url(hmac.new(self.secret, _canonical(video_receipt), hashlib.sha256).digest())
        self.files.commit_upload(self.actor, video_operation, video_receipt)

        workbook = self.store.create(self.actor, "scene_beats", self.project["id"],
            str(uuid.uuid4()), "媒体类型关联测试")
        row = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"][0]
        self.store.update_scene_row(self.actor, workbook["id"], row["id"], row["version"], {
            "video_action": "attach", "video": {"id": image["file_id"], "file_id": image["file_id"],
                "name": "take.mp4", "path": path}})

        with self.assertRaises(ApiFault) as restore:
            self.files.restore_file(self.actor, image["file_id"], {
                "operation_id": str(uuid.uuid4()), "client_id": str(uuid.uuid4()),
                "revision_id": image["revision_id"], "expected_version": 2, "source": "wpf"})
        self.assertEqual("VIDEO_FILE_TYPE_MISMATCH", restore.exception.code)

        image_bytes = b"replacement image"
        image_digest = hashlib.sha256(image_bytes).hexdigest()
        image_operation = str(uuid.uuid4())
        replacement = self.files.begin_upload(self.actor, {
            "operation_id": image_operation, "action": "upload", "client_id": str(uuid.uuid4()),
            "source": "wpf", "scope_id": f"project:{self.project['id']}",
            "project_id": self.project["id"], "logical_path": path,
            "file_id": image["file_id"], "expected_version": 2,
            "size": len(image_bytes), "sha256": image_digest, "media_type": "image/jpeg"})
        image_receipt = {"v": 1, "op_id": image_operation, "object_id": replacement["object_id"],
            "size": len(image_bytes), "sha256": image_digest,
            "staged_at": "2026-10-16T10:00:00Z", "nonce": str(uuid.uuid4())}
        image_receipt["mac"] = _b64url(hmac.new(self.secret, _canonical(image_receipt), hashlib.sha256).digest())
        with self.assertRaises(ApiFault) as overwrite:
            self.files.commit_upload(self.actor, image_operation, image_receipt)
        self.assertEqual("VIDEO_FILE_TYPE_MISMATCH", overwrite.exception.code)

    def test_beat_rejects_a_non_video_file_even_when_its_path_matches(self) -> None:
        image, _ = self._upload("节拍表/第1beat/not-a-video.mp4",
            data=b"image bytes", media_type="image/jpeg")
        workbook = self.store.create(self.actor, "scene_beats", self.project["id"],
            str(uuid.uuid4()), "拒绝非视频测试")
        row = self.store.document(self.actor, workbook["id"])["sheets"][0]["scene_rows"][0]
        with self.assertRaises(ApiFault) as caught:
            self.store.update_scene_row(self.actor, workbook["id"], row["id"], row["version"], {
                "video_action": "attach", "video": {
                    "id": image["file_id"], "file_id": image["file_id"],
                    "name": "not-a-video.mp4", "path": "节拍表/第1beat/not-a-video.mp4"}})
        self.assertEqual("VIDEO_FILE_TYPE_MISMATCH", caught.exception.code)


if __name__ == "__main__":
    unittest.main()
