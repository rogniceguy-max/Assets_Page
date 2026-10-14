"""
MuYing Assets Hub - Mac mini M4 Media Worker Daemon
===================================================
Autonomous background media worker running on Mac mini (Apple Silicon M4).
Responsible for:
1. Monitoring & indexing NAS asset shares (mounted via SMB).
2. Generating high-performance WebP thumbnails (640px) using M4 hardware acceleration (VideoToolbox / FFmpeg).
3. Extracting exact media metadata (resolution, duration, aspect ratio, codec).
4. Caching outputs directly back to each space's `.muying_cache/thumbs/` directory on the NAS.
"""

import os
import sys
import time
import json
import hashlib
import logging
import subprocess
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
from typing import Optional, Dict, Any, List

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S'
)
logger = logging.getLogger("MediaWorker")

VIDEO_EXTS = {'.mp4', '.mov', '.mkv', '.avi', '.webm', '.flv', '.ts', '.m4v', '.wmv'}
IMAGE_EXTS = {'.png', '.jpg', '.jpeg', '.webp', '.bmp', '.tiff', '.tif', '.psd'}
MEDIA_EXTS = VIDEO_EXTS | IMAGE_EXTS

# Spaces to monitor under NAS root
TARGET_SPACES = ["00_公共素材", "01_影视资源库", "02_文学创作库", "项目文档"]


