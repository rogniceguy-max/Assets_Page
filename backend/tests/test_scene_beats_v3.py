from __future__ import annotations

import json
import hashlib
import sqlite3
import tempfile
import unittest
import uuid
from pathlib import Path

from backend.project_workbooks.projects_api import ApiFault
from backend.project_workbooks.sqlite_store import SqliteProjectWorkbookStore


class SceneBeatsV3Tests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir.name)
        self.db_path = self.root / "workbooks.db"
        self.store = SqliteProjectWorkbookStore(self.db_path)
        project = self.store.create_project_record("TestProject")
        self.project_id = project["id"]

    def _catalog_video(self, name: str, *, beat: int = 1) -> dict[str, str]:
        file_id = str(uuid.uuid4())
        revision_id = str(uuid.uuid4())
        operation_id = str(uuid.uuid4())
        path = f"节拍表/第{beat}beat/{name}"
        now = "2026-10-16T10:00:00.000+00:00"
        digest = hashlib.sha256(b"test video bytes").hexdigest()
        compact_id = revision_id.replace("-", "")
        conn = sqlite3.connect(self.db_path)
        try:
            conn.execute("""INSERT INTO files
                (file_id,scope_id,project_id,logical_path,logical_path_key,media_kind,media_type,
                 current_revision_id,version,created_at,updated_at)
                VALUES(?,?,?,?,?,'video','video/mp4',?,1,?,?)""",
                (file_id, f"project:{self.project_id}", self.project_id, path, path.casefold(),
                 revision_id, now, now))
            conn.execute("""INSERT INTO file_revisions
                (revision_id,file_id,revision_no,storage_key,sha256,size,media_type,actor_id,
                 actor_name,source,operation_id,created_at)
                VALUES(?,?,1,?,?,?,?,?,?,'wpf',?,?)""",
                (revision_id, file_id, f"_objects/video/{compact_id[:2]}/{compact_id}", digest,
                 len(b"test video bytes"), "video/mp4", "7", "fixture", operation_id, now))
            conn.commit()
        finally:
            conn.close()
        return {"id": file_id, "file_id": file_id, "name": name, "path": path}

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_create_scene_beats_defaults_to_8_beats(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-1", "第1集")
        doc = self.store.document(None, wb["id"])
        sheet = doc["sheets"][0]
        self.assertEqual("scene_beats", sheet["kind"])
        self.assertEqual(8, len(sheet["scene_rows"]))
        for i, row in enumerate(sheet["scene_rows"], start=1):
            self.assertEqual("1", row["scene_number"])
            self.assertEqual(str(i), row["beat_number"])
            self.assertEqual("", row["story_content"])
            self.assertIsNone(row["note"])
            self.assertIsNone(row["video"])
            self.assertEqual([], row["discarded_videos"])

    def test_add_scene_row_increments_beat_number(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-2", "第2集")
        doc = self.store.document(None, wb["id"])
        sheet_id = doc["sheets"][0]["id"]
        row9 = self.store.add_scene_row(None, wb["id"], sheet_id)
        self.assertEqual("1", row9["scene_number"])
        self.assertEqual("9", row9["beat_number"])

    def test_delete_workbook(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-3", "待删节拍表")
        files = self.store.list_files(None, self.project_id)
        self.assertTrue(any(f["id"] == wb["id"] for f in files))

        del_res = self.store.delete_workbook(None, wb["id"])
        self.assertTrue(del_res["ok"])

        files_after = self.store.list_files(None, self.project_id)
        self.assertFalse(any(f["id"] == wb["id"] for f in files_after))

    def test_update_scene_row_and_video_lifecycle(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-4", "视频测试")
        doc = self.store.document(None, wb["id"])
        row = doc["sheets"][0]["scene_rows"][0]
        row_id = row["id"]

        # 1. Update text fields inline
        up1 = self.store.update_scene_row(None, wb["id"], row_id, 1, {
            "story_content": "男主与女主在孟婆桥相遇",
            "breakdown_plan": "远景->特写",
            "note": "注意光影渲染",
            "label_level": "黄"
        })
        self.assertEqual("男主与女主在孟婆桥相遇", up1["story_content"])
        self.assertEqual("远景->特写", up1["breakdown_plan"])
        self.assertEqual("注意光影渲染", up1["note"])
        self.assertEqual("黄", up1["label_level"])

        # 2. Attach video V1
        v1 = self._catalog_video("take1.mp4")
        up2 = self.store.update_scene_row(None, wb["id"], row_id, up1["version"], {
            "video_action": "attach",
            "video": v1
        })
        self.assertEqual(v1, up2["video"])
        self.assertEqual([], up2["discarded_videos"])

        # 3. Discard current video V1
        up3 = self.store.update_scene_row(None, wb["id"], row_id, up2["version"], {
            "video_action": "discard"
        })
        self.assertIsNone(up3["video"])
        self.assertEqual(1, len(up3["discarded_videos"]))

        # 4. Restore V1 from discarded_videos
        up4 = self.store.update_scene_row(None, wb["id"], row_id, up3["version"], {
            "video_action": "restore",
            "video_id": v1["id"]
        })
        self.assertEqual("take1.mp4", up4["video"]["name"])
        self.assertEqual([], up4["discarded_videos"])

    def test_multi_video_in_single_beat(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-multi", "BeatMultiVideo")
        doc = self.store.document(None, wb["id"])
        row_id = doc["sheets"][0]["scene_rows"][0]["id"]
        v1 = self._catalog_video("clip1.mp4")
        v2 = self._catalog_video("clip2.mp4")

        # 1. Add first video with action "add"
        up1 = self.store.update_scene_row(None, wb["id"], row_id, 1, {
            "video_action": "add",
            "video": v1
        })
        self.assertEqual(1, len(up1["videos"]))
        self.assertEqual("clip1.mp4", up1["videos"][0]["name"])

        # 2. Add second video with action "add" -> both must be retained!
        up2 = self.store.update_scene_row(None, wb["id"], row_id, up1["version"], {
            "video_action": "add",
            "video": v2
        })
        self.assertEqual(2, len(up2["videos"]))
        self.assertEqual(["clip1.mp4", "clip2.mp4"], [v["name"] for v in up2["videos"]])
        self.assertEqual([], up2["discarded_videos"])

        # 3. Discard clip1
        up4 = self.store.update_scene_row(None, wb["id"], row_id, up2["version"], {
            "video_action": "discard",
            "video_id": v1["id"]
        })
        self.assertEqual(1, len(up4["videos"]))
        self.assertEqual("clip2.mp4", up4["videos"][0]["name"])
        self.assertEqual(1, len(up4["discarded_videos"]))
        self.assertEqual("clip1.mp4", up4["discarded_videos"][0]["name"])

    def test_unlinked_video_cannot_replace_an_active_video(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-name-conflict", "同名冲突")
        row = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"][0]
        first = self._catalog_video("take.mp4")
        current = self.store.update_scene_row(None, wb["id"], row["id"], row["version"], {
            "video_action": "add", "video": first
        })

        for action in ("add", "attach"):
            unlinked_id = str(uuid.uuid4())
            with self.assertRaises(ApiFault) as raised:
                self.store.update_scene_row(None, wb["id"], row["id"], current["version"], {
                    "video_action": action,
                    "video": {"id": unlinked_id, "file_id": unlinked_id,
                              "name": "take.mp4", "path": first["path"]},
                })

            self.assertEqual(409, raised.exception.status)
        refreshed = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"][0]
        self.assertEqual([first], refreshed["videos"])
        self.assertEqual([], refreshed["discarded_videos"])

    def test_add_rejects_same_name_in_another_workbook_with_same_media_folder(self):
        first_wb = self.store.create(None, "scene_beats", self.project_id, "op-folder-conflict-a", "第一本节拍表")
        second_wb = self.store.create(None, "scene_beats", self.project_id, "op-folder-conflict-b", "第二本节拍表")
        first = self.store.document(None, first_wb["id"])["sheets"][0]["scene_rows"][0]
        second = self.store.document(None, second_wb["id"])["sheets"][0]["scene_rows"][0]
        shared_video = self._catalog_video("take.mp4")
        self.store.update_scene_row(None, first_wb["id"], first["id"], first["version"], {
            "video_action": "add",
            "video": shared_video,
        })

        with self.assertRaises(ApiFault) as raised:
            self.store.update_scene_row(None, second_wb["id"], second["id"], second["version"], {
                "video_action": "add",
                "video": shared_video,
            })

        self.assertEqual(409, raised.exception.status)

    def test_readding_discarded_name_requires_explicit_restore(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-discard-conflict", "废片重加")
        row = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"][0]
        original = self._catalog_video("take.mp4")
        current = self.store.update_scene_row(None, wb["id"], row["id"], row["version"], {
            "video_action": "add", "video": original
        })
        discarded = self.store.update_scene_row(None, wb["id"], row["id"], current["version"], {
            "video_action": "discard", "video_id": original["id"]
        })

        with self.assertRaises(ApiFault) as raised:
            self.store.update_scene_row(None, wb["id"], row["id"], discarded["version"], {
                "video_action": "add",
                "video": original,
            })

        self.assertEqual(409, raised.exception.status)
        restored = self.store.update_scene_row(None, wb["id"], row["id"], discarded["version"], {
            "video_action": "restore", "video_id": original["id"]
        })
        self.assertEqual(original["id"], restored["video"]["id"])
        self.assertEqual([], restored["discarded_videos"])

    def test_new_absolute_video_paths_are_rejected_but_legacy_rows_remain_readable(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-legacy-path", "旧路径读取")
        row = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"][0]
        invalid_path_id = str(uuid.uuid4())
        with self.assertRaises(ApiFault):
            self.store.update_scene_row(None, wb["id"], row["id"], row["version"], {
                "video_action": "add",
                "video": {"id": invalid_path_id, "file_id": invalid_path_id,
                          "name": "legacy.mp4", "path": "/media/legacy.mp4"},
            })

        legacy_video = {"id": "legacy", "name": "legacy.mp4", "path": "/media/legacy.mp4"}
        conn = sqlite3.connect(self.db_path)
        try:
            stored = conn.execute("SELECT data FROM workbook_rows WHERE id=?", (row["id"],)).fetchone()
            payload = json.loads(stored[0])
            payload["video"] = legacy_video
            payload["videos"] = [legacy_video]
            conn.execute("UPDATE workbook_rows SET data=? WHERE id=?", (json.dumps(payload), row["id"]))
            conn.commit()
        finally:
            conn.close()

        reread = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"][0]
        self.assertEqual(legacy_video, reread["video"])
        self.assertEqual([legacy_video], reread["videos"])

    def test_beat_change_moves_every_linked_file_logically_and_preserves_object_key(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-beat-move", "改 beat")
        rows = self.store.document(None, wb["id"])["sheets"][0]["scene_rows"]
        first, second = rows[0], rows[1]
        self.store.delete_row(None, wb["id"], second["id"], second["version"], "scene_beats")
        video = self._catalog_video("take.mp4")
        attached = self.store.update_scene_row(None, wb["id"], first["id"], first["version"], {
            "video_action": "add", "video": video})
        conn = sqlite3.connect(self.db_path)
        try:
            object_key_before = conn.execute("SELECT storage_key FROM file_revisions WHERE file_id=?",
                                             (video["file_id"],)).fetchone()[0]
        finally:
            conn.close()

        moved = self.store.update_scene_row(None, wb["id"], first["id"], attached["version"],
                                            {"beat_number": "2"})
        self.assertEqual("节拍表/第2beat/take.mp4", moved["video"]["path"])
        self.assertEqual(moved["video"]["path"], moved["videos"][0]["path"])
        conn = sqlite3.connect(self.db_path)
        try:
            file_row = conn.execute("SELECT logical_path FROM files WHERE file_id=?",
                                    (video["file_id"],)).fetchone()
            object_key_after = conn.execute("SELECT storage_key FROM file_revisions WHERE file_id=?",
                                            (video["file_id"],)).fetchone()[0]
            event = conn.execute("""SELECT previous_logical_path,logical_path FROM file_events
                WHERE file_id=? AND action='move' ORDER BY sequence DESC LIMIT 1""",
                (video["file_id"],)).fetchone()
        finally:
            conn.close()
        self.assertEqual("节拍表/第2beat/take.mp4", file_row[0])
        self.assertEqual(object_key_before, object_key_after)
        self.assertEqual(("节拍表/第1beat/take.mp4", "节拍表/第2beat/take.mp4"), tuple(event))

    def test_beat_change_retargets_deleted_video_without_emitting_a_local_move(self):
        from backend.file_storage import FileStorageService

        actor = {"authenticated": True, "user": {"id": 7, "username": "editor", "group_type": "film"}}
        files = FileStorageService(self.store, transfer_base_url="https://transfer.example",
            ticket_secret=b"test-secret-that-is-long-enough-for-hmac")
        workbook = self.store.create(actor, "scene_beats", self.project_id, "op-deleted-video", "删除后改 beat")
        rows = self.store.document(actor, workbook["id"])["sheets"][0]["scene_rows"]
        row = rows[0]
        self.store.delete_row(actor, workbook["id"], rows[1]["id"], rows[1]["version"], "scene_beats")
        video = self._catalog_video("gone.mp4")
        attached = self.store.update_scene_row(actor, workbook["id"], row["id"], row["version"], {
            "video_action": "add", "video": video})
        files.delete_file(actor, {"operation_id": str(uuid.uuid4()),
            "client_id": str(uuid.uuid4()), "file_id": video["file_id"],
            "expected_version": 1, "source": "sync"})

        moved = self.store.update_scene_row(actor, workbook["id"], row["id"], attached["version"],
                                            {"beat_number": "2"})
        expected_path = "节拍表/第2beat/gone.mp4"
        self.assertEqual(expected_path, moved["video"]["path"])
        with self.store._reading() as conn:
            file_row = conn.execute("SELECT logical_path,deleted_at,current_revision_id FROM files WHERE file_id=?",
                                     (video["file_id"],)).fetchone()
            action = conn.execute("SELECT action FROM file_events WHERE file_id=? ORDER BY sequence DESC LIMIT 1",
                                  (video["file_id"],)).fetchone()[0]
        self.assertEqual(expected_path, file_row["logical_path"])
        self.assertIsNotNone(file_row["deleted_at"])
        self.assertEqual("retarget_deleted", action)
        restored = files.restore_file(actor, video["file_id"], {"operation_id": str(uuid.uuid4()),
            "client_id": str(uuid.uuid4()), "revision_id": file_row["current_revision_id"],
            "expected_version": 3, "source": "sync"})
        self.assertEqual(4, restored["version"])


if __name__ == "__main__":
    unittest.main()
