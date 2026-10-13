# UGOS 隔离 ACL 验证进度（2026-10-13）

## 范围和结论

测试仅操作 `/volume1/MuYing_ACL_Test`，未对生产 Assets 文件执行递归权限操作。NAS 为 Debian 12 / UGOS 1.17.0.0095。

当前**不是最终验收通过**：低权限 Unix 与 NAS smbclient 的 210 个操作判定通过；Windows 三身份 WNet 均连通。原多操作 Windows probe 的 5 项访问个人空间结果是 Win32 59，不能计为权限拒绝通过，且同一时间 Samba 子进程发生 UGOS ACL 模块断言崩溃。单身份隔离复测继续中。Windows/WPF 视觉、重启后权限持久性及生产方案仍待完成。

原生用户服务创建了以下普通用户；系统个人目录关闭，密码不入仓库：

|身份|UID|部门补充组|
|---|---:|---|
|[TEST-FILM]|1005|my_acl_film / GID 1006|
|[TEST-WRITING]|1006|my_acl_writing / GID 1007|
|[TEST-VISITOR]|1007|无部门组|

用户及组均可被 UGOS 原生 `pathAcl` API 识别。原生账号创建通过管理员正常认证后的 `v1/user/create`，没有直接写厂商数据库。

> **脱敏说明：** 本文及 `docs/validation/` 证据文件中，账号显示名与手机号样账号已替换为占位符（`[TEST-FILM]`、`[TEST-WRITING]`、`[TEST-VISITOR]`、`[REDACTED-ACCOUNT-ID]`）。占位符仅为对外发布替换，不改变任何操作结果、判定或结论。

## 权限矩阵

|身份|film|writing|public|personal_film|personal_writing|
|---|---|---|---|---|---|
|影视|读写|只读|读写|读写|禁止|
|文学|只读|读写|读写|禁止|读写|
|参观|只读|只读|只读|禁止|禁止|

每种身份、每个目录分别检查列举、读取、覆盖、创建文件、重命名、删除和创建目录。两层验证为 `runuser` 的真实低权限 Unix 身份与携带真实密码的 `smbclient -A`；没有使用 `-N`。临时认证文件位于 `/run`，0600 权限，测试结束删除。

原始脱敏结果：`validation/ugos-acl-2026-10-13/isolated-full-matrix.json`。失败数组为空。拒绝项为 Unix EACCES / SMB NT_STATUS_ACCESS_DENIED，不是密码认证失败。

## ACE 与继承

使用系统原生 `ugacltool addace`，不是 POSIX setfacl。全部实际 ACE（含新文件和目录）保存在结果的 `aces` 字段。

- 管理员组 GID 10 保留原有完整权限及文件/目录继承。
- 根目录给两个部门组和参观 UID 1007 `r-x---a-R-c--`，不继承，避免进入个人空间。
- 部门目录给本部门组 `rwxpdDaARWc--`，其他部门组和参观身份 `r-x---a-R-c--`，文件和目录继承 `fd`。
- 公共目录给两部门组读写，参观身份只读，`fd` 继承。
- 个人目录只给对应 UID 读写，`fd` 继承；无其他员工或访客授权。
- 在授予目录 ACE 前建立的测试文件，逐个执行原生 `enforce_inherit` 后验证。**这不证明旧文件会自动追溯继承**，更不授权对生产文件递归处理。
- 新文件及嵌套测试文件已采集继承 ACE；跨身份对新增文件的完整操作矩阵仍须另验。

Windows 权限探针运行时，所有测试连接均使用独立 `LogonUser` 网络身份及 WPF `WindowsSmbConnectionService.ConnectWithCredentials` 的真实 WNet 路径。隔离身份下 Credential Manager 写入返回 1312，故测试使用内存凭据；没有验证 Windows Vault 持久化。第一轮多身份矩阵有 5 项 Win32 59，因此不能作为 ACL 拒绝证据。

## UGOS ACL Samba 崩溃诊断（未修复）

脱敏日志 `validation/ugos-acl-2026-10-13/windows-ugos-panic-evidence.json` 保留实际调用栈。三次退出在不同 PID：342584、342779、342787；堆栈均为 `tevent timer → trigger_write_time_update_immediate → file_ntimes → can_write_to_fsp → vfs_ugacl.c:875`，断言 `real_smb_file_name`。这说明失败层在 Samba 的 UGOS 原生 ACL VFS 模块。生产 smbd 主服务 `10118` 持续 active，NRestarts=0。

时间相近的 `ugvfs_acl_fget_nt_acl`/`NT_STATUS_ACCESS_DENIED` 与断言存在相关性，但尚不能证明读取被拒个人目录就是崩溃原因。原综合 Windows probe 每步前有属性预检，不能只根据步骤标签推定文件操作实际执行。未找到证据支持扩大授权、关闭 ACL 或修改厂商 VFS 组件。

