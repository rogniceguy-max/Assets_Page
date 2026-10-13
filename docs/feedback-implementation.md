# 匿名反馈中心｜实现契约与验收记录

日期：2026-10-13。需求来源：Notion《沐影盘 MuYing Drive｜产品需求与设计规范》《Windows UI 界面结构与交互规范》第 10 节，以及本分支 [feedback-center.md](feedback-center.md)。

本分支从 `codex/nas-smb-v2` 派生；GitHub 比较确认原有分支仅新增反馈专项设计与约束。本次实现仍为 `.NET 8 / net8.0-windows / C# / 原生 WPF / XAML / MVVM`，未调整既有账户、SMB 权限、传输引擎或更新流程。

## 功能与访问权限

- 左侧第三项「反馈」与文件、传输同级；展开和折叠时均可聚焦，有 Tooltip 和可访问名称。文件浏览 ViewModel、当前空间及传输实例保持不变。
- 公共列表支持全文搜索、全部/问题反馈/功能建议筛选、按更新时间倒序、每页 20 条；详情返回保留列表控件及当前筛选/页码。
- 同一原生 WPF 表单支持两类反馈、标题、描述、问题类型/相关场景和可选截图；原生文件选择、拖入、剪贴板截图以及提交前预览。Ctrl+Enter 提交、Esc 关闭；提交或读图期间防止重复提交。失败保留草稿，关闭表单后同一登录会话内可再次打开。
- 2026-10-13 用户确认取消传输任务的反馈快捷入口。反馈仅从侧栏「反馈」工作区提交；传输页不显示「反馈此问题」，不预填传输诊断。已删除专属点击处理与脱敏预填服务。
- 纯文字反馈通过字段、长度、控制字符及明显凭据/路径/邮箱/地址检查后公开。该检查无法识别任意姓名或自然语言中的敏感内容；员工仍须检查文字。
- **带截图的整条反馈先进入待审核区**。普通员工只能收到随机编号和已提交确认；截图及反馈正文在审核公开前均不能被普通员工查询或下载。维护人员检查截图像素及文字后可公开、隐藏或调整处理状态。提交提示明确告知审核过程。
- 默认无人拥有维护权限。`MUYING_V2_FEEDBACK_MAINTAINERS` 以明确的现有 V2 账号数字 ID 列表配置反馈维护角色；这只影响反馈接口，不修改账户表、员工部门或 Samba 权限。列表/详情/附件/变更每次都重新校验现有会话。普通员工不能改状态或查看待审核/隐藏内容。
- 无「我的反馈」、作者信息、固定匿名代号、投票、评论或通知系统。

## HTTP 契约

所有接口需要现有 `Authorization: Bearer …`；未登录或失效会话返回 401。反馈 JSON 不包含用户、部门、会话、设备或地址字段。

| 接口 | 作用 | 约束 |
| --- | --- | --- |
| `GET /api/v2/feedback?kind=all&q=&page=1&scope=public` | 搜索/列表 | kind: all/problem/suggestion；q 最多 100 字符；每页 20 条；scope: public/pending/hidden，后两者仅维护角色 |
| `GET /api/v2/feedback/capabilities` | 反馈权限与审核策略 | 只返回 can_manage / images_require_review |
| `POST /api/v2/feedback` | 提交 | 只接受 kind/category/title/description/images/privacy_confirmed；其他字段拒绝 |
| `GET /api/v2/feedback/{feedback_id}` | 详情 | 不公开的记录仅维护角色可读 |
| `GET /api/v2/feedback/attachments/{attachment_id}` | 已重新编码的 PNG | 与所属反馈采用同一权限；随机 ID，不接受文件路径 |
| `PATCH /api/v2/feedback/{feedback_id}` | 状态/审核 | 仅维护角色，必须同时传 status/visibility；类别对应状态集合校验 |

公开记录只返回 `feedback_id/kind/category/title/description/status/created_at/updated_at/attachments`。附件只返回 `attachment_id/mime/width/height/size`。创建响应只返回 `feedback_id/pending_review`，不建立作者映射。反馈编号和附件编号为独立随机 128 位值。

问题状态：pending/in_progress/resolved；建议状态：evaluating/planned/accepted/declined。发布范围：public/pending/hidden。创建不自动标记解决或采纳。

标题最多 120 字符、描述最多 4000 字符；截图对象只允许 mime/data（严格 Base64），不接收原文件名。JSON 上传最大 9 MiB；原有身份接口维持 64 KiB 请求/响应限制。客户端反馈列表响应独立限为 512 KiB；附件响应最多 6 MiB。反馈及附件响应使用 no-store、nosniff 和 no-referrer。

## 图片与持久化

