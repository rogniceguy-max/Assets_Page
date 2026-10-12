# 沐影盘软件更新模块（独立开发分支）

状态：设计与接口契约草案；**未接入生产、未提供可安装更新包**。

## 当前运行时和正式发布策略（已确认）

- **现阶段固定为 .NET 8 / `net8.0-windows`**，客户端和独立更新器坚持原生 WPF + XAML + C#，不在本分支擅自切换到 .NET 10。
- 员工端正式打包目标为 **Windows x64 自包含（self-contained）安装包**：内含所需 .NET/WPF 运行时，不要求员工单独安装 .NET Desktop Runtime、SDK 或开发工具。自包含不等于已有正式安装器；真实产物、签名、安装权限和干净 Windows 设备必须验收。
- **将来再考虑 .NET 10：** 先建立独立兼容性测试分支/环境，验证 UI、依赖、NAS 登录和 SMB 权限、断点传输、发布包安全校验、安装器、自动更新与回滚、Windows 10/11 场景；测试通过后再由用户批准向员工推送，不能直接对员工触发框架大版本升级。
- .NET 8 后续终止官方支持后存在安全维护风险；应跟踪支持期限和升级计划，但风险提醒不等于授权 AI 立即迁移。

## 已确认产品规则（Notion）

- 安装包及版本发布信息由 NAS 托管。
- Windows 客户端使用中检查并预下载。
- 用户可手动选择立即更新；不选择则在软件**完全退出后**由独立进程安装。
- 不得中断未保存工作或正在进行的传输。
- 验证可信发布者、版本、包 SHA-256 和发布清单数字签名；失败时恢复旧版本或保持旧版本可用。
- 更新 UI 不应占用登录页、主窗口顶部品牌区或常驻底部状态栏。

## 代码基线

分支从 `codex/nas-smb-v2` 创建：

- `desktop/MuYingDrive.NativeWpf/`：正式候选原生 WPF 客户端（.NET 8）。
- `desktop/MuYingDrive.NativeWpf/MainWindow.xaml.cs`：已有 `Closing -> ShutdownAsync -> Close` 生命周期。
- `desktop/MuYingDrive.NativeWpf/ViewModels/MainViewModel.cs`：启动和退出流程入口。
- `desktop/MuYingDrive.NativeWpf/ViewModels/TransferViewModel.cs`：`PauseAllAsync` 及任务列表。
- `desktop/MuYingDrive.NativeWpf/Services/ResumableTransferEngine.cs`：账号隔离、断点及传输检查点。
- `backend/v2/server.py`：HTTPS API；目前仅有认证与健康接口。
- `backend/v2/config.py`：环境配置；当前没有发布包目录配置。
- `desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj`：尚无正式安装与升级打包链路。

## 拟定结构（尚未实现）

```
backend/v2/update/
  manifest.py              # 发布清单生成、校验和版本检查
  release_store.py         # 只读版本索引和包文件访问
desktop/MuYingDrive.NativeWpf/Updates/
  UpdateClient.cs          # 检查最新发布清单
  UpdateDownloadService.cs # 流式预下载、限速、断点和哈希复核
  UpdateCoordinator.cs     # 状态机：检查、准备、就绪、等待退出、安装
desktop/MuYingDrive.Updater/
  Program.cs               # 进程外更新器；只允许已验证的本地暂存包
  InstallTransaction.cs    # 原子切换及失败回滚
docs/
  software-updater.md
```

## 建议 API（拟定）

- `GET /api/v2/updates/stable/manifest`：返回签名发布清单，明确版本、平台/架构、大小、校验和、发布时间及包标识。
- `GET /api/v2/updates/packages/{opaque_id}`：NAS 托管包，支持 `Range`、`ETag`、明确长度与缓存策略。不可将客户端传入字符串拼成磁盘路径。
- 不在普通员工 API 暴露发布/覆盖接口；发布是管理员单独部署动作。
- 不应仅凭 HTTPS 证书或 SHA-256 就认定内容可信：在客户端内置受信任发布公钥，独立验证清单签名，限制版本降级，安装前再次校验实际包哈希。

