# 交接说明 —— 当前分支 `feat/mac-m4-media-worker`

> 初稿写于 2026-10-14。2026-10-16 更新：下文 NAS/project-helper 状态是当日历史快照；本轮只更新仓库源代码和文档，没有连接或修改生产 NAS、systemd 服务、ACL、项目文件或账号。
>
> 这份文件存在的原因：接手的人只能读到仓库。而仓库里 `README.md` 和 `AGENTS.md` 写的是另外两条
> 工作线（`codex/software-updater` / `codex/feedback-center`）的规则，**都不覆盖当前分支**，其中几行
> 还是**已经作废的**。照那几行做，会把已经删掉的东西加回来。

> **2026-10-16 架构更新：** 本文第 3–8 节记录的是更早的阶段，不能当作当前文件存储方案。当前方案以
> [`ARCHITECTURE.md`](../ARCHITECTURE.md) 和 [`docs/file-storage-v2.md`](file-storage-v2.md) 为准：
> 文件及工作簿元数据在 SQLite；内容通过 Rust Windows 传输代理和 NAS 传输服务；逻辑移动/删除/恢复不搬动或删除 NAS 对象。
> WPF 不直接访问 SMB；访客只读公共区、两个部门空间和项目数据，不访问个人空间。Mac M4 媒体工人经服务生成版本绑定的 WebP 缩略图。
> 访客及账号 helper 的旧实现已移除。生产 NAS、服务、ACL 和文件都没有在本轮修改。

---

## 1. 先搞清楚你在哪

| | |
|---|---|
| 当前工作分支 | `feat/mac-m4-media-worker` —— **真正的主干** |
| `main` | 只有一个初始提交（旧 WebView2/React 原型），**是空的，不要往上合** |
| 其他分支 | `codex/project-workbooks`、`codex/software-updater` 等是历史工作线，各有自己的 worktree |
| 客户端 | `desktop/MuYingDrive.NativeWpf/`（.NET 8 / net8.0-windows / 原生 WPF / XAML / MVVM） |
| 后端 | `backend/`（Python + SQLite API；文件内容由独立 Rust NAS 传输服务处理） |

**技术栈是锁定的**，`AGENTS.md` 第 1 节说的那些（禁 WebView2 / React / Electron / Tauri，不许自行升
.NET 10）在这一分支**同样有效**。那部分没作废。

---

## 2. ⚠️ 已经作废的规则（最容易踩的坑）

`AGENTS.md` 和 `README.md` 里还有几行描述的是**部门导航**，而它已经在 2026-10-14 被用户拍板删掉了。
凡是要求「保留 本部门 / 公共素材 / 其他部门 的入口」的话，**都不要再照做**。

- `AGENTS.md`「匿名反馈中心」一节：保留部门位置 —— 已就地标注作废
- `README.md`「导航理解」一段：四个部门空间入口 —— 已就地标注作废

**侧栏现在长这样：**

```
全局主导航:  图片 / 视频 / 个人空间 / 传输 / 反馈
导航分组:    项目  [+]
```

「文件」这个入口没有了；「图片」「视频」「个人空间」是**同一个文件浏览器指向三个不同作用域**
（`SpaceScope`），不是三个页面。「项目」下面每个项目是一棵树：工作簿 / 图片 / 视频。

**当前员工账号不创建 NAS 用户。** 注册只在 SQLite 保存用户名、密码 hash、部门和应用 session；
个人空间由 `personal:{user_id}` 逻辑 scope 定位，文件读写经认证 API 与 Rust 传输服务。仓库已移除
per-user OS/Samba account helper、provisioning token 与对应 systemd unit/socket。部门字段仍是应用账号
和部门逻辑空间的元数据；本轮没有修改生产 Samba 用户、组、配置或 ACL。

---

## 3. 这一轮做了什么（2026-10-14）

按提交倒序：

| 提交 | 做了什么 |
|---|---|
| `6ae841e` | 节拍批注改成**作者签名**：六列导入批注全部去掉，把评论线程搬进面板 |
| `06170a8` | 场景详情从**弹窗改成常驻侧边面板** |
| `157745c` | 走查四条：点 .xlsx 走阅读器 / 侧栏横向滚动 / 滚动条样式 / 进度条闪烁 |
| `91614ec` | `+` 真的建出项目（文件夹 + 两张空表） |
| `0a9b715` | 第一次真实走查的修正 |
| `6e741fe` | 项目组读真实数据 |
| `cc535e0` | 创建后显式设置模式（见下「踩过的坑」） |
| `5dfca3d` | 建项目走**特权辅助进程**，而不是放宽服务单元的写权限 |
| `aa47ee9` | 项目侧栏，部门入口移除 |
| `30cbd57` | 把 `DriveSpaceKind` 换成 `SpaceScope` |
| `0b02b42` | 曾用 inode 作为项目 id；当前改为 SQLite 稳定 id 与目录身份关联 |
| `9dfacf0` | media-worker 凭据改读**环境变量**，不再读文件 |
| `6b5f0f1` | 可选依赖（openpyxl）不再能拖垮整个服务 |

