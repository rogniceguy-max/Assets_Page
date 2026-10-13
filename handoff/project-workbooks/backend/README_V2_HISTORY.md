# v2 历史说明，已被 README.md v3 接入说明替代

**仅作设计历史。当前接口、上传大小、构造器、评论表名和导出能力以 README.md 及 WORKFLOW_V3.md 为准，不要按此文件接线当前 Python 模块。**

本目录是可合并的参考模块，**未接入当前服务器、未运行、未编译、未测试**。不修改原项目源码，不启动监听、不在导入时初始化数据库。协议以 `../contracts/API.md` 为准。

已阅读现有 `work/Assets_Page/backend/v2/server.py`：宿主使用 `BaseHTTPRequestHandler`、`ThreadingHTTPServer`、`V2Application`，因此本包不引入 FastAPI 或新的 Web 服务器。

## 文件及职责

- `projects_api.py`：完整路由分发、请求 DTO 校验、20 MiB 上传上限、统一错误、必填鉴权和业务 Protocol、HTTP handler 桥接。
- `image_relations.py`：真实 SQLite 事务实现；单张选定、多张有序备选、版本冲突、同事务权限检查、原子替换关系。
- `images_schema.sql`：独立命名的新增表草案；经宿主正常 migration 审核合并，不会自动执行。
- `review_store.py` / `reviews_schema.sql`：独立的好/不好投票与多条评论事务、作者修改删除、版本冲突及统计查询；契约见 `../contracts/REVIEWS.md`。

业务服务 Protocol 使用 `...` 表达接口定义，没有返回假成功的业务实现。**不能只拷贝路由文件就宣布完整表格已实现**：必须提供下面的宿主 adapters。所有路由都委派到具名接口，没有默认管理员或默认放行权限。

## 宿主最小接线

建议把目录作为 `backend/v2/project_workbooks/` 包合并，并注入实现了 `Security`、`WorkbookServices` 的宿主类。以下为接线片段，不是独立可执行服务器：

```python
from .project_workbooks.projects_api import ProjectsApi

# HostProjectSecurity/HostWorkbookServices 由合并者实现，不能用空类占位。
projects = ProjectsApi(HostProjectSecurity(app), HostWorkbookServices(app))

# 在现有 create_handler(app) 内各方法最前面添加；保留原有方法后续代码。
def do_GET(self):
    if projects.handle(self):
        return
    # 原 GET 路由继续执行

def do_POST(self):
    if projects.handle(self):
        return
    # 原 POST 路由继续执行，包括原 _read_json()

def do_PUT(self):
    if projects.handle(self):
        return
    # 调用宿主已有 404 响应；未来存在其他 PUT 时继续其分发

def do_DELETE(self):
    if projects.handle(self):
        return
    # 继续宿主原 DELETE 路由；未匹配时返回宿主 404
```

必须在原 `_read_json()` **之前**分发，否则 xlsx/图片会被原宿主 JSON 解析及大小限制拒绝。保留宿主 TLS、证书验证、socket 超时与并发控制。`handle` 在读取 body 前验证 session，按 Content-Length 限制读取，拒绝重复长度和 Transfer-Encoding；超限/未读完请求关闭连接。反向代理也应设置上传和超时限制。当前 20 MiB 上限允许有界内存读取；未来大文件必须实现流式限长存储、取消时清理临时文件，不能先无限 read 再检查长度。

如果宿主已有等价限长读取，可调用 `dispatch(method, target, headers, body)`：非本模块路径返回 `None`，本模块返回 `Response`。此接口仅处理 `ApiFault`；宿主必须兜底隐藏未知错误。`handle` 已提供隐藏内部异常的安全错误输出。日志仅允许必要且脱敏的服务端诊断，不记录 token、body、图片内容或服务器路径。

## 必须实现的 adapters

| 接口 | 宿主责任 |
|---|---|
| `Security.authenticate(headers)` | headers 的键已统一小写；解析 `authorization`，复用既有 session 失效检查，返回可信 principal。缺失/过期返回 `ApiFault(401, 'SESSION_REQUIRED', '请先登录。')`。禁止信任 JSON 的 user_id。 |
| `list_files / list_images` | 仅列出当前 principal 可见项，不能静默截断；文件名保留 `.xlsx`，返回稳定 opaque ID。 |
| `create` | 校验当前空间创建权限；从版本化模板生成**真实 xlsx**；只支持 `scene_beats`、`asset_review`；实际落盘与元数据成功后才返回 201。 |
| `import_xlsx` | 校验当前空间写入权限；以服务端 ID 存储原始 bytes；验证真实 OOXML ZIP、解压总量/成员数/路径/压缩比限制、工作表数量和大小；拒绝伪 xlsx、损坏文件和不支持的格式。扩展名检查不等于内容验证。 |
| `document` | 校验文件读取权限；完整读取所有工作表、行、列，合并图片关系；现有业务字段提取不构成完整工作簿。陌生列/表须保留 generic fallback 或明确失败，不能静默丢弃。 |
| `original` | 鉴权后按稳定 ID 返回保留的原始 bytes 和原文件名；不接受用户提供的服务器路径。 |
| `upload_image` | 校验上传/素材写入权限；真正 decode PNG/JPEG/WebP，验证声明 MIME 与实际 bytes、宽高、总像素、帧数/解码成本；拒绝 SVG、HTML、伪图片；存储前校验且不发布本地路径。 |
| `image_content` | 每次 GET 都检查当前 session 和 asset ACL；缩略图也须鉴权；返回经过验证的实际 MIME 和 bytes。WebP 原件对原生 WPF 的缩略图和完整预览均提供 PNG/JPEG rendition，并返回对应 Content-Type；存储元数据可继续标记原件 WebP。不能直接假定原生 WPF 支持 WebP 解码。 |
| `replace_images` | 文件/行写权限与所有图片存在性/可关联权限在同一操作中校验；使用版本控制、原子替换，返回完整更新后的 AssetRow。可调用 `ImageRelations.replace`。 |

