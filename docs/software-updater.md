# 沐影盘软件更新模块（独立开发分支）

状态：架构和界面已在本分支实现；**未接入生产、未配置发布信任密钥、未提供可安装更新包**。客户端在没有受信任公钥和 Authenticode 指纹时 fail closed，不会把任何版本标记为就绪。

## 当前运行时和正式发布策略（已确认）

- **现阶段固定为 .NET 8 / `net8.0-windows`**，客户端和独立更新器坚持原生 WPF + XAML + C#，不在本分支擅自切换到 .NET 10。
- 员工端正式打包目标为 **Windows x64 自包含（self-contained）安装包**：内含所需 .NET/WPF 运行时，不要求员工单独安装 .NET Desktop Runtime、SDK 或开发工具。自包含不等于已有正式安装器；真实产物、签名、安装权限和干净 Windows 设备必须验收。
- **将来再考虑 .NET 10：** 先建立独立兼容性测试分支/环境，验证 UI、依赖、NAS 登录和 SMB 权限、断点传输、发布包安全校验、安装器、自动更新与回滚、Windows 10/11 场景；测试通过后再由用户批准向员工推送，不能直接对员工触发框架大版本升级。
- .NET 8 后续终止官方支持后存在安全维护风险；应跟踪支持期限和升级计划，但风险提醒不等于授权 AI 立即迁移。

## 已确认产品规则（Notion）

- 安装包及版本发布信息由 NAS 托管。
- Windows 客户端使用中检查并预下载。
- 更新包在后台准备好后，用户点击工作区顶部、头像旁的更新图标即可安装；该图标就绪时显示小红点，不弹出提醒。
- 不得中断未保存工作或正在进行的传输。
- 验证可信发布者、版本、包 SHA-256 和发布清单数字签名；失败时恢复旧版本或保持旧版本可用。
- 更新 UI 不应占用登录页、主窗口顶部品牌区或常驻底部状态栏。

## 当前实现

- `desktop/MuYingDrive.UpdateCore/`：签名 envelope/schema 校验、版本防降级、SHA-256、Windows Authenticode 发布者固定、ZIP 路径/链接/展开大小防护、更新路径策略和跨进程启动/安装锁。
- `desktop/MuYingDrive.NativeWpf/`：独立检查、可续传下载和校验；更新就绪时在账户头像旁显示更新图标和小红点，单击直接开始。启动时会在后台重试本机待发送的匿名更新失败报告。安装目标按用户确认使用 `%LOCALAPPDATA%\Programs\MuYingDrive`；缓存位于 `%LOCALAPPDATA%\MuYingDrive\Updates`。
- `desktop/MuYingDrive.Updater/`：独立的 .NET 8 WPF 进程。等该安装目录下所有客户端退出后复核清单和包，允许在安装切换前取消，展开到同卷暂存目录，保留旧目录，验证新进程启动并以 0 退出后才把事务记为成功；失败时尝试停止专用健康检查进程并回滚，旧版无法恢复时保留新旧文件供恢复。成功后自动启动沐影盘；进度窗在成功/失败后隐藏进度条，避免终态仍播放动画。更新事务状态保存在 JSON journal，状态变化另追加到 JSONL 事件日志。
- `backend/v2/release_store.py` 和 `server.py`：只读稳定清单和固定 opaque ID 包读取端点；支持 ETag、条件请求和单段 Range。普通客户端没有发布/覆盖接口。
- `backend/v2/update_failure_reports.py` 和 `server.py`：`POST /api/v2/diagnostics/update-failures` 匿名最小启动失败报告接口；仅接受更新 ID、来源/目标版本、阶段、固定错误码和 UTC 时间。保存在 V2 私有 SQLite 数据库，去重，最多保留 1,000 条且不超过 30 天；不存账户、设备、路径、堆栈或文件内容。报告正文不含身份信息；服务只在内存中使用来源 IP 限流，不写应用日志或报告表。
- 客户端待发送报告位于 `%LOCALAPPDATA%\MuYingDrive\Updates\reports\outbox`，最多 50 条、保留 30 天；更新器立即尝试发送，失败后由下一次主客户端启动重试。
- `desktop/publish-update.ps1`：Windows x64 自包含发布、Authenticode 签名、ZIP/SHA-256 和 ECDSA 签名清单生成。私钥和发布证书必须来自发布管理员的本地安全存储。