单身份最小复测结果：

|场景|结果|Samba 新 PANIC|
|---|---|---|
|WNet 连接、列举/读取 film、读取 personal_writing（预期拒绝）、等待12秒及连接存活读|允许目录读成功；拒绝目录 Win32 5；12秒后连接仍存活|否|
|WNet 连接、单次 CreateNew 写 1 字节、等待12秒及连接存活读|通过|否|
|WNet 连接、单次覆盖写1字节、等待12秒及连接存活读|通过|否|
|同一新文件 CreateNew→写1字节→关闭→立即重命名→等待12秒及连接存活读|通过|否|
|同一新文件 CreateNew→写1字节→关闭→立即删除→等待12秒及连接存活读|通过|否|

拒绝路径单步诊断进一步定位了 Win32 59：`File.GetAttributes(personal_writing)` 目录成功，`File.GetAttributes(personal_writing/existing.txt)` 报 `IOException` / HRESULT `0x8007003B` / Win32 59；直接 `File.OpenRead` 同一文件则报 `UnauthorizedAccessException` / `0x80070005` / Win32 5。故 59 是 UGOS ACL 不允许的文件属性查询在 Windows 上的错误映射，实际打开被拒绝文件明确返回 Access Denied。不能把所有 59 通用认作拒绝；应用需要直接报告属性查询网络错误，不把它等同登录失败。

这些结果降低了“单次拒绝读取”、“单次写入”、“写后重命名”或“写后删除”即触发的可能性。三个身份在同一 Windows 进程内依次连接并各自写一个小文件、同一身份短时间内连续写12个文件、三个身份依次尝试直接读取禁止的个人文件，以及“写后直接尝试无权限读取”均未产生新 PANIC。完整单身份五空间 probe 的唯一异常是个人文件属性查询 Win32 59；分步骤探针确认直接打开同一文件为 Win32 5。因此，当前仍未复现多身份综合矩阵中的 VFS 断言，不能归因于身份切换、拒绝访问或单次/短时连续写入。详细 UTC、单调时钟、HRESULT、SMB主服务状态及日志增量见各自 `minimal-*.json`、`switch-*.json`、`burst-write-*.json` 和 `write-own-denied-*.json`。最初综合测试里同时出现的写入时序、UGOS ACL 调用栈，以及后续隔离复测未复现，是目前能确认的边界；不重复已崩溃的多身份综合矩阵。

### 触发条件定位（2026-10-13 复核）

在原有结论基础上，把三次崩溃与 108 步多身份矩阵逐条比对，进一步收窄触发条件：

- 三次调用栈完全一致，且都发生在 **tevent 延迟写时间更新定时器**回调中（`trigger_write_time_update_immediate` → `file_ntimes` → `can_write_to_fsp` → `smbd_check_access_rights_fsp` → VFS 链 `catia→fruit→full_audit→ugacl` → `vfs_ugacl.c:875`），**不是**某个直接 SMB 请求的同步路径。
- 崩溃前紧邻日志必然是：对**被拒个人空间目录**（`personal_film` / `personal_writing`）的 ACL 查询返回拒绝（`ugacl_agent_acl fail, ret:-1073741790` = `0xC0000022`；`smbd_calculate_maximum_allowed_access_fsp: Could not get acl ... NT_STATUS_ACCESS_DENIED`）。
- 三次分别落在 `film → personal_writing`、`writing → personal_film`、`visitor → personal_film + personal_writing`，与矩阵中被拒访问逐条吻合。
- **假设（未证实）**：被拒目录的 fsp 在 `real_smb_file_name` 为 NULL 时被延迟写时间更新回调再次访问，`ugvfs_acl_fget_nt_acl()` 缺少 NULL 防御而断言。该假设可解释“单步复测不重现”——它依赖延迟定时器与被拒 fsp 的**组合时序**，而非任何单次操作。
- 断言的具体文件、SMB 请求与 file id 仍无法从日志还原（core 未保留），故 `ACCESS_DENIED` 与断言的因果性仍不能确证。
- 面向厂商的完整定位报告与最小复现步骤见 [`ugos-samba-vfs-ugacl-assert-report.md`](ugos-samba-vfs-ugacl-assert-report.md)。

### 根因结论边界

