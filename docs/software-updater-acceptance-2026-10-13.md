# 原生客户端与更新链路 —— 真实 NAS 环境验收记录（2026-10-13）

本文记录在**生产 NAS 真实环境**上完成的端到端验收。与本地模拟不同，本次包含真实账号创建、
真实 SMB 共享访问和真实发布服务器。**未验证项在文末单独列出**，不在此处声称通过。

环境标识按仓库惯例脱敏（NAS 地址记为 `<NAS_LAN_IP>`，内部账号名不在此记录）。

---

## 1. 验收范围与结果

| 环节 | 结果 | 证据 |
|---|---|---|
| 员工注册 | **通过** | 生产 NAS 上真实创建账号：`useradd` + Samba 密码 + 加入 `group_assets` 与所属部门组；`id` 显示 `gid=group_assets`、补充组 `group_film` |
| 登录 | **通过** | 会话建立，客户端显示已登录身份 |
| 浏览生产文件 | **通过**（修复生产 ACL 后，见 §4） | 客户端列出真实素材目录；本部门可写、跨部门只读、个人空间隔离 |
| 应用内更新 | **通过** | 从生产发布服务器拉取签名清单 → 验签 → 下载 142 MB → SHA-256 校验 → 点击 → 更新器切换版本 → 健康检查 → `transaction state = healthy` → 自动重启为新版本 |

发布信任链在生产环境得到验证：客户端内嵌 ECDSA 公钥成功验证了由离线私钥签名的清单。

## 2. 生产变更清单（均已单独批准）

| 变更 | 内容 | 回退 |
|---|---|---|
| 后端重新部署 | `/opt/muying-v2/releases/20261013-01`，`current` 软链接原子切换，`muying-v2.service` 重启 | `ln -sfn /opt/muying-v2/releases/20261012-01 /opt/muying-v2/current && systemctl restart muying-v2.service` |
| 发布目录 | 新建 `/volume1/muying-releases`（`root:muying-v2`，目录 0555 / 文件 0444）；`server.env` 增加 `MUYING_V2_UPDATE_RELEASES_DIR` | 删除该目录并从 `server.env` 移除该行 |
| 生产 ACL | `/volume1/Assets` 及四个空间目录**各新增 1–2 条 ACE**（详见 §4） | 执行前快照存于 NAS `/root/assets-acl-before.txt`，用 `ugacltool del_one <path> <index>` 按索引回退 |

**未改动**：`user:123`、管理员的现有三条权限；`[Assets]` SMB 配置；`03_个人空间` 下的个人目录；
生产 SMB 主服务未重启（`ugacltool` 为在线修改）。

## 3. 本次发现并修复的缺陷（7 项）

前 4 项为**产品缺陷**，本地模拟无法测出或测出成本极高。

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 1 | 任何更新在读取更新包之前就失败 | .NET SDK 给 `InformationalVersion` 追加 `+<git sha>`，`ProductVersion` 因此不是纯三段版本；更新器按 `.` 分割要求三段落整数 | `desktop/Directory.Build.props` 统一版本来源并关闭 source-revision 追加；新增 `VersionStamp.NormalizeProductVersion` |
| 2 | **安装器与更新器点击后毫无反应** | `App.xaml` 未写 `Startup="OnStartup"`；`OnStartup(object, StartupEventArgs)` 是事件处理器签名而非重写，生成的 `Main` 只调用 `InitializeComponent(); Run();` —— 消息循环启动、窗口从不创建、handler 永不执行 | 两个 `App.xaml` 均补 `Startup="OnStartup"` |
| 3 | 更新器窗口构造时崩溃 | `ProgressBar.Value` 默认 TwoWay 绑定到只读属性 `Progress` | 显式 `Mode=OneWay` |
| 4 | 修掉 #3 后**仍然崩溃** | 同一窗口内 `<Run Text="{Binding Progress}">` —— **`Run.Text` 同样默认 TwoWay**（反射实测 `BindsTwoWayByDefault = True`） | 显式 `Mode=OneWay`；并全仓库审计所有默认 TwoWay 目标 |
| 5 | 换账号登录必定失败（1219），即使已清凭据 | Windows 的凭据冲突判定是**按服务器**而非按共享；`Disconnect()` 只取消配置里那一个共享，而冲突来自同一 NAS 的**其它**共享 | `Disconnect()` 改为枚举本机到该服务器的**全部**连接并逐个取消 |
| 6 | 安装器播种了客户端会拒绝的配置，且**静默禁用更新** | `ShareRoot` 未校验 UNC 格式；客户端启动时拒绝该配置，而该失败发生在构建主视图模型期间，更新协调器因此从未创建 | 安装器按 `NasClientSettings.Load` 的规则校验 `ShareRoot` |
| 7 | 文件页"返回上级"按钮空白（传输页取消按钮同样） | XAML 引用了 `ChevronLeft` 与 `X`，而 `LucideIcon` 图标表中不存在这两个名字；控件对未知名字**静默不绘制** | 补齐两个图标；新增 `--check-icons` 自动核对 XAML 图标名与图标表 |

