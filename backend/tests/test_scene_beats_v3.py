from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from backend.project_workbooks.sqlite_store import SqliteProjectWorkbookStore


class SceneBeatsV3Tests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir.name)
        self.db_path = self.root / "workbooks.db"
        self.project_dir = self.root / "TestProject"
        self.project_dir.mkdir()
        self.store = SqliteProjectWorkbookStore(self.root, self.db_path)
        projects = self.store.projects(None)
        self.project_id = projects[0]["id"]

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
            self.assertIsNone(row["final_video"])
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
        v1 = {"id": "v-1", "name": "take1.mp4", "path": "/media/take1.mp4"}
        up2 = self.store.update_scene_row(None, wb["id"], row_id, up1["version"], {
            "video_action": "attach",
            "video": v1
        })
        self.assertEqual(v1, up2["video"])
        self.assertIsNone(up2["final_video"])
        self.assertEqual([], up2["discarded_videos"])

        # 3. Set as final video
        up3 = self.store.update_scene_row(None, wb["id"], row_id, up2["version"], {
            "video_action": "set_final"
        })
        self.assertEqual(v1, up3["final_video"])

        # 4. Attach video V2 -> V1 should be moved to discarded_videos
        v2 = {"id": "v-2", "name": "take2.mp4", "path": "/media/take2.mp4"}
        up4 = self.store.update_scene_row(None, wb["id"], row_id, up3["version"], {
            "video_action": "attach",
            "video": v2
        })
        self.assertEqual(v2, up4["video"])
        self.assertEqual(1, len(up4["discarded_videos"]))
        self.assertEqual("take1.mp4", up4["discarded_videos"][0]["name"])

        # 5. Discard current video V2
        up5 = self.store.update_scene_row(None, wb["id"], row_id, up4["version"], {
            "video_action": "discard"
        })
        self.assertIsNone(up5["video"])
        self.assertEqual(2, len(up5["discarded_videos"]))

        # 6. Restore V1 from discarded_videos
        up6 = self.store.update_scene_row(None, wb["id"], row_id, up5["version"], {
            "video_action": "restore",
            "video_id": "v-1"
        })
        self.assertEqual("take1.mp4", up6["video"]["name"])
        self.assertEqual(1, len(up6["discarded_videos"]))
        self.assertEqual("take2.mp4", up6["discarded_videos"][0]["name"])

    def test_multi_video_in_single_beat(self):
        wb = self.store.create(None, "scene_beats", self.project_id, "op-multi", "BeatMultiVideo")
        doc = self.store.document(None, wb["id"])
        row_id = doc["sheets"][0]["scene_rows"][0]["id"]
        v1 = {"id": "v-1", "name": "clip1.mp4", "path": "/media/clip1.mp4"}
        v2 = {"id": "v-2", "name": "clip2.mp4", "path": "/media/clip2.mp4"}

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

        # 3. Set clip2 as final video
        up3 = self.store.update_scene_row(None, wb["id"], row_id, up2["version"], {
            "video_action": "set_final",
            "video_id": "v-2"
        })
        self.assertEqual("clip2.mp4", up3["final_video"]["name"])

        # 4. Discard clip1
        up4 = self.store.update_scene_row(None, wb["id"], row_id, up3["version"], {
            "video_action": "discard",
            "video_id": "v-1"
        })
        self.assertEqual(1, len(up4["videos"]))
        self.assertEqual("clip2.mp4", up4["videos"][0]["name"])
        self.assertEqual(1, len(up4["discarded_videos"]))
        self.assertEqual("clip1.mp4", up4["discarded_videos"][0]["name"])


if __name__ == "__main__":
    unittest.main()