发布清单是包含 `payload` 和 `signature` 的 JSON envelope。`payload` 是 UTF-8 JSON 的 Base64，ECDSA P-256/SHA-256 签名覆盖这些**原始 payload 字节**。负载固定为 stable、Windows x64，并包含版本、32 位小写十六进制 package ID、字节数、SHA-256 和 UTC 发布时间。包通过 `/api/v2/updates/packages/{package_id}` 读取；下载使用签名 SHA-256 作为强 ETag 和 Range 的 If-Range。

发布目录约定为：

```text
<MUYING_V2_UPDATE_RELEASES_DIR>/
  stable.manifest.json
  packages/<32-hex-package-id>.zip
```

发布操作必须先在同一文件系统的临时路径写完并校验 ZIP，再以新的唯一 package ID 原子发布；已发布的包文件不可原地覆盖，NAS 服务账号对包目录应只读。后端会拒绝带 POSIX 写权限位的包文件。最后原子替换 `stable.manifest.json`。若有管理员在传输期间改动存储文件，客户端仍会重新校验完整签名 SHA-256 并拒绝安装；此举可能中断下载，因此发布流程必须用新 ID 和原子替换。

更新器与主客户端在 `%LOCALAPPDATA%\MuYingDrive` 使用两个文件句柄协调启动：多个普通客户端可共享客户端锁；更新器获得互斥安装锁和排他的客户端锁后，新的普通客户端无法启动。健康检查通过更新专用参数启动，不取得普通客户端锁。第一次升级旧版客户端时若客户端锁文件不存在，更新器会在目标进程退出后安全创建。

当前 `desktop/MuYingDrive.UpdateCore/Trust/` 中的公钥和指纹文件只是空配置说明。需先写入 ECDSA P-256 发布公钥及 Authenticode 证书的 SHA-256 指纹，再使用匹配的私钥和 Windows 证书存储中的签名证书构建发布。私钥不得放入仓库或 NAS。发布脚本不会自动部署到 NAS；发布目录由管理员单独指定。

## 代码基线

分支从 `codex/nas-smb-v2` 创建：

- `desktop/MuYingDrive.NativeWpf/`：正式候选原生 WPF 客户端（.NET 8）。
- `desktop/MuYingDrive.NativeWpf/MainWindow.xaml.cs`：已有 `Closing -> ShutdownAsync -> Close` 生命周期。
- `desktop/MuYingDrive.NativeWpf/ViewModels/MainViewModel.cs`：启动和退出流程入口。
- `desktop/MuYingDrive.NativeWpf/ViewModels/TransferViewModel.cs`：`PauseAllAsync` 及任务列表。
- `desktop/MuYingDrive.NativeWpf/Services/ResumableTransferEngine.cs`：账号隔离、断点及传输检查点。
- `backend/v2/server.py`：HTTPS API；现已提供稳定清单和 opaque ID 包的只读 GET 路由。
- `backend/v2/config.py`：通过可选 `MUYING_V2_UPDATE_RELEASES_DIR` 指向发布目录。
- `desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj`：Release 目标为 win-x64、自包含；`desktop/publish-update.ps1` 负责本地生成已签名发布包。

## 代码结构

```
backend/v2/release_store.py
desktop/MuYingDrive.UpdateCore/
desktop/MuYingDrive.NativeWpf/Services/UpdateCoordinator.cs
desktop/MuYingDrive.Updater/Services/InstallTransaction.cs
desktop/publish-update.ps1
docs/
  software-updater.md
```