将宿主已知异常显式映射到 `ApiFault`：403 FORBIDDEN、404 NOT_FOUND、409 VERSION_CONFLICT、413 TOO_LARGE、415 UNSUPPORTED_TYPE、422 INVALID_WORKBOOK/INVALID_IMAGE/INVALID_REQUEST、503 STORAGE_UNAVAILABLE。不能将未净化 `str(exception)` 发给前端。当前 API 错误 envelope 与宿主原 `_send_error` 不同，本模块必须使用自己的 `Response`，避免 WPF 无法解析。

宿主 adapter 还负责返回 DTO 的结构校验：所有数组字段必须存在、业务显示字段统一为 string/null、ID 为安全 URL segment（非 `.`、`..`，不含路径分隔符，文件 ID 不使用静态路由保留名）、版本为正整数、图片元数据与实际图片一致。路由层只校验请求，不替宿主猜测或修补损坏的返回 DTO。`Response` 已为直接 dispatch 和 handle 两种调用都设置 private/no-store 与 nosniff；实际发送层必须保留这些 headers。缩略图查询默认 `thumbnail=true`。

`import/create` 当前契约没有 processing 状态和轮询端点：最简单正确接法是后台线程执行重任务但本次请求等待可读取文档后才返回 201。若改成真正异步索引，应先端到端扩展契约和客户端；不能返回 201 后让打开操作读到残缺工作簿。未知格式不能退回“只取几列但标记成功”。所有 import/create 都需要宿主的原子存储、失败清理和明确的重试/操作 ID 策略；本包不自动重试 mutation。

## 图片关系事务接入

1. 由宿主 migration 应用 SQL。使用宿主 SQLite 数据库时，先确认数据库确实为 SQLite，表名没有冲突；其他引擎需保持相同事务语义重新实现。
2. `connect()` 每次返回一个全新的、尚未开启事务的 connection。设置宿主认可的 busy timeout；本模块操作结束会关闭 connection。不要传入全局共享连接。
3. `RowAccess` 用传入 connection 查询文件/行 ACL 和业务字段，区分 `write=True/False`；`ImageAccess` 同样使用该 connection 查询素材权限和元数据。若权限存储在外部服务，单靠回调无法提供跨库原子保证，需要宿主锁/事务策略，不能宣称已解决。
4. 成功导入/创建且真实解析出资产行后，在相同宿主事务调用 `ImageRelations.register_row(connection, file_id, row_id)`。不要在用户提交 PUT 时创建不存在的行。重新索引须保持稳定 row_id；删除行时由宿主事务清除关系。
5. `WorkbookDocument.asset_rows` 通过 `read` 或宿主等价批量事务组装。`pwb_asset_row_state.version` 是本提案 AssetRow 的权威版本；未来修改名称等字段也必须在同一事务增长这个版本。旧列版本不能另起计数覆盖它。
6. PUT 使用 `BEGIN IMMEDIATE` + `expected_version`，冲突返回 409。检查所有目标图片后才写；任何失败整体回滚。重复候选 ID 拒绝，候选顺序保留；选定图允许同时存在于备选；清除选定不会删图片文件。
7. 上传成功但关系保存失败时，旧关系保留。未关联上传由宿主 retention 策略清理，不能在客户端重试或撤销时擅自删除公共素材。

原始 `.xlsx` 下载始终返回导入时保留的原件；网页/WPF 中的图片关系修改是独立项目数据。把修改回写成新的可下载 xlsx 属于另一个导出能力，当前没有伪装成已实现。

## 新增好/不好与独立评论（v2 提案）

`projects_api.py` 已声明并分发 `ReviewServices` 的全部六个路由；用 `ProjectsApi(security, workbook_services, reviews=review_store)` 注入实际实现。未注入时新评价路由明确返回 503，不显示伪造的零统计。旧表格路由仍可独立接线；两份 SQL 均只由宿主 migration 明确应用。

