# UGOS Samba `vfs_ugacl.c:875` 断言崩溃 — 面向厂商的定位报告

日期：2026-10-13。环境：UGOS 1.17.0.0095 / Debian 12。
状态：**根因未最终确认，但已缩小到 UGOS 自研 VFS 模块**；本报告仅供提交厂商与内部留档，**不声称已修复**，也**未**修改厂商模块、Samba 栈或生产 ACL 来绕过断言。

测试全程只在隔离共享 `/volume1/MuYing_ACL_Test` 上进行，未触碰生产 Assets。

---

## 1. 摘要

在隔离共享上，用 SMB 客户端以**普通域内身份**访问受 UGOS 原生 ACL 约束的目录时，**smbd 子进程发生断言崩溃**：

```
PANIC: assert failed at ../../source3/modules/vfs_ugacl.c(875): real_smb_file_name
```

断言位于 UGOS 自研的 Samba VFS 模块 `vfs_ugacl.c` 的 `ugvfs_acl_fget_nt_acl()` 内（同函数在 889 行还有一条 `fget_nt_acl ... fail` 日志，但崩溃路径在抵达该日志前就已断言）。

崩溃**只在特定时序下出现、非确定性复现**：同一批操作在单步/隔离复测中未再触发。崩溃只终止**单个连接的 smbd 子进程**，主机 `smbd` 主服务全程未重启。

## 2. 环境

| 项 | 值 |
|---|---|
| NAS 系统 | UGOS 1.17.0.0095，Debian 12 |
| Samba | `4.17.12-Debian`（断言自报版本） |
| 共享 VFS 栈 | `catia fruit full_audit recycle streams_xattr ug_xattr_filter` + UGOS `ugacl` |
| 共享 | `/volume1/MuYing_ACL_Test`（隔离测试共享） |
| 客户端 | Windows 11，.NET 8 WPF，`WNetAddConnection2W` |
| 崩溃时 `smbd` 主服务 | `MainPID=10118`、`NRestarts=0`、`ActiveState=active`（全程） |

## 3. 崩溃记录

三次崩溃发生在同一轮多身份 Windows 矩阵测试期间，**调用栈完全一致**：

| # | smbd 子进程 PID | 时间（UTC+8） |
|---|---:|---|
| 1 | 342584 | 2026-10-13 10:24:43.432 |
| 2 | 342779 | 2026-10-13 10:26:38.437 |
| 3 | 342787 | 2026-10-13 10:26:40.991 |

## 4. 断言与调用栈

```
PANIC (pid 342584): assert failed: real_smb_file_name in 4.17.12-Debian
BACKTRACE: 30 stack frames:
 #0  libgenrand-samba4.so.0(log_stack_trace+0x2e)
 #1  libgenrand-samba4.so.0(smb_panic+0x9)
 #2  libsmbd-base-samba4.so.0(+0x4f42e)
 #3  vfs/full_audit.so(+0x7d52)
 #4  vfs/fruit.so(+0xd850)
 #5  vfs/catia.so(+0x5a35)
 #6  libsmbd-base-samba4.so.0(smbd_check_access_rights_fsp+0x1a6)
 #7  libsmbd-base-samba4.so.0(can_write_to_fsp+0x1b)
 #8  libsmbd-base-samba4.so.0(file_ntimes+0xc4)
 #9  libsmbd-base-samba4.so.0(+0x68a72)
 #10 libsmbd-base-samba4.so.0(trigger_write_time_update_immediate+0xd3)
 #11 libtevent.so.0(tevent_common_invoke_timer_handler+0xfe)
 ...
 #18 libsmbd-base-samba4.so.0(smbd_process+0xaff)
 #19 /usr/sbin/smbd(+0x9e00)
 ...
```

关键帧（自下而上）：

`tevent 定时器` → `trigger_write_time_update_immediate` → `file_ntimes` → `can_write_to_fsp` → `smbd_check_access_rights_fsp` → VFS 链 `catia → fruit → full_audit → ugacl` → `ugvfs_acl_fget_nt_acl()` → 断言。

## 5. 断言前日志序列（三次一致）

每次崩溃前，日志都出现同一模式：对**被 ACL 拒绝的个人空间目录**的 ACL 查询失败，随后延迟定时器触发并断言。