## 更新 API

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
- 用户已确定首版采用 per-user 安装：`%LOCALAPPDATA%\Programs\MuYingDrive`，不请求 UAC。更新器只接受该固定目标路径；其他安装布局会 fail closed。当前没有托盘驻留；客户端关闭窗口即按现有生命周期退出。

## 验收测试

标注为**已通过**的项附实际证据；其余一律为**未验证**。完整记录见
[`software-updater-acceptance-2026-10-13.md`](software-updater-acceptance-2026-10-13.md)。

1. NAS 无法访问、manifest 格式异常、签名错误、SHA 不符：拒绝更新且应用正常运行。
   **已通过（本地）**：签名篡改、错误密钥重签、SHA 不符、大小不符、截断下载、版本降级均被正确拒绝；
   应用保持可用、不显示红点、坏包被删除。
2. 下载过程中断网/断电、磁盘不足：安全恢复或重试；不得把部分文件当作有效包。
   **部分通过（本地）**：截断下载与 Range 续传已验证，部分文件从不被当作有效包；**磁盘不足未验证**。
3. 更新准备好但正有 SMB 上传、下载、外部 Office/PSD 文件打开：不得强制中断或覆盖。**未验证**。
4. 点击头像旁图标立即更新、取消更新、客户端重启后待发送失败报告重试。
   **点击更新已通过（生产）**；**取消更新与失败报告重试未验证**。
5. 模拟安装失败、版本降级、路径穿越、恶意篡改、客户端异常退出及回滚。
   **部分通过（本地）**：确定性回滚已验证（健康检查失败后旧版本恢复、失败目录隔离）；
   **生产环境未验证**，其余故障注入未验证。
6. Windows 10/11 干净环境，安装权限、运行时依赖、无管理员账号场景。
   **部分通过**：per-user 安装与"全程无 UAC 提示"已在单机确认；**干净环境与无管理员账号场景未验证**。
7. 新版启动健康检查成功才清理旧版本；发布与回滚均提供可审计日志。
   **已通过（生产）**：事务日志 `state=healthy`、健康标记写入、旧版本以 `MuYingDrive.previous-<id>` 保留、
   journal 与 JSONL 事件日志齐全。

## 与现网隔离

- 不修改目前正在运行的 `muying-hub.service`、`muying-v2.service`、Samba 配置/ACL、账号库、生产存储。
- 在独立 Windows 测试安装目录及 NAS 测试发布目录验证。
- 仅当打包、安全验证及回滚闭环通过后，才考虑合并和生产部署。

## 发布验收状态（2026-10-13 更新）

已完成：

- **ECDSA 发布公钥已配置并内嵌**（`MuYingDrive.UpdateCore/Trust/`），私钥离线保存、不入仓库与 NAS。
  生成脚本：`desktop/tools/New-ReleaseSigningKey.ps1`（显式指定 P-256 曲线）。
- **Authenticode 按产品决定跳过**：更新信任锚为 ECDSA 清单签名 + 整包 SHA-256。安装器 exe 无签名，
  需随包公布其 SHA-256 并经可信渠道分发（`publish-installer.ps1` 已自动打印）。
- **首次安装产物已实现**：`desktop/MuYingDrive.Bootstrap`（原生 WPF、per-user、免 UAC、无向导），
  从 NAS 拉取客户端包并**校验签名清单后才安装**。
- **生产发布目录已上线并验证**：`MUYING_V2_UPDATE_RELEASES_DIR` 已配置，清单与包均为 200，
  包文件只读、属主 `root:muying-v2`。
- **独立 Windows x64 自包含产物已实测**：版本切换、健康握手、恢复日志、旧版本清理均在生产通过。

## 交给同事使用前必须知道的事（2026-10-13 审计结论）

对"把安装包发给局域网内他人"这条路径做过一次逐环节审计（37 项确认、7 项被推翻，含本地桩服务实测复现）。
结论与据此做出的改动：

