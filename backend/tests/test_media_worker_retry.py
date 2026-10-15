from __future__ import annotations

import json
import os
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

from services.media_worker.worker import (JOB_RETRY_BASE_SECONDS, MediaWorker,
    MAX_DOWNLOAD_CACHE_AGE_SECONDS, WorkerConfig)


class MediaWorkerRetryTests(unittest.TestCase):
    def test_failed_job_uses_persistent_backoff_without_storing_file_ids(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = WorkerConfig("https://api.example", "secret", "worker", "ffmpeg", "ffprobe", root)
            job = {"file_id": str(uuid.uuid4()), "revision_id": str(uuid.uuid4())}
            with patch("services.media_worker.worker.time.time", return_value=1_000):
                first = MediaWorker(config)
                first._record_job_failure(job)
                self.assertTrue(first._job_retry_deferred(job))

            retry_state = json.loads((root / "job-retry-state.json").read_text(encoding="utf-8"))
            self.assertNotIn(job["file_id"], json.dumps(retry_state))
            self.assertNotIn(job["revision_id"], json.dumps(retry_state))

            with patch("services.media_worker.worker.time.time", return_value=1_001):
                restarted = MediaWorker(config)
                self.assertTrue(restarted._job_retry_deferred(job))
            with patch("services.media_worker.worker.time.time",
                       return_value=1_000 + JOB_RETRY_BASE_SECONDS + 1):
                self.assertFalse(restarted._job_retry_deferred(job))

    def test_failed_source_cache_is_bounded_by_age_and_size(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            download_dir = root / "downloads"
            download_dir.mkdir()
            old = download_dir / "old.source"
            recent = download_dir / "recent.source"
            unrelated = download_dir / "keep.log"
            old.write_bytes(b"old")
            recent.write_bytes(b"new")
            unrelated.write_bytes(b"log")
            old.touch()
            recent.touch()
            os.utime(old, (10_000, 10_000))
            os.utime(recent, (49_900, 49_900))
            with patch("services.media_worker.worker.time.time", return_value=50_000), \
                 patch("services.media_worker.worker.MAX_DOWNLOAD_CACHE_AGE_SECONDS", 100):
                MediaWorker(WorkerConfig("https://api.example", "secret", "worker", "ffmpeg", "ffprobe", root))\
                    ._cleanup_download_cache()
            self.assertFalse(old.exists())
            self.assertTrue(recent.exists())
            self.assertTrue(unrelated.exists())

            first = download_dir / "first.source"
            second = download_dir / "second.source"
            first.write_bytes(b"1234")
            second.write_bytes(b"5678")
            os.utime(first, (49_000, 49_000))
            os.utime(second, (49_900, 49_900))
            with patch("services.media_worker.worker.time.time", return_value=50_000), \
                 patch("services.media_worker.worker.MAX_DOWNLOAD_CACHE_AGE_SECONDS", 100), \
                 patch("services.media_worker.worker.MAX_DOWNLOAD_CACHE_BYTES", 5):
                MediaWorker(WorkerConfig("https://api.example", "secret", "worker", "ffmpeg", "ffprobe", root))\
                    ._cleanup_download_cache()
            self.assertFalse(first.exists())
            self.assertTrue(second.exists())


if __name__ == "__main__":
    unittest.main()
