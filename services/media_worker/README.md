# Mac mini M4 Media Worker (媒体处理旁路工蜂)

## 架构概述 (Architecture Overview)

在木影资产管理系统中，用户客户端（WPF 桌面端）与 NAS 存储之间的数据吞吐量极大（数十 GB 的 4K/1080p HEVC 成片和高分辨率分镜）。
传统方案由客户端直接通过 SMB 挂载读取原始超大文件并通过 Windows Shell COM 抽帧，存在以下三个严重瓶颈：
1. **网络与磁盘 IO 风暴**：每个视频文件需传输数十 MB 头信息，数十个文件同时访问拖垮 NAS 与内网。
2. **STA 单线程串行阻塞**：Windows `IShellItemImageFactory` 强依赖 COM 单线程公寓模型，即使开多线程也会被操作系统底层序列化，导致图片“一个接一个”极其缓慢地弹出。
3. **UI 布局重排卡顿**：因预先未知视频真实长宽比（默认 1.4），缩略图陆续加载完成后不断触发 WPF `JustifiedGalleryPanel` 测量与重排，视觉上频繁跳动。

### 旁路工蜂架构 (Sidecar Worker Pattern)

为了彻底解决以上问题，系统引入一台常驻局域网的高性能 **Mac mini M4** 作为独立媒体处理工蜂：
1. **自动监听与扫描**：Mac mini 开机自动挂载 NAS SMB 共享，后台守护进程定期巡检各业务空间（`00_公共素材`、`01_影视资源库`、`02_文学创作库`、`项目文档`）。
2. **M4 硬件加速抽帧**：利用 Apple Silicon M4 强大的硬件编解码单元与独立 ARM64 FFmpeg/VideoToolbox，以 200~400ms 的极速抽取 640px WebP 缩略图（体积仅约 20KB，压缩比超 8000:1）。
3. **元数据精确预提取**：通过 ffprobe 提取媒体真实分辨率、准确时长、编码格式及精确长宽比，写入 JSON 元数据。
4. **去中心化缓存回写**：直接写入各空间根目录下的 `.muying_cache/thumbs/`，避免跨盘符与权限隔离冲突，普通员工账号亦可极速只读访问。
5. **客户端毫秒级消费**：WPF 客户端通过纯流式 `BitmapImage` 并行并发解码 25KB WebP，并在初次加载时即匹配精确长宽比，实现**瞬间满屏秒开且 0 布局跳动**。

---

## 缓存目录与命名规则 (Cache Layout & Naming)

每个业务空间根目录下维护独立的 `.muying_cache/thumbs/`：
```text
\\192.168.1.153\Assets\
├── 00_公共素材\
│   └── .muying_cache\
│       └── thumbs\
│           ├── {rel_hash}_640.webp
│           └── {rel_hash}.json
└── 01_影视资源库\
    ├── 影视项目\...
    └── .muying_cache\
        └── thumbs\
            ├── {rel_hash}_640.webp
            └── {rel_hash}.json
```

- **相对路径哈希 (`rel_hash`)**：
  采用文件相对于当前业务空间的标准化路径（正斜杠 `/`，去除首尾斜杠），计算 SHA-256 并截取前 16 位小写十六进制字符串。
  示例：`影视项目/20260810孟婆/孟婆成片61集全/2.mp4` -> `5680c6210d92ab8d`。
- **缩略图文件**：`{rel_hash}_640.webp`
- **元数据文件**：`{rel_hash}.json`
  ```json
  {
    "rel_path": "影视项目/20260810孟婆/孟婆成片61集全/2.mp4",
    "rel_hash": "5680c6210d92ab8d",
    "mtime": 1787649352,
    "size": 226779767,
    "thumb_file": "5680c6210d92ab8d_640.webp",
    "width": 1920,
    "height": 1080,
    "aspect_ratio": 1.7778,
    "duration": 176.47,
    "codec": "hevc"
  }
  ```

---

## 守护进程部署与维护 (Deployment on Mac)

### 文件清单
- `worker.py`：Python 3 核心工蜂巡检与并发转码程序。
- `start.sh`：自动检测挂载与启动脚本。
- `com.muying.mediaworker.plist`：macOS launchd 守护配置。

### 常用命令
```bash
# 启动守护程序
nohup python3 -u /Users/muying12345/media_worker/worker.py > /Users/muying12345/Library/Logs/media_worker.log 2>&1 &

# 查看实时处理日志
tail -f /Users/muying12345/Library/Logs/media_worker.log

# 检查进程状态
ps aux | grep worker.py
```