### 几个关键判断，以及为什么

**项目和普通文件统一走逻辑目录。** 项目、工作簿、文件路径、修订、删除标记和变更事件都进入 SQLite；
文件字节由独立 Rust 传输服务处理。仓库已移除 `project_helper.py`、账户 helper 及两个特权 helper 的
unit/socket 模板，也移除了对应注入配置。身份 schema 在遇到旧 helper 字段时会重建员工账号/session 表；
用户已允许丢弃这些应用账号和会话数据。此次源码变更没有运行 schema migration、卸载或停止 NAS 服务、
删除 OS/Samba 用户、改 ACL 或迁移/删除 NAS 文件；旧部署状态需要单独安排。

**不采用交接包里的 `ProjectSpaceShell`。** 有实据，别改回去：它从未编译过（`README.md` 自己写了）；
它会丢四样宿主已有的东西（主题切换、更新红点、账号菜单、侧栏折叠）；它依赖的路由全仓库不存在
（`project-spaces` 零命中）；它是 v3.2，主线 vendor 的是 v3.1，`Contracts.cs` 有实质差异。

**改名的连接键是项目名，不是 id。** 后端给的是 inode，但工作簿/媒体挂靠用的是项目名，
所以**改名会断链**。这是**已知且接受**的限制，写在代码注释里了，不要以为是 bug 去"修"。

**批注没有"我是成员几"这回事。** 用户明确否掉了列映射方案：没有六列、没有声明。
批注由写它的人签名 —— 评论本来就带 `UserName`。原来那六列导入的「成员批注」**完全去掉**，
因为它们指向这个 app 无法识别的人，替登录人选一列就是把猜测当事实显示。

**历史说明：** `SceneRow.LegacyNotes` 和 XLSX 一次性导入曾短暂保留；按后续决定，WPF 与后端现在都已
移除这组兼容字段和导入路径，项目工作簿只保存在 SQLite 中。

**离开守卫在 ViewModel 上，不在按钮上。** `ProjectWorkbooksViewModel.ConfirmLeaving`。
因为宿主的项目树通过 `OpenWorkbookAsync` 换工作簿，**根本不问 View** —— 只守页内按钮的话，
从侧栏点另一个工作簿时草稿会无声消失。

**进度条不能只靠 `DelayedVisibility` 折叠。** 它在值变成 `false` 时折叠，而绑定解析出的初始
`false` 正好**等于属性默认值，不算一次变化**，回调从不执行。所以 XAML 里必须写 `Visibility="Collapsed"`
作为初始值。

---

## 4. ✅ 验过 / ❌ 没验过

**这一节最重要，代码里看不出来。**

### 在真机上验过的

- ✅ **后端项目创建**：在真实 NAS 上建过项目，两张工作簿（节拍表 / 资产表）分类正确，
  身份/权限矩阵正确，探针已清理
- ✅ **更新修复**：2026-10-14 在真实机器上更新到 1.0.17 成功（更新器改名那一步——正是坏掉的那步
  ——成功了）
- ✅ **后端读路径**：`/api/v2/project-workbooks/projects` 在新根下返回试点项目

### 只在测试台验过的（**不是真机**）

- ✅ 编译 0 错 0 警告；渲染测试台两种主题全过、零绑定错误
  （`dotnet run --project desktop/MuYingDrive.NativeWpf.FeedbackTests`）
- ✅ 侧栏形状（`图片/视频/个人空间/传输/反馈`、无部门项）由测试台断言
- ✅ 场景面板渲染（面板是代码构建的，曾经只活在 Window 里 —— 资源查找能不能解析只有渲染才知道）
- ✅ 库采用宿主样式（Button + ScrollBar 两个 key）

### ❌ 没验过的

- ❌ **`+` 新建项目的客户端往返**（后端验过，**客户端路径没在真实会话下走过**）
- ❌ **侧边面板的真机渲染** —— 测试台里面板是单独画的，真机上它在工作簿页里、继承宿主的滚动条样式。
  布局对，滚动条真机上应该更好看
- ❌ 面板里的**保存 / 重新加载 / 409 冲突 / 草稿保留**这一整套状态机在真机上的表现
  （逻辑是从原弹窗原样搬过来的，但搬过来之后没在真机上走过一遍）
- ❌ 批注（评论线程）嵌进面板后的真机表现

### 已知未修的静默失败

- `UpdateCheckState.Failed` **没有界面绑定** —— 检查失败时界面上什么都不会发生
- **更新回滚在视觉上和"什么都没发生"一样**
- 点「孟婆」曾闪退**一次且没有输出**；已加崩溃日志到
  `%LOCALAPPDATA%\MuYingDrive\client-crash.log`，但**没能复现**

