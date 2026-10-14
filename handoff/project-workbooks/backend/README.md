# 后端 v3 独立合并包

**仅静态阅读/编辑；没有运行、编译、测试、启动服务器或执行 SQL，没有修改原工程。** 权威契约为 `../contracts/WORKFLOW_V3.md`，覆盖旧版冲突内容。`README_V2_HISTORY.md` 仅留档，不适用于当前模块接线。

沿用宿主 stdlib `BaseHTTPRequestHandler`、`V2Application`，不引入 Web 框架或账号体系。NAS 项目、完整 Excel 导入/导出、素材索引、权限、存储元数据必须接宿主真实实现。缺失依赖明确503，不返回假文件，不把 original 冒充当前导出。

## 文件与完成边界

| 文件 | 已提供 |
|---|---|
| projects_api.py | 全量v1/v2/v3分发、请求验证、鉴权入口、有界暂存上传与流式响应 |
| workflow_services.py | v3宿主 Protocol、StreamAsset、同事务ExportTouch协议 |
| workflow_store.py / workflow_schema.sql | 真实SQLite节拍规划/等级/来源事务、防环、幂等helper |
| image_relations.py / images_schema.sql | 原子选定/备选关系、版本检查及export版本回调 |
| review_store.py / reviews_v3_schema.sql | asset/scene隔离、单人单票、多评论、作者修改删除、独立处理权限与版本 |
| migrate_reviews_v2_to_v3.sql | 仅针对此前独立包v2评价表的可选一次性迁移草案 |
| reviews_schema.sql | v2旧schema留档；新装无需应用 |

**没有实现真实宿主 WorkflowServices。** Protocol 的 `...` 表示接口定义，不是假成功占位。本包不是新服务器，也不是完整NAS管理系统；需完成下面的adapter后方能运行。

## HTTP 接线

建议作为宿主 `backend/v2/project_workbooks/` 包合并。以下 `host_*` 均为真实宿主实现，不是可直接运行的demo变量：

```python
from .project_workbooks.projects_api import ProjectsApi
from .project_workbooks.review_store import ReviewStore

asset_reviews = ReviewStore(host_connect, host_principal, host_review_access,
                            host_row_image_ids, host_export_touch, row_kind="asset")
scene_reviews = ReviewStore(host_connect, host_principal, host_review_access,
                            host_row_image_ids, host_export_touch, row_kind="scene")
projects = ProjectsApi(host_security, host_legacy_services,
    reviews=asset_reviews, scene_reviews=scene_reviews,
    workflow=host_workflow_services, upload_limit=64 * 1024 * 1024)

# 现有 do_GET/do_POST/do_PUT/do_DELETE 的最前面加入相同分发：
if projects.handle(self):
    return
# 继续宿主原有该方法的路由，不能覆盖原方法全部内容。
```

必须在原 `_read_json()` 之前接线，保留TLS、证书验证、session失效、socket/并发限制。Security.authenticate接收小写header键，从bearer得到可信principal；缺失/过期401。宿主已知错误转ApiFault，不能向客户端返回exception原文、token或路径。

v2无query文件/素材列表与旧create JSON仍保留；新项目筛选不能退回全库。workflow已配置时，旧shape图片PUT也走workflow.replace_images，确保同事务更新关系与export版本。图片关联不收集原因，也不保留选定历史。HTTP上传统一调用stream接口，v2缺项目头时传None，由宿主明确旧scope或报错，不猜默认管理员项目。

dispatch(bytes)仅供已自行限流读取的宿主；v3 bytes上传包装BytesIO给stream adapter，不是大文件推荐入口。dispatch可能返回stream Response，发送者必须按length发送、保留headers并关闭stream；未知异常由调用者净化兜底。handle已提供这些处理。

## 有界流式传输

- 上传默认64MiB，upload_limit必须正整数且与客户端配置一致。先认证/检查长度，再读body；拒绝重复Content-Length、Transfer-Encoding、超限。缺stream adapter先503。
- 每64KiB写入SpooledTemporaryFile，内存阈值1MiB，超出落本地临时文件。adapter接收BinaryIO+length，禁止无界read或转多份bytes，也不能异步保留此流；调用结束自动关闭。
- 普通JSON64KiB，节拍更新/来源JSON256KiB（容纳20k非BMP字符被JSON转义）；JSON响应25MiB，图片预览64MiB，工作簿原件/当前导出256MiB。超限明确报错，不静默截断。
- 上传网络读取180秒，普通读/响应90秒，工作簿发送180秒；逐块检查总期限，临时socket timeout结束恢复宿主原值。未读完body的拒绝关闭连接。
- **网络传输计时不限制adapter计算**：宿主还须把请求deadline/cancellation落实到Excel解析、导出、NAS I/O，并限制并发/工作量；不能声称无限运行的exporter已有180秒计算限制。
- StreamAsset须非空、指针0、可seek、不可变快照，长度及MIME正确。发送headers前验证真实长度，发送中再检查实际读取并最终关流。非seek NAS源由宿主有界暂存后返回；不可边发送边覆盖源文件。
- 字节验证归宿主：真实PNG/JPEG/WebP解码、尺寸/像素/帧数/成本；真实xlsx OOXML/ZIP、路径/成员数/展开量/压缩炸弹。扩展名与MIME不等于验证。保留原件，不压缩原图绕限制。WebP向原生WPF提供PNG/JPEG rendition并用实际Content-Type。

## 必填宿主 WorkflowServices