```python
from .project_workbooks.review_store import ReviewStore, ReviewPrincipal, ReviewPermissions

# 四个依赖都是宿主提供，以下变量名仅说明接线，不是放行权限的占位实现。
review_store = ReviewStore(
    connect=host_new_sqlite_connection,
    principal=host_review_principal,
    access=host_review_access,
    targets=host_current_row_image_ids,
)
projects = ProjectsApi(host_security, host_workbook_services, reviews=review_store)
```

- `principal(actor)` 必须从已鉴权的 principal 提取 `ReviewPrincipal(user_id, user_name)`。请求中不能提供/覆盖身份；保存评论时记录该可信姓名快照，宿主如需改名同步可在自身用户展示层接入。
- `access(connection, actor, file_id, row_id, image_id)` 必须在本事务检查原文件/行读取权限、图片存在性和可见性、评价读取权限，然后返回明确的 `ReviewPermissions(can_vote, can_comment, can_edit_own_comments, can_delete_own_comments)`。这套权限独立于替换选定/备选图的 metadata-write 权限，不能让只有阅览/评审权的成员无法评价。
- null 目标代表资产总体讨论；非 null 目标是当前或有历史评价记录的本行图片。宿主 hook 负责这个 scope 校验，不能仅检查“图片在全局素材库中存在”。图片移出备选后保留历史 thread，继续受文件/图片权限控制；宿主如不再允许读取，应明确返回权限/不存在错误，不能伪造空讨论。
- `targets(connection, actor, file_id, row_id)` 从真实选定/备选关系返回当前图片 ID；参考 store 去重并加入 null，按行批量聚合统计。只输出当前目标列表；不将同一图片在另一文件或另一行的评价计入本行。
- `can_edit/can_delete` 同时需要评论作者本人及相应权限。客户端 flags 只是展示依据；服务端对修改/删除重新检查作者、权限与 expected_version，不存在管理员越权修改他人评论的默认分支。

投票表以 `(file_id, row_id, image_id, user_id)` 唯一，每人每张图最多一个 good/bad；提交 null 只撤回自己的票，不删评论。general 的 image_id=null 禁止投票。评论单独多条，正文 trim 后 1–4000 Unicode 字符，拒绝孤立 surrogate。编辑和删除只接受评论的版本，不更改图片关系/资产行版本。修改记录沿用创建时间，更新时间变更；按 `(created_at,id)` 展示。

评论增删改/投票使用 `BEGIN IMMEDIATE`，将 mutation 与返回 ReviewThread 的计数、本人票、完整评论放在同一事务；失败整体回滚。读 thread/summary 使用同一只读事务快照。`connect()` 必须返回新的未开启事务连接，结束后会被关闭；ACL 和 targets 必须查询这条连接。外部权限服务不能靠该代码获得跨库事务保证，需要宿主协调。

DELETE 路由版本放 query，**没有 body**；HTTP bridge 允许 DELETE 缺省 Content-Length=0 并拒绝带 body 的评论 DELETE。WPF 的 JSON mutation 仍需使用有 Content-Length 的有界缓冲内容。所有请求字段严格匹配契约，不能捎带 user_id 或作者名。GET/general 省略 image_id，不能发送 `image_id=null` 字符串冒充 null。

没有对 `pwb_asset_row_state`/`pwb_asset_row_candidates` 建 cascade：换选定图或删除候选引用不会删除评价；新图不会继承旧图分数。现有 `asset_feedback` 属于资产层历史反馈，**不能静默迁移成某一张图片的票数**，保留为 legacy，待明确映射方案再迁移。

当前无分页、无轮询，只按手动刷新获取完整 thread。超大 thread 应端到端扩展分页，不得在后端截断后还称完整。summary 参考实现按行批量聚合；多资产行宿主可增加批量文件投影，但必须同时更新协议和 WPF，不能静默改变现有 DTO。

## 合并后验证清单（本次未执行）

合并 AI 应先完成真实 adapters，再检查：会话过期、跨空间越权、图片内容 GET 越权；伪图片和 ZIP 炸弹拒绝；只允许两种模板及 xlsx 导入；完整多工作表/未知列保留；两客户端同时改同一行只有一个成功；空选定+空备选可保存；已有候选设为选定仍保留候选；PUT 失败不删旧关系；上传超过上限在读 body 前拒绝；原始 xlsx bytes 不变。按照用户约定，何时启动/编译由负责重构的主 AI 协调。

评价补充检查：普通评审无需图片写权限即可评论/投票；好切不好只改本人票、撤回不删评论；general 只评论不投票；非作者伪造 edit/delete 返回 403；陈旧版本返回 409；并发评论不会覆盖；换图不转移分数、移出候选不删历史；跨行/文件统计隔离；缺失 summary 不能当 0；每次 mutation 返回同事务的新统计与全部评论。本次仍未运行这些验证。