---

## 5. NAS 上的现状

下面是 2026-10-14 记录的生产状态快照，不是本轮实时检查结果。**本轮没有操作生产；之后要更新或停止生产服务仍需单独安排。**

| | |
|---|---|
| 后端版本 | `/opt/muying-v2/current` → release `20261014-03`，服务 active |
| 旧 helper 单元 | 当日记录了项目 helper；账户 helper 也曾由旧 API 调用。仓库模板现已移除，生产状态未检查、未改变 |
| 旧 helper socket | 项目 socket 当日记为 `/run/muying-v2/project.sock`（0600）；账户 socket 当日路径未在此记录。均未检查、未改变 |
| 旧环境变量 | 当日记录包含项目 root/socket；旧版本还使用 account provisioner socket。当前仓库不再读取这些配置 |
| 项目根 | 当日记录为 `/volume1/Assets/Project`；当前源码不扫描、不创建、不删除该目录或其中内容 |
| ACL 快照 | `/root/assets-acl-before-project-root-20261014-1333.txt`（改 ACL 之前拍的） |
| ACL 回滚 | `ugacltool del_one /volume1/Assets/Project <索引>`，**从最高索引删起** |

### 🚫 不要动的东西

- **用户 `1004`（账号 `123`）** —— 用户明确要求
- **`/volume1/Assets/Project/孟婆/`** —— 试点项目，用户明确要求不要动
- 生产 `muying-hub.service` / `muying-v2.service` / NAS 目录 / 账号数据库 —— 未经批准不得修改
- Samba VFS 模块、Samba 栈、生产 ACL —— 永远不要为了绕过某个断言去改

### 🔑 凭据：不在仓库里，也永远不要放进来

- NAS 管理员 SSH + 测试账号密码：`Desktop\MuYing Drive\password.md`（**仓库外**）
- 发布签名私钥：`Desktop\MuYing Drive\release-signing\muying-release-signing.key`（**仓库外**）

**这个仓库是公开的。** 推 = 发布。历史上已经有一次明文 NAS 凭据进了公开历史，用户已决定**不改密码**
—— 这件事**不要再提**。相关文件已改成读环境变量。推之前先扫一遍 diff。

### NAS 上两条容易撞的坑

- **本地 `mkdir` 出来的目录没有 UGOS ACE** —— 继承只在 Samba 创建文件时发生。
  `getace` 会报 "It's Linux mode"。
- **这个共享盘上，创建时给的模式不是最终落盘的模式** —— 要 0770 的目录和要 0660 的文件
  都回来 0770（文件带可执行位）。**必须在创建之后显式 `os.chmod`。**
  同一条教训的两种形态：**创建调用是一个请求，不是一个结果。**

---

## 6. 怎么验

```bash
# 编译
dotnet build desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj

# 渲染测试台 —— 两种主题截图 + 零绑定错误断言
dotnet run --project desktop/MuYingDrive.NativeWpf.FeedbackTests -- <输出目录>

# 后端单测 —— 2026-10-14 的基线：116 passed, 3 skipped（跳过的是 Unix-only 的用例）
python -m pytest backend/v2/tests
```

**渲染测试台有个盲区，要知道：** 它**不触发 `Loaded`**（没有窗口，元素树不连 PresentationSource）。
所以任何挂在 `Loaded` 上的东西 —— 资源采用、延迟显示 —— 它都不会自动覆盖。
测试台现在会**手动调一次** `AdoptHostTheme`，这一步就是靠它才发现"进度条静止时一直可见"的。
**往这条路径上加东西时，确认测试台真的覆盖到了，否则截图会骗你。**

NAS 读写验收（**只对隔离测试盘跑，绝不对生产**）：

```bash
dotnet run --project desktop/MuYingDrive.NativeWpf.FeedbackTests -- --nas-acceptance <UNC目录>
```

---

## 7. 当前尚未闭环

- ❗ **等用户**：实机看一眼侧边面板（我验证不了真机渲染）
- ❗ **等用户**：复现「孟婆」闪退，然后读 `%LOCALAPPDATA%\MuYingDrive\client-crash.log`
- ❗ **等用户**：在真实会话里走一遍 `+` 新建项目

---

## 8. 这一轮刻意没做

跨项目媒体聚合（需要 `/api/v2/media/*`，不存在）、资产行 ↔ 图片文件夹原子创建、回收站、软删除、
**真正的部门移除**（数据库 + ACL）、`ProjectSpaceShell` 的整体接入、v3.1 → v3.2 库升级。

全局「图片/视频」这一轮**只读且诚实**：枚举试点项目目录、按类型过滤、明确标出作用范围，
**不伪造跨项目分组**。任何一个页面上「看起来像聚合但其实不是」的东西都是 bug。