| 方法 | 宿主必须保证 |
|---|---|
| projects / list_files | 真实NAS项目、用户可见文件，包含project/revision元数据及当前export version |
| create | 两模板生成真实xlsx，项目创建ACL，同事务operation_id幂等与元数据发布 |
| import_xlsx_stream | 原件保留、完整所有sheet/未知列、稳定内部row_id；项目写权和revision_of读取/同项目修订资格同事务校验；不按beat或行号搬评论 |
| list_images | 项目范围、授权过滤在稳定分页之前；q≤200、category角色/场景、page_size1–100，返回items+next_cursor；未接项目检索503 |
| upload_image_stream | 项目上传与可关联权、验证原图、每次字节版本分配不可变ID；名称不当身份，variant不从文件名自动猜 |
| image_content_stream / original_stream | 每次GET资源ACL、不可变可seek快照，正确length与类型 |
| revisions | 同一真实修订链、权限过滤、可打开旧版；总表/单集不自动同链 |
| export_current | expected_version一致、跨表完整快照；保留源sheet/未知列/原六列，写当前规划、图片及带目标/作者/时间的讨论表；未实现503绝不返回原件 |
| update_scene / update_origins | 可委派SceneStore同名方法；源字段完整投影仍来自宿主 |
| replace_images | 可委派ImageRelations.replace，同事务更新图片关系与export版本 |

路由不猜测/修补宿主DTO。adapter必须返回全部必需数组与新字段，安全URL段ID，文件ID不能取create/import/image-assets/projects，版本正整数；未知sheet/列要保留或明确不支持。原story_content只读；六个legacy_notes即便空也保留label/source_cell，不给旧批注冒认作者；scene/cast_context只从同场原文继承。

## SQLite 与导出版本

connect每次返回新建未开启事务连接并设置宿主busy timeout；store启动/提交/回滚并关闭。所有ACL、投影和ExportTouch使用传入connection，不自行commit。外部权限库/NAS不会因此自动获得跨库事务保证，需要宿主一致性策略。

ExportTouch(connection,actor,file_id)是必填：在原事务更新权威ProjectFile.version，或使导出冲突/不可用。规划、来源、图关系、投票、评论、处理状态均调用它。**不可传no-op**；无法维护则抛503回滚。行版本/评论版本/文件导出版本不同，不可互相替代。

### 节拍编辑与来源

SceneStore(connect,access,origin_access,export_touch)。在成功导入/创建的同事务，对真实行register_row；不在收到不存在行修改请求时创建。SceneAccess检查file/project与真实scene行权限，返回完整源SceneRow、准确can_edit；document必须通过read或等价批量投影叠加状态，不能继续发旧规划值。

只写红黄绿/null与≤20000Unicode字符规划，原文不改。最多100个唯一来源；同项目其他修订、真实行、可读取由OriginAccess验证。参考来源账本用递归查询+BEGIN IMMEDIATE防环；宿主若另有来源图，必须合并为同一权威图，不能只查半张图。来源只做关联，不复制评论，不用显示beat作主键。

### 图片关系

ImageRelations(connect,row_access,image_access,export_touch)。RowAccess返回category/name/appearance/variant/can_edit；ImageAccess接收file_id,row_id,write关键字，检查图片是否存在、可见且可关联。

单选、多备选、expected_version冲突409。选定与备选只保存当前关系，不记录选定原因或变化历史；候选增删不会改变选定。图片关系与export version同事务。高票不自动选定；移除引用不删源图或已有评论评价。

### 资产/节拍讨论与处理

构造两个ReviewStore，row_kind分别asset/scene；ReviewAccess新增row_kind关键字，按真实类型验证，SQL scope带类型隔离。scene仅null总体讨论，不接受图片或投票；独立评审权限不能等同metadata写权。

ReviewPermissions新增can_resolve_own_comments、can_resolve_all_comments，默认False；can_resolve按本人或授权处理者计算，服务端再次检查。正文编辑删除仍仅作者。resolved=true需说明，false可清空；处理增长comment.version。正文编辑自动重新待处理并清除旧说明。所有写操作及返回thread/counts同事务，再更新export版本。

### 迁移（未执行）

新装由宿主migration应用images_schema.sql、workflow_schema.sql、reviews_v3_schema.sql。已真正使用v2独立包表时先备份、暂停旧writer，建v3表后在宿主migration事务执行migrate_reviews_v2_to_v3.sql；核对数量/ID/内容后切换writer，旧表留到回滚窗口结束。重复执行主键冲突会失败，不忽略不同数据。宿主旧asset_feedback完全不在迁移范围内，禁止猜它属于某图片。

## 幂等与原件发布

operation_fingerprint(metadata,source,length)分块SHA256，恢复流位置；metadata含操作种类、project、template/文件名、revision_of、长度。idempotent_result(connection,user_id,operation_id,kind,fingerprint,work)放在宿主BEGIN IMMEDIATE内、当前session/project/revision ACL之后。work用同连接写业务元数据并返回ProjectFile；响应与业务同事务提交。相同key/指纹返回原结果，不同内容409。

helper不启停事务，不能回滚NAS写入。宿主须先验证/落不可变暂存blob，再在DB事务发布blob引用、索引和幂等结果；崩溃孤儿按明确retention回收。缺真实blob不能返回成功，不擅自删除公共图片。可信user_id来自session，每人operation_id隔离。

未知失败保留相同operation_id和相同内容供用户显式重试；旧无ID导入不能承诺幂等。当前没有异步pending协议，create/import只能在完整文档可读后201；否则需端到端扩展契约，不能打开残缺表格却称完成。

## 合并后待验证（本次没有执行）

权限/跨项目/跨类型；64MiB流与真实25.7MiB图片、断连清理；完整31sheet、原六列、上下文；20k规划、版本冲突/来源环；同ID重试及内容冲突；修订不搬评论；评审与处理权限；图片引用原子替换；导出版本冲突、真实完整xlsx、保存失败不截断已有目标。执行时机由用户与正在重构的AI协调。