class MediaWorker:
    def __init__(self, nas_root: str, ffmpeg_bin: str = "/usr/local/bin/ffmpeg", ffprobe_bin: str = "/usr/local/bin/ffprobe", max_workers: int = 4):
        self.nas_root = Path(nas_root)
        self.ffmpeg_bin = ffmpeg_bin
        self.ffprobe_bin = ffprobe_bin
        self.max_workers = max_workers
        self.running = True

    def ensure_nas_mounted(self) -> bool:
        """Verifies if the NAS share is mounted, attempts to mount if missing."""
        if self.nas_root.exists() and any(self.nas_root.iterdir()):
            return True
        logger.warning(f"NAS root {self.nas_root} is not mounted or empty. Attempting auto-mount...")
        cmd = [
            "mount_smbfs",
            "//19945022511:Andy66668888@192.168.1.153/Assets",
            str(self.nas_root)
        ]
        try:
            subprocess.run(cmd, check=True)
            logger.info("NAS Assets share mounted successfully.")
            return True
        except Exception as e:
            logger.error(f"Failed to mount NAS Assets share: {e}")
            return False

    @staticmethod
    def get_cache_dir(space_dir: Path) -> Path:
        cache_dir = space_dir / ".muying_cache" / "thumbs"
        cache_dir.mkdir(parents=True, exist_ok=True)
        return cache_dir

    @staticmethod
    def compute_rel_hash(rel_path: str) -> str:
        """Normalizes path using forward slashes and returns 16-char hex sha256."""
        norm_path = rel_path.replace("\\", "/").strip("/")
        return hashlib.sha256(norm_path.encode("utf-8")).hexdigest()[:16]

    def is_file_settled(self, file_path: Path, mtime: int) -> bool:
        """Checks if a file has finished transferring. Only waits if modified within 5s."""
        try:
            if time.time() - mtime > 5.0:
                return True
            s1 = file_path.stat().st_size
            time.sleep(1.0)
            s2 = file_path.stat().st_size
            return s1 == s2 and s1 > 0
        except Exception:
            return False

    def probe_media(self, file_path: Path) -> Optional[Dict[str, Any]]:
        """Extracts resolution, duration, codec via ffprobe."""
        cmd = [
            self.ffprobe_bin,
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            str(file_path)
        ]
        try:
            res = subprocess.check_output(cmd, timeout=30)
            data = json.loads(res)
            video_stream = next((s for s in data.get("streams", []) if s.get("codec_type") == "video"), None)
            duration = float(data.get("format", {}).get("duration", 0.0))
            width = int(video_stream.get("width", 0)) if video_stream else 0
            height = int(video_stream.get("height", 0)) if video_stream else 0
            codec = video_stream.get("codec_name", "") if video_stream else ""
            
            aspect_ratio = round(width / height, 4) if height > 0 else 1.0
            return {
                "width": width,
                "height": height,
                "aspect_ratio": aspect_ratio,
                "duration": duration,
                "codec": codec
            }
        except Exception as e:
            logger.warning(f"ffprobe failed for {file_path.name}: {e}")
            return None

    def process_file(self, space_dir: Path, file_path: Path) -> bool:
        """Processes a single file, generating WebP thumbnail and JSON metadata."""
        try:
            rel_path = str(file_path.relative_to(space_dir))
        except ValueError:
            return False

        # Ignore files inside any cache folder or temp files
        if ".muying_cache" in rel_path or file_path.name.startswith((".", "~$")):
            return False

        ext = file_path.suffix.lower()
        if ext not in MEDIA_EXTS:
            return False

        rel_hash = self.compute_rel_hash(rel_path)
        cache_dir = self.get_cache_dir(space_dir)

        try:
            st = file_path.stat()
            mtime = int(st.st_mtime)
            size = st.st_size
        except OSError:
            return False

        thumb_name = f"{rel_hash}_640.webp"
        meta_name = f"{rel_hash}.json"
        thumb_path = cache_dir / thumb_name
        meta_path = cache_dir / meta_name

        # If up-to-date thumbnail and metadata exist, skip immediately
        if thumb_path.exists() and meta_path.exists() and thumb_path.stat().st_size > 0:
            try:
                with open(meta_path, "r", encoding="utf-8") as f:
                    meta = json.load(f)
                if meta.get("mtime") == mtime and meta.get("size") == size:
                    return False
            except Exception:
                pass

        # Ensure file has finished uploading
        if not self.is_file_settled(file_path, mtime):
            logger.info(f"File {file_path.name} is still writing/transferring, postponing...")
            return False

        t0 = time.time()
        logger.info(f"Processing [{ext}] {rel_path} ({size / (1024*1024):.1f}MB)...")

        meta = self.probe_media(file_path) or {}
        duration = meta.get("duration", 0.0)

        # Decide seek time for video: skip initial black frame if possible
        seek_sec = "0"
        if ext in VIDEO_EXTS:
            if duration > 10:
                seek_sec = "3.0"
            elif duration > 2:
                seek_sec = f"{duration * 0.1:.2f}"

        # Generate 640px WebP
        tmp_out = f"/tmp/{thumb_name}"
        ffmpeg_cmd = [
            self.ffmpeg_bin,
            "-y",
            "-ss", seek_sec,
            "-i", str(file_path),
            "-vframes", "1",
            "-vf", "scale=640:-2",
            "-c:v", "libwebp",
            "-quality", "82",
            tmp_out
        ]

        try:
            subprocess.run(ffmpeg_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True, timeout=60)
            
            # Atomic move into cache directory
            if os.path.exists(tmp_out):
                subprocess.run(["mv", "-f", tmp_out, str(thumb_path)], check=True)
                
                # Write metadata JSON
                meta_content = {
                    "rel_path": rel_path.replace("\\", "/"),
                    "rel_hash": rel_hash,
                    "mtime": mtime,
                    "size": size,
                    "thumb_file": thumb_name,
                    "width": meta.get("width", 0),
                    "height": meta.get("height", 0),
                    "aspect_ratio": meta.get("aspect_ratio", 1.0),
                    "duration": duration,
                    "codec": meta.get("codec", "")
                }
                with open(meta_path, "w", encoding="utf-8") as f:
                    json.dump(meta_content, f, ensure_ascii=False, indent=2)

                elapsed = (time.time() - t0) * 1000
                thumb_kb = thumb_path.stat().st_size / 1024
                logger.info(f"Done in {elapsed:.1f}ms -> {thumb_name} ({thumb_kb:.1f}KB, ratio={meta.get('aspect_ratio')})")
                return True
        except Exception as e:
            logger.error(f"Failed to generate thumbnail for {file_path.name}: {e}")
            if os.path.exists(tmp_out):
                try:
                    os.remove(tmp_out)
                except OSError:
                    pass
            return False

    def scan_space(self, space_name: str):
        """Scans a space directory on the NAS and processes any unindexed files."""
        space_dir = self.nas_root / space_name
        if not space_dir.exists():
            return

        media_files: List[Path] = []
        for root, dirs, files in os.walk(space_dir):
            # Do not traverse into cache directories
            if ".muying_cache" in dirs:
                dirs.remove(".muying_cache")
            
            for f in files:
                ext = os.path.splitext(f)[1].lower()
                if ext in MEDIA_EXTS:
                    media_files.append(Path(root) / f)

        if not media_files:
            return

        with ThreadPoolExecutor(max_workers=self.max_workers) as executor:
            executor.map(lambda fp: self.process_file(space_dir, fp), media_files)

    def run_loop(self, poll_interval: int = 10):
        """Continuous worker loop: checks mount, sweeps spaces for new/modified files."""
        logger.info(f"MediaWorker started on {self.nas_root} (workers={self.max_workers}, interval={poll_interval}s)")
        while self.running:
            try:
                if self.ensure_nas_mounted():
                    for space in TARGET_SPACES:
                        self.scan_space(space)
            except Exception as e:
                logger.error(f"Error in scan loop: {e}")
            time.sleep(poll_interval)


if __name__ == "__main__":
    nas_dir = sys.argv[1] if len(sys.argv) > 1 else "/Users/muying12345/NAS_Assets"
    worker = MediaWorker(nas_dir)
    worker.run_loop(poll_interval=10)