```
vfs_ugacl.c:889(ugvfs_acl_fget_nt_acl)
  ugvfs_acl_fget_nt_acl: fget_nt_acl(real_path:/volume1/MuYing_ACL_Test/personal_writing, stream:(null))
  ugacl_agent_acl fail, ret:-1073741790
smbd/open.c:3403(smbd_calculate_maximum_allowed_access_fsp)
  smbd_calculate_maximum_allowed_access_fsp: Could not get acl on file personal_writing: NT_STATUS_ACCESS_DENIED
...
vfs_ugacl.c:494(ugacl_aget_acl)
  ugacl_aget_acl: ERR_ACCESS_DENIE
vfs_ugacl.c:889(ugvfs_acl_fget_nt_acl)
  ugvfs_acl_fget_nt_acl: fget_nt_acl(real_path:/volume1/MuYing_ACL_Test/personal_writing, stream:(null))
  ugacl_agent_acl fail, ret:-1073741790
smbd/open.c:3403(smbd_calculate_maximum_allowed_access_fsp)
  smbd_calculate_maximum_allowed_access_fsp: Could not get acl on file personal_writing: NT_STATUS_ACCESS_DENIED
vfs_ugacl.c:875(ugvfs_acl_fget_nt_acl)
  PANIC: assert failed at ../../source3/modules/vfs_ugacl.c(875): real_smb_file_name
```

`ret:-1073741790` = `0xC0000022` = `STATUS_ACCESS_DENIED`。

## 6. 触发条件分析

**观察到的相关性（已证实）**

- 三次崩溃前，客户端都对**当前身份被拒绝的个人空间目录**（`personal_film` / `personal_writing`）发起了元数据/列举访问，UGOS ACL agent 返回 `ACCESS_DENIED`。
- 崩溃点位于 **tevent 延迟定时器回调**，而非某个直接 SMB 请求的同步路径。
- 与 108 步多身份矩阵逐条比对，三次崩溃分别落在：

  | 崩溃 | 对应身份 → 被拒目录 |
  |---|---|
  | pid 342584 | `film` → `personal_writing` |
  | pid 342779 | `writing` → `personal_film` |
  | pid 342787 | `visitor` → `personal_film`、`personal_writing` |

**假设（尚未证实，供厂商参考）**

被拒绝目录对应的 `fsp` 在 `real_smb_file_name` 为 **NULL** 的情况下，被延迟的**写时间更新**回调（`trigger_write_time_update_immediate` → `file_ntimes` → `can_write_to_fsp`）再次访问；`ugvfs_acl_fget_nt_acl()` 在该处直接对 `real_smb_file_name` 断言，缺少 NULL 防御，于是 panic。

- 该假设能解释"为什么单步复测不重现"：它依赖**延迟定时器**与**被拒 fsp** 的组合时序，而非任何单次文件操作。

**未能证实的部分**

- 断言对应的确切文件、SMB 请求与原语（`GET_ATTRIBUTES` / 目录 open / 写时间更新）无法从现有日志还原：UGOS/Samba 日志未暴露与断言 PID 关联的路径和 file id，core 未保留。
- `ACCESS_DENIED` 与断言**是否因果关系**（而非巧合相关）尚不能确证。

## 7. 最小复现步骤

需要在隔离共享上、以**非属主身份**执行：

1. 在共享上按 UGOS 原生 ACL 建立"个人目录仅属主可访问"的结构（本测试为 `personal_film` / `personal_writing`）。
2. 客户端以**非属主**域内身份通过 WNet（或等价 SMB 会话）连接该共享。
3. 对被拒绝的个人目录执行**元数据查询 / 目录列举**（本探针为逐段 `File.GetAttributes(dir)` 与 `Directory.EnumerateFileSystemEntries`）。
4. **保持连接**，等待若干秒，让延迟的写时间更新定时器触发。
5. 观察 `smbd` 子进程出现 `vfs_ugacl.c:875` 断言。

> 说明：第 3 步的目录属性查询本身是"被拒绝即返回 ACCESS_DENIED"的正常结果；崩溃发生在**其后**的延迟回调，因此不能把它当成一次请求的同步失败来处理。

## 8. 已验证**未能**触发的场景（用于排除）

以下隔离复测均**未**产生新的 PANIC（`panic_count` 恒为 3）：

| 场景 | 结果 |
|---|---|
| 单身份遍历/读取允许目录，读取被拒目录，等 12s 及连接存活读 | 无 PANIC |
| 单次 `CreateNew` 写 1 字节，等 12s 及连接存活读 | 无 PANIC |
| 单次覆盖写 1 字节，等 12s 及连接存活读 | 无 PANIC |
| 新建→写→关闭→立即重命名，等 12s | 无 PANIC |
| 新建→写→关闭→立即删除，等 12s | 无 PANIC |
| 三身份在同一 Windows 进程内依次连接并各写一个小文件 | 无 PANIC |
| 同一身份短时间连续写 12 个文件 | 无 PANIC |
| 写后直接尝试对同一文件无权限读取 | 无 PANIC |