- **已证实：** 最初完整 Windows 多身份矩阵期间三个独立 smbd 子进程发生 `vfs_ugacl.c:875` `real_smb_file_name` 断言；Samba 是 Debian 4.17.12；日志调用栈由延迟更新时间进入 UGOS ACL VFS。三个子进程退出，但 `smbd` 主服务未重启，仍 active / `NRestarts=0`。
- **已证实：** Linux/SMB/WPF 连接认证均成功；Windows 直接打开员工不应访问的个人文件返回 Access Denied (Win32 5)。Windows 文件属性查询返回的 Win32 59 是另一项元数据查询结果，不能混同密码认证、连接失败或权限打开结果。
- **未证实：** 断言的确切文件、SMB 请求和触发时序。UGOS/Samba 日志没有暴露与断言 PID 对应的路径和 file ID，core 未保留；不能声称根因已修复或把崩溃归咎于 ACL 拒绝。
- **处理边界：** 没有改 UGOS 厂商模块、Samba VFS 堆栈、生产 ACL 或权限绕行。若要继续追出 VFS 缺陷，应向 UGOS 厂商提交精确版本、已脱敏调用栈和复现记录；不扩大权限来规避断言。

## SMB 生成配置的已知限制

原生创建共享 API 会重生成全局 `smbshare.conf`，曾从生产 Assets 的 valid users / write list 移除手工加入的 `@group_assets`。用户批准后已只补回这两个条目。

为了继续隔离验证，后续没有调用全局共享生成接口；仅修改测试共享自己的 valid users / write list，保留管理员，加入两部门组及参观用户。参观用户不在 write list 中，目录 UGOS ACL 同时只读。

**这只是隔离验证配置，并非解决 UGOS 重生成覆盖问题的生产持久化方案。** 禁止定时覆写生产配置或修改厂商数据库。生产方案需采用原生管理的授权关系并保留必要旧身份兼容，经单独批准后实施。

操作后生产根目录、影视和文学目录的 ACE 与 mode 均同基线；Assets SMB 条目语义同基线（账户顺序忽略）。证据：`post-matrix-production-check.json`。未逐个遍历/校验 5,085 个文件，不声称完成全量文件校验。

## 证据一致性待核（未决）

`docs/validation/ugos-acl-2026-10-13/final-production-check.json` 由其 `check.production_directory_ACLs_unchanged` 报告 **`false`**，但同一文件 `observed` 中三个生产目录的 ACE 与 mode 与基线**逐字节一致**；而 `ugos-account-investigation/final-production-check.json`（另一套 schema）三项检查均为 `true`。

两者结论相反，生成脚本不在仓库内，**无法从现有文件判断生产目录 ACL 是否真的发生漂移**。在澄清前：

- 该文件的 `false` **不得**被当作“生产已变更”的证据，也**不得**被当作“生产未变更”的证据。
- 不据此提交生产变更或回退方案；如要继续，需重跑校验并保留比较基线。

## 清理与回退（尚未执行）

1. 停止仅属于测试环境的应用/服务，确认无测试传输。
2. 保存测试共享信息、完整 ACE、配置与测试记录。
3. 用户指示后仅删除测试工具产生的文件、测试共享和三名测试账号及专用组；不得清理生产或旧测试账号。
4. 通过 UGOS 删除共享前，必须考虑其再次重生成生产 SMB 配置的副作用，比较完整配置并单独确定恢复方案。
5. 不用整个旧 `smbshare.conf` 直接覆盖后续配置；只按经审查的差异回退对应共享条目。

## 客户端（WPF）隔离修复：换身份不再“进入不了”

Windows 在**同一登录会话内不允许对同一服务器用不同用户名建立第二条连接**（WNet 错误 **1219**）。此前客户端在退出登录时刻意不断开该连接（为保留 commit `02cb888` 的共享会话安全修复），于是切换测试身份后残留的旧身份连接会让新身份连接直接失败，只弹出“请到资源管理器断开该连接”而无法进入。

现改为在**新账号登录成功后、写入新凭据之前**自动清理：

- 清空本机保存的会话令牌与 SMB 凭据；
- 断开本应用对该共享的 WNet 连接（`WNetCancelConnection2W`，force）；
- 再以新身份重新连接。

边界：

- 清理只在**服务器已接受新登录之后**执行；登录失败不会清除当前账号的凭据，避免打错密码反而被登出。
- 退出登录仍**不**断开 WNet 连接（`02cb888` 修复保留）。
- 若资源管理器等其它进程仍占用该共享，强制断开可能失败；此时退回到原有提示，需用户手动关闭占用后重新登录。
- 该修复为客户端行为，**尚未在真机 WPF 交互验收中确认**，列为待验收项。

## 后续门槛

Windows 原生连接/权限、WPF 实际界面、新增文件跨身份继承、免登录参观只读通道均未最终验收。全部通过后才提交生产精确变更与备份/回退方案，等待第二次批准。