- 只接受真实 PNG/JPEG，MIME 必须与魔数和解码格式一致；畸形、动画、多帧、过大图像拒绝。最多四张、原始每张 2 MiB、总计 6 MiB；尺寸最长边 4096，像素数最多 1200 万。重新编码后的附件总计也不超过 6 MiB。
- 服务端使用 Pillow 验证、重新解码、校正 EXIF 旋转，从新像素对象编码 PNG；不保留 EXIF、ICC、文本 metadata、源文件名或原始上传。像素内容须人工审核。
- `backend/v2/requirements-feedback.txt` 声明图片依赖。未安装时纯文字仍可用，截图提交返回安全的 503；反馈模块不影响原有服务启动。
- 独立目录默认 `<MUYING_V2_DATA_DIR>/feedback`，可用 `MUYING_V2_FEEDBACK_DIR` 指定 NAS 本机受控目录；**不可放在 SMB 业务树、公开 Web 目录或客户端网络盘**。
- `feedback.db` 独立 SQLite 数据库，仅含反馈和附件两张业务表，图片为重新编码的 BLOB；一个事务提交反馈和全部附件。失败不留下部分可见反馈或孤立原图。 schema 版本 1，遇到更高版本拒绝操作，不能静默降级。
- 附件数据总量上限 1 GiB，记录数上限 50000；达到上限返回 507。隐藏不物理删除，未提供删除/自动回收功能；维护人员应安排存储监控与后续容量策略。
- 维护操作写入隔离的 `maintenance.db`，只记录维护账号、反馈编号、时间及最终状态/公开范围。通过同一 SQLite 回滚日志事务提交状态与审计，不记录原提交者；不能借此反查作者。两库须位于同一 NAS 本机文件系统；不要擅自改成 WAL 跨库事务。
- 防滥用为进程内全局 10 分钟 30 次窗口，仅存瞬时时钟数值；不以身份、IP 或会话作为键。该限制可能使员工共享限额，且重启后重置；没有长期身份跟踪。

## 日志与生产验收边界

V2 handler 原有访问日志禁用，反馈异常返回固定安全文案；HTTP server 的默认 socket 异常日志也被禁用，避免错误 traceback 泄露客户端 IP。反馈代码不记录请求体、认证头、原文件名、User-Agent、IP 或跨请求关联键。

**尚未验证**：生产 NAS、TLS、反向代理、监控/APM、系统日志和备份是否仍能通过 IP、时间、请求内容、搜索参数、认证头或关联 ID 追溯提交者。部署前应关闭反馈请求访问/内容日志与监控采样，限制身份库/维护审计/备份的访问，检查目录与数据库实际权限以及全链路日志。不能宣称网络基础设施层绝对不可追踪。登录账户系统本身仍按原有用途保留账户资料，反馈业务没有建立关联。

未改变生产服务配置，未向生产 NAS 部署、重置数据、创建账号或调整 ACL；合并及上线需另行授权。

## 验证记录与复现

| 验证 | 结果 |
| --- | --- |
| V2 Python 完整测试集 | 33 项通过：原有 20 项回归 + 13 项反馈专属安全/HTTP 测试 |
| NativeWpf Release 编译 | 0 错误、0 警告；仍为 net8.0-windows |
| 原生 WPF 反馈验证程序 | 草稿隐私确认、图片数量与准备期间禁止提交、匿名序列化、页面实例/队列保留；Light/Dark 的列表/详情/表单/空/加载/错误及有失败任务的传输页渲染，150%/200% DPI，无绑定错误 |
| 实际渲染检查 | 检查浅色列表、深色表单和详情，修复行高裁切及类型对象文本；反馈专属样式沿用现有令牌，不改通用主题 |
| HTTP 路由/权限/请求上限/响应头 | 使用真实 BaseHTTPRequestHandler 解析内存中的 HTTP 请求并验证结果；环境禁止回环 socket 连接，真实网络未验证 |
| Windows 10/11 人工键盘、剪贴板/拖入、屏幕阅读器 | 未验证；仅原生控件/焦点/可访问名称和程序化渲染检查完成 |
| 真实 NAS/SMB 传输持续运行及生产匿名性 | 未验证；导航验证保留 ViewModel/任务实例，没有实机 NAS 传输测试 |

从仓库根目录运行：

```powershell
python -m unittest discover -s backend/v2/tests -v
dotnet build desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj -c Release
dotnet run --project desktop/MuYingDrive.NativeWpf.FeedbackTests -c Release -- <截图输出目录>
```

反馈测试需要 Pillow。受限 Windows 环境下可把 TEMP/TMP 指向工作区可写的测试临时目录。WPF 验证程序的内容与账号均为测试夹具，正式客户端没有假反馈或演示账号。