即：身份切换、拒绝访问、单次写入、短时连续写入**单独都不足以**触发。触发需要原始综合矩阵中的组合时序。

## 8.5 应用真实访问模式不会触发（2026-10-13 复核）

在真实 NAS 上完成客户端验收期间（注册、登录、反复浏览生产目录、参观通道多次列举三个库、
ACL 变更、多次应用内更新），**Samba 日志没有产生任何新的 PANIC**：

```
PANIC: assert 事件数 = 3
时间戳         = 10:24:43.432198 / 10:26:38.437477 / 10:26:40.991076
```

三次全部集中在最初那次**多身份矩阵探针**运行的 40 秒窗口内；其后数小时的真实使用为零。
（`grep -c PANIC` 会得到 6，因为每次崩溃在日志中占两行；按 `PANIC: assert` 计为 3 次。）

原因：触发需要**对"当前身份被拒绝的目录"发起元数据/列举访问**。而本客户端只会读取
**用户当前选中的那一个空间**，且按设计四个空间（本部门、公共、个人、另一部门）对员工**均为可读**
——应用不存在该访问模式。产生崩溃的矩阵探针是**刻意**把每个身份对每个空间（含被拒的个人目录）
逐一戳一遍，属于人为构造，不代表应用行为。

**因此：本应用侧没有可修复点。** 剩余工作是把本报告提交 UGOS 厂商。

## 9. 影响

- 崩溃终结**单个连接的 smbd 子进程**；主机 `smbd` 主服务未重启（`MainPID=10118`、`NRestarts=0`）。
- 对客户端表现为**连接被切断**。
- 未观察到数据损坏或生产目录被修改；生产 root / 影视 / 文学目录 ACE 与 mode 同基线。

## 9.5 厂商侧无可用更新（2026-10-13 核查）

- Samba 为 Debian 原版 `2:4.17.12+dfsg-0+deb12u3`，`apt` **没有可升级版本**。
- `ugacl` **不是可替换的独立模块**：它是 UGOS 补丁并编译进 `smbd` 二进制的
  （崩溃栈中的 `source3/modules/vfs_ugacl.c`），`/usr/lib/.../samba/vfs/` 下没有对应 `.so`。
- `@appstore` 中未见系统/组件更新通道。

即：**无法通过升级或替换模块规避，只能由厂商修复。**

## 10. 请求厂商

1. 请确认 `vfs_ugacl.c:875` 处 `real_smb_file_name` 断言**预期的前置条件**，以及哪些**合法调用路径**会给出 NULL。
2. 请评估在 `ugvfs_acl_fget_nt_acl()` 及延迟写时间更新路径上，对 `real_smb_file_name` 为 NULL 的 fsp 做**防御/跳过**（而非 `assert`），是否有副作用。
3. 如需要，我方可提供：精确版本、完整脱敏调用栈、隔离复现脚本、崩溃前后日志片段与时间戳。

## 11. 边界声明

- 未修改 UGOS 厂商模块、Samba VFS 栈、生产 ACL，也未通过扩大权限来规避断言。
- 本文不声称根因已修复，也不把崩溃归因于 ACL 拒绝本身——只能说 AGENT 返回 `ACCESS_DENIED` 与断言**相关**。
- 生产共享的持久化方案与回退仍待单独批准，与本文无关。

## 12. 证据文件

| 文件 | 内容 |
|---|---|
| `docs/validation/ugos-acl-2026-10-13/windows-ugos-panic-evidence.json` | 三次崩溃的完整脱敏日志、调用栈、服务状态、生产基线对照 |
| `docs/validation/ugos-acl-2026-10-13/windows-wpf-services-4b8defb9237b41d3ac89e2fa9f02337c.json` | 触发崩溃的 108 步多身份矩阵结果 |
| `docs/validation/ugos-acl-2026-10-13/windows-single-*.json` | 单身份隔离复测（均未复现） |
| `docs/validation/ugos-acl-2026-10-13/minimal-*.json`、`switch-*.json`、`burst-write-*.json`、`write-*.json` | 分步诊断与排除证据 |
| `docs/ugos-acl-validation.md` | 总体验证进度与结论边界 |