> 缺陷 2 还揭示：`desktop/MuYingDrive.Updater` **自写出来起就无法运行**——此前文档中"已实现、未做端到端验证"低估了它，实为没有可用的启动入口。

## 4. 生产 ACL 缺口（已修复）

**症状**：新注册账号能连接 `\\<NAS_LAN_IP>\Assets`（SMB `valid users` 含 `@group_assets`），但**读不到任何目录**。

**根因**：`/volume1/Assets` 及全部子目录的 UGOS ACL 只有管理员组、`user:123`、管理员账号三条，
**没有任何部门组或 `group_assets` 条目**。即员工权限模型从未配置到生产共享目录上。

**修复**（只新增，未修改或删除现有条目）：

| 路径 | 新增 |
|---|---|
| `/volume1/Assets` | `group_assets` 只读进入，**不继承** |
| `00_公共素材` | `group_film` + `group_writing` 读写，继承 |
| `01_影视资源库` | `group_film` 读写 / `group_writing` 只读，继承 |
| `02_文学创作库` | `group_writing` 读写 / `group_film` 只读，继承 |
| `03_个人空间` | `group_assets` 仅可穿过，**不继承** |

**以真实员工身份实测**：本部门写入成功、跨部门写入被拒（`Permission denied`）、个人空间他人目录不可读。

**关于既有文件**：本轮新增的 ACE **不追溯**目录内已存在的文件。实测表明**未造成影响**——既有内容权限位为
`777`，本身即可读。但这是一个**已知残余风险**：`777` 比新规则宽松，若将来把旧内容移入受限目录，
跨部门只读将失效。记录于此，本轮不处理。

## 5. 未验证项

以下**没有**经过真实环境验证，不得据此声称通过：

- **服务端 `release_store.py` 语义**：写权限位拒绝、`O_NOFOLLOW` 路径防御。该模块依赖
  `O_DIRECTORY`/`O_NOFOLLOW`/`dir_fd`，在本机 Windows 上不可运行（已实测），本次仅在生产 NAS 上间接生效，
  未做定向攻击性验证。
- **更新失败与回滚路径**：安装失败、版本降级、路径穿越包、健康检查失败回滚——本地做过确定性回滚验证，
  **未在生产 NAS 上验证**。
- **逆向代理 Range 行为**：生产发布地址直连，未经过反向代理。
- **多用户并发**：未验证多个员工同时更新。
- **Windows 10/11 视觉与 DPI**：仅在单机单主题下人工确认。
- **`vfs_ugacl.c:875` 断言**：仍未修复（详见 `ugos-acl-validation.md`），真实使用中可能偶发断连。
- **免登录参观通道**：仍为设计稿，未实现。
- **既有内容 777 的长远影响**：见 §4。

## 6. 后续

- 更新检查间隔已由 8 小时调整为 15 分钟，并清理被取代的缓存包（`a066d12`），使已发布的修复能较快触达
  开着的客户端，且用户只需更新一次。
- 2026-10-14 进一步缩短为 **5 分钟**，并加上 **±20% 抖动**（`UpdateCheckSchedule`，放在 UpdateCore
  以便测试）—— 没有抖动时，同一时间开机、或同一批安装的客户端，会在每个周期几乎同一秒一起打 NAS。
  同时 manifest 轮询改为发送 `If-None-Match`：服务端一直支持 304，但客户端从未带过这个头，
  所以每次检查都在传输并重新验签整份信封。首次检查仍立即执行，不等待。
- 本文未覆盖的更早的 ACL 隔离验证见 `docs/ugos-acl-validation.md`。