## 状态机和更新边界

`Idle -> Checking -> Downloading -> Verifying -> Ready -> WaitingForExit -> Installing -> Succeeded`

- 检查失败或无更新不得阻碍正常登录和文件访问。
- 下载过程存放在独立的 `%LOCALAPPDATA%\MuYingDrive\Updates`，不得与 SMB 文件传输缓存/业务素材混用；文件完成并验证后才标记 `Ready`。
- 点击「立即更新」先询问/确认当前传输如何安全暂停并落盘检查点；有未完成且无法安全暂停的任务时禁止强制安装。
- 关闭窗口不等同于彻底退出（若日后引入托盘）；启动进程外更新器前先完成 `ShutdownAsync` 并释放句柄。
- 原有 `MainWindow.Closing` 是可用的协调点，但不得在旧版退出流程与新安装流程中重复关闭/死锁。
- 安装器需要等待客户端进程完全退出，检查包版本和权限，在用户可写的安装布局执行原子版本目录切换；旧版本保留至新版本健康确认。
- 发生安装错误时回滚或恢复旧版本；不允许删除用户账户、会话、传输检查点、个人设置或业务缓存。
- 首版先确定 per-user / per-machine、是否需要 UAC、安装器形式和是否托盘驻留；不确定前不得声称可在所有 Windows 电脑上静默更新。

## 验收测试

1. NAS 无法访问、manifest 格式异常、签名错误、SHA 不符：拒绝更新且应用正常运行。
2. 下载过程中断网/断电、磁盘不足：安全恢复或重试；不得把部分文件当作有效包。
3. 更新准备好但正有 SMB 上传、下载、外部 Office/PSD 文件打开：不得强制中断或覆盖。
4. 手动立即更新、正常退出后更新、取消更新、重启后待更新恢复。
5. 模拟安装失败、版本降级、路径穿越、恶意篡改、客户端异常退出及回滚。
6. Windows 10/11 干净环境，安装权限、运行时依赖、无管理员账号场景。
7. 新版启动健康检查成功才清理旧版本；发布与回滚均提供可审计日志。

## 与现网隔离

- 不修改目前正在运行的 `muying-hub.service`、`muying-v2.service`、Samba 配置/ACL、账号库、生产存储。
- 在独立 Windows 测试安装目录及 NAS 测试发布目录验证。
- 仅当打包、安全验证及回滚闭环通过后，才考虑合并和生产部署。

## 更新界面设计与视觉锁定（本分支执行约束）

更新**专属 UI**（含提醒、验证、安装、失败回滚状态及独立更新程序的进度窗口）属于 `codex/software-updater`，不交给 `codex/ui-refinement` 另造一套。主应用沿用原生 WPF；独立更新程序也必须使用 **.NET 8 + 原生 WPF/XAML/C#**，禁止 WebView2、React、HTML、Tauri/Electron 或内嵌浏览器界面。

**完整且应直接实施的窗口蓝图：** [独立更新器 UI 结构与验收规范](updater-ui-spec.md)。该规范明确紧凑窗口（基线宽约 460 DIP）、标题/状态/说明/进度条/阶段/操作区的固定排列、真实进度规则、成功失败和回滚文案，以及深浅主题与原生键盘交互。不得自行改为全屏安装器、多步欢迎向导、常驻软件更新导航或新视觉语言。

就绪提醒提供「立即更新」「退出后更新」，具体挂载在主窗口的哪一处仍需产品 UI 评审（Notion 尚未确认）；**不得擅自新增永久更新中心、顶栏按钮或登录页版本区域**。本次仅冻结专用独立更新器的布局基线，不改变 Notion 尚待确认的入口位置。