- **安装包不携带客户端本体**。它在安装时从 NAS 拉取当前 stable 版本，验 ECDSA 清单签名与整包 SHA-256
  后才写入磁盘。因此：**安装那一刻必须能连到 NAS**（同一局域网内），且**安装包自身的版本号与装出来的
  客户端版本无关**——NAS 发布什么就装什么。
- **`nas-client.json` 不随更新通道下发**，也永远不会被更新覆盖。它保存 NAS 地址、TLS pin 与
  `CredentialProfile`。因此重装（而非更新）是改变地址或 pin 的唯一途径——见下方证书轮换。
  安装器现在会在重装时**刷新地址/pin/ShareRoot 并保留 `CredentialProfile`**
  （`MuYingDrive.UpdateCore/ClientConfigurationMerge.cs`，有单元测试覆盖），否则轮换证书后
  老机器会一直拿旧 pin 去连，且无解。
- **安装包无 Authenticode 签名**，从邮件/网页取得会带 Mark-of-the-Web，触发 SmartScreen 的
  「Windows 已保护你的电脑 / 未知发布者」。**经局域网共享盘分发通常不带该标记**，因此
  **推荐走共享盘而不是邮件**；随包应公布 SHA-256 供对方核对。
- **桌面快捷方式默认创建**（`--no-desktop-shortcut` 可关；`--quiet` 静默部署默认不建）。
  安装过程中可「取消」，窗口的 X 也会请求取消并等待安全停止后才关闭。
- **证书 pin 不匹配不再报成网络故障**：TLS 校验失败会明确说明证书可能已更换或连接被冒充，
  与"连不上服务器"区分开（`MuYingDrive.UpdateCore/UpdateFailureText.cs`，有单元测试覆盖）。
  下载中途断流也不再向用户抛出英文 CLR 异常文本。

仍需完成：

- **证书与密钥轮换流程**：TLS 叶子证书与 ECDSA 发布密钥的续期/轮换尚未定义。
  **当前 NAS 叶子证书（`CN` 为 NAS 的局域网地址）有效期至 2028-10-11**，而
  `CertificatePin.IsTrusted` **会强制校验证书有效期**（`MuYingDrive.UpdateCore/CertificatePin.cs`），
  因此这一天是硬期限：**届时所有已安装客户端将同时无法更新、也无法新装**。
  轮换需要多 pin / 多公钥支持，否则等同每台机器重装；到期前必须预留重新打包与重新分发时间。
- **密钥丢失恢复**：未定义。
- **反向代理 Range 行为**：生产直连，未经过反向代理验证。
- **发布管理员流程**：发布目录写入、旧版本保留期限仍需书面流程；运维须遵守不可原地覆盖包文件的约定，
  以确保强 ETag 与断点续传语义稳定。
- **安全/故障注入与 Windows 10/11 界面实测**：见上文验收清单，多项仍为未验证。

## 更新界面设计与视觉锁定（本分支执行约束）

更新**专属 UI**（含提醒、验证、安装、失败回滚状态及独立更新程序的进度窗口）属于 `codex/software-updater`，不交给 `codex/ui-refinement` 另造一套。主应用沿用原生 WPF；独立更新程序也必须使用 **.NET 8 + 原生 WPF/XAML/C#**，禁止 WebView2、React、HTML、Tauri/Electron 或内嵌浏览器界面。

**完整且应直接实施的窗口蓝图：** [独立更新器 UI 结构与验收规范](updater-ui-spec.md)。该规范明确紧凑窗口（基线宽约 460 DIP）、标题/状态/说明/进度条/阶段/操作区的固定排列、真实进度规则、成功失败和回滚文案，以及深浅主题与原生键盘交互。不得自行改为全屏安装器、多步欢迎向导、常驻软件更新导航或新视觉语言。

更新就绪入口放在文件工作区顶部、账户头像旁；仅在包签名、版本和 SHA-256 全部通过后显示红点，单击即可开始。不会弹更新横幅或 Toast，不新增主导航、登录页版本区域或底栏状态栏。

