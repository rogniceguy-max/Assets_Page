# 文件操作：上传（多文件 / 文件夹）、重命名、复制、删除

日期：2026-10-14。分支：`codex/ugos-acl-visitor`。

## Context

右键菜单里一直有「重命名 / 移动到 / 复制 / 删除」四项，但四项都绑在同一个占位命令上，点了只弹一句
"尚未接入 NAS"。同时上传只能一次一个文件，`SmbFileCatalog` 是纯只读的 —— 所以这不是"解锁"，
是从零实现。本次实现其中三项，`移动到` 保持占位（它需要一个能在 NAS 上浏览选目标文件夹的对话框，
是另一件工作）。

## 新增了什么

| 能力 | 入口 |
|---|---|
| 一次选多个文件上传 | 「上传」按钮 |
| 上传整个文件夹（保留目录结构） | 「上传文件夹」按钮（新增） |
| 重命名（文件与文件夹） | 右键 → 重命名 |
| 复制（原地生成副本） | 右键 → 复制 |
| 删除（含确认框） | 右键 → 删除，或 Delete 键 |

**同名一律自动加序号，绝不覆盖**（`照片.jpg` → `照片 (2).jpg`）。这是刻意的：一次拖 200 个文件时，
一个停下来问的冲突要问 200 次，而答案每次都一样。

## 安全规则（本次的重点）

列表页的路径检查一直是**字符串前缀比较**。对读够用，对写不够 —— 共享里一个 junction
（`...\film\link` 指向 `C:\Windows`）会被枚举过滤掉所以看不见，但前缀比较对它照样通过。

所以新增了 `SmbSpaceResolver`，把"路径必须在这个空间里"的判断收成**一处**（此前散在三处，加写操作
会变成第四处），并分成两个明确不同的检查：

- `RequireReadablePath` —— 只做前缀比较。读用它，因为链接本来就被枚举过滤掉，且 NAS 怎么暴露内容是它的事。
- `RequireWritablePath` —— 前缀 **加上** 逐段拒绝重解析点。**每一次写操作，源和目标都要过这一关。**

这个不对称是刻意的：读是"服务器愿意给什么"，写是"这个客户端愿意改什么"。

其余：名字段校验（拒绝 `\ / : * ? " < > |`、控制字符、`.`/`..`、结尾点或空格、`CON`/`NUL` 等保留名）；
递归删除**自己显式走**而不依赖 `Directory.Delete(recursive: true)`（它的重解析点语义随版本变化，
而这是对生产素材的删除）；目录复制跳过链接而不是复制它指向的东西。

**已知残留窗口**：UNC 上没有"按句柄打开且不跟随"的 API，检查与落盘之间组件理论上可被换成链接。
检查因此紧贴落盘处执行，并如实记在这里，不声称已消除。

## 顺带修的既有缺陷

- **`CanModify` 是常量 `true`**，不看连接状态、也不看「其他部门」这个只读空间。改成与 `CanUpload`
  同源，并在切换空间、加载状态变化等**每一处**同步补上变更通知（漏一处就会出现"切到只读空间后
  编辑菜单还在"这个正是要修的 bug）。
- **上传完成后列表不刷新** —— 新文件要离开再回来才看得见。文件夹上传会让这个缺陷非常刺眼。
  现在由 `TransferViewModel.UploadCompleted` 通知，**带 400ms 去抖**（200 个文件否则会触发 200 次重载），
  并且只在传进当前显示的目录时才刷新。
- **传输引擎没有任何并发上限** —— 一个文件夹 500 个文件就是 500 个并发 SMB 流。加了**可选**上限
  （`maxConcurrentWorkers`，默认仍为无限），`TransferViewModel` 传 4。这只限制 worker 启动数，
  **不改动传输协议**（断点、校验、发布语义都没动）。
  **注意**：AGENTS.md 把"传输引擎协议"列为敏感项，这一点需要单独知情。
- 更新检查间隔 15 → 5 分钟，并加 **±20% 抖动**；manifest 轮询发 `If-None-Match`（服务端一直支持 304，
  客户端从未带过这个头）。

## 验证状态

**已验证（本机）**：

- Release 构建 0 警告 0 错误；四个项目全部构建通过。
- `MuYingDrive.UpdateCore.Tests`、`MuYingDrive.Bootstrap.Tests`、`MuYingDrive.NativeWpf.FeedbackTests`
  全部通过；后端 68 项通过。
- **渲染测试新增参观端渲染** —— WPF 按运行时类型绑定，这是唯一能抓到"员工端有、参观端没有"的机制。
  已确认参观端渲染出「只读」徽标且**没有上传按钮**。
- 两个新对话框（确认框、重命名框）已在浅色/深色下渲染通过，无绑定错误；
  确认框的默认按钮已断言为「取消」（回车不能删东西）。
- 纯逻辑断言：副本命名（含 `.gitignore`、`archive.tar.gz` 等边界）、名字段拒绝清单、空间范围拒绝越界路径、
  重解析点检查不吃掉正常路径、文件夹遍历保留空目录。
- 用一个故意的错误断言确认了测试确实会失败，而不是恒过。

**已在隔离共享上实测（2026-10-14）**：

`dotnet run --project desktop/MuYingDrive.NativeWpf.FeedbackTests -- --nas-acceptance '\\<NAS>\MuYing_ACL_Test\film'`
以 `沐影影视测试` 连上隔离共享，真实执行 12 项，**全部通过**：

```
creates nested and empty directories                          PASS
duplicates beside the original  (照片 (2).txt)                 PASS
the copy is numbered, not named the same                      PASS
the original is untouched                                     PASS
a second duplicate takes the next number                      PASS
renames to a free name exactly                                PASS
a rename onto an existing name is numbered rather than
  overwriting  (照片 (4).txt)                                  PASS
the file it would have overwritten still has its own contents PASS
counts everything under a folder for the confirmation  (4)    PASS
refuses a path that walks out of the space                    PASS
refuses a path belonging to another space                     PASS
deletes a folder and everything under it                      PASS
```

探针结束后共享上**无残留**（刻意用带 GUID 的独立目录，并在 finally 里兜底清理）。
**生产素材库上一个写操作都没有做过。**

这轮同时也说明了一件好事：头两次跑测试时挂掉的两项，查下来都是**测试自己的期望算错了**
（把 `照片 (4).txt` 写成了 `照片 (2) (2).txt`；数目录时漏了 `空目录` 本身），代码行为是对的。

**仍未验证**：

- **重解析点拒绝本身没被实测** —— Windows 上建 junction/symlink 需要权限。这条规则目前的可信度
  来自代码与 Linux 侧验收，不来自测试。
- **应用内的完整链路没跑过** —— 探针直接调服务层，**未经**按钮、右键菜单、两个对话框和上传刷新。
  那部分靠渲染测试（无绑定错误）和代码审查覆盖，**不等于实机点过**。
- 多文件与文件夹上传没有在真机上跑过（探针只覆盖了写操作，没有走传输队列）。
- 传输引擎并发上限的实际行为（排队显示、暂停、销毁不挂起）。
- 大目录复制的表现（无进度、不可续传，已知限制）。

## 隔离共享的现状（不是本次造成）

`MuYing_ACL_Test` 里仍有 **2026-10-13 那轮 ACL 验证留下的约 60 个探针产物**
（`muying-acl-probe-*`、`probe-*`、`matrix-*` 等）。`docs/ugos-acl-test-teardown-plan.md`
的清理方案**至今未执行**。本次的产物已全部清除。

## 明确不做

移动到（保持占位）、跨空间操作、目录复制的进度与断点续传。
