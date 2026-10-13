# v3 工作流契约（覆盖冲突的 v1/v2 条款）

本文件为本次前后端实现共同依据。仍是独立合并包，未接入宿主或运行。JSON snake_case，认证/ACL/错误格式/ID验证继续沿用。所有新增依赖缺失时明确503，不能假数据成功。

## 项目、修订、导入、导出

Base `/api/v2/project-workbooks`。

- GET `/projects` -> `{items:[{id,name}]}`，来自现有NAS/宿主项目，不另建账号体系。
- GET `/` 可带 `project_id`，返回 `{items:ProjectFile[]}`；WPF先选项目，再列文件。
- ProjectFile追加 `project_id:string|null, revision_of:string|null, revision_label:string|null`。version是当前可导出快照版本；正文/图片/讨论修改影响导出时宿主须更新该版本，或导出请求冲突而非伪装最新。
- POST `/create` v3 `{template_kind,project_id,operation_id}`；模板仍仅scene_beats/asset_review。
- POST `/import` 原始xlsx；除X-File-Name，加X-Project-Id、X-Operation-Id，可选X-Revision-Of（明确导入为该文件新修订）。头中的ID用UTF8 percent编码。返回新文件ProjectFile；原文件和讨论保留，不按行号或beat号迁移评论。
- operation_id客户端产生UUID格式字符串（作为安全ID验证即可），每次明确提交唯一，超时恢复须保持同一ID与相同内容。服务端按actor+operation_id+操作种类幂等，payload指纹不同409；成功响应与数据同事务提交。客户端不自动重试mutation；若失败结果未知，保留待重试描述及operation_id，仅允许用户显式重试相同操作或取消。
- 客户端待处理操作按服务器API根地址与稳定账号ID的SHA256隔离存储，提交前核对当前scope。宿主必须注入稳定账号ID回调；缺失时创建/导入失败，不用token或共享默认账号代替。账号/服务器切换须重建页面与服务。
- GET `/{file_id}/revisions` -> `{items:ProjectFile[]}`，同一修订链且权限过滤，可打开旧版。不自动把总表与单集视作同一个文档，新修订必须由用户明确选择。
- GET `/{file_id}/export?expected_version=N` -> 当前快照xlsx。正文、原六列、图片关系、评价讨论以明确工作表写入；保留所有源工作表/未知列，新增讨论表带目标ID、作者、时间。原件GET original保留。导出可由宿主实现，未实现503；不能回退原件。版本不符409，跨表快照一致。客户端流式保存临时文件，成功后替换用户选择的目标文件，失败清理临时文件，禁止截断已有文件。

## 节拍编辑、来源、上下文

SceneRow追加 `legacy_notes:[{label,text,source_cell}], scene_context:string|null, cast_context:string|null, origins:[{file_id,row_id}], can_edit:bool`。原六列即使空也保留label及source_cell，不认领原批注作者。scene_context/cast_context来自原文同场上下文，不编造新信息。原文story_content只读。

- PUT `/{file_id}/scene-rows/{row_id}` `{expected_version,label_level,breakdown_plan}` -> 更新SceneRow。等级允许红/黄/绿/null；规划允许空字符串或null，最多20000 Unicode字符。同事务权限+版本检查，增长行版本并使导出版本失效/增长。
- PUT `/{file_id}/scene-rows/{row_id}/origins` `{expected_version,origins:[{file_id,row_id}]}` -> SceneRow。最多100个唯一来源，必须是同项目中其他修订文件的真实节拍行，不能自己指自己；宿主防环。此操作仅关联来源、供查看旧讨论，不复制/合并评论。
- 场景行按稳定内部ID标识，显示用场景/beat编号不是主键。多对多来源允许重拆，用户显式选择，禁止自动猜测。

## 节拍讨论与意见处理

v2评论路由扩展到 `/{file_id}/scene-rows/{row_id}/reviews` 与 `/review-summaries`。请求/响应与asset-rows相同，但只允许image_id=null/省略；不允许vote。后端存储必须隔离row_kind，不能与同ID的asset行混淆。

ReviewComment追加 `resolved:bool, resolution_note:string|null, can_resolve:bool`。
- PUT `/{file_id}/{asset-rows|scene-rows}/{row_id}/reviews/comments/{comment_id}/resolution` `{expected_version,resolved,resolution_note}` -> ReviewThread；note最多4000字符，标记resolved=true需非空说明；false可清空。检查独立处理权限，允许作者或宿主授权处理者，权限仍由服务端判定，不等于正文写权限。
- 评论正文仅作者编辑删除；修改正文使已解决意见重新待处理，清除旧解决说明，避免新意见看似已落实；处理状态变化增长评论版本。投票仍好/不好/撤回，不等于最终选定，更不改变红黄绿。
- 打开时取最新；v3.1去掉常驻刷新，失败时可重新加载，成功写入更新当前讨论。重新打开可查看其他成员的新意见。不宣称实时推送或跨设备已读状态。

## 素材检索、版本、比较与选定历史

ImageAsset追加 `project_id:string|null, category:string|null, variant:string|null, revision_label:string|null`。image id必须对应不可变字节版本，覆盖同路径需新id；名称不做身份。
- GET `/image-assets?project_id=...&q=...&category=...&cursor=...&page_size=40` -> `{items:ImageAsset[],next_cursor:string|null}`；project_id必填于v3客户端，category可省略或角色/场景；q为名称/形态关键词，最多200字符；page_size 1–100，cursor不透明。结果按稳定顺序分页，授权过滤在分页前，不能把漏页称完整。旧无查询可兼容旧接口。
- POST image-assets 带X-Project-Id，作为归属/可关联范围；服务器校验权限。若Host还未提供项目检索，返回503，不退回全库。
- AssetRow追加 `variant:string|null, can_edit:bool`。每行是具体用途/形态，仍单张选定、多张备选。未知或尚未填形态允许null，不猜测。
- PUT images原请求可加 `selection_reason:string|null` 最多4000字符；只在选定发生变化时记录事件。
- GET `/{file_id}/asset-rows/{row_id}/selection-history` -> `{items:[{id,previous_image_id,image_id,user_name,created_at,reason}]}`，image_id及previous允许null；身份从会话获取。更换/清空都记录，候选增删不冒充选定变更；同事务权限检查、版本更新、历史落库。高票不自动选定。

## 有界流式传输

默认xlsx及图片原件上传64 MiB（可由宿主配置，客户端相同配置）；不再使用20 MiB。前端流式HttpContent明确Content-Length，保留调用方流所有权，禁止把大文件复制成多份byte[]。非seek输入可有界暂存到本地临时文件。后端HTTP入口按声明长度分块读到SpooledTemporaryFile，鉴权/长度检查先于读body，再交给显式stream adapters；不改变原图片以绕过限制。直接dispatch(bytes)只供已经自行限流的宿主使用。

JSON请求64KiB（节拍更新允许256KiB以容纳20000字符），JSON响应25MiB、预览图片64MiB、导出/原件下载256MiB，均有总时限并检查实际读取字节。上传默认180秒、普通请求90秒、导出180秒，可宿主调整；无无限制read。实际图片内容验证、像素上限、xlsx结构验证仍由宿主adapter负责。

## 界面范围

保持宿主主题与“项目”入口。只允许xlsx导入/两模板新建。顶部项目及集数切换、节拍搜索/等级筛选；v3.1正文默认全文显示，点击行看批注，保留上下文与六列来源批注。可编辑规划/等级、打开节拍讨论、查看并手工关联旧行。新建按钮简化为“新建节拍表”“新建资产表”，删除常驻刷新与首页拖入说明；失败恢复不等于常规刷新功能，不新增轮询或自动重试写请求。

素材选择先显示当前页元数据，缩略图有限并发加载；搜索/类别过滤及加载下一页。资产行按需加载图，主表先出现；至少提供2–4图并排查看和放大。关联剧本/分镜仅作为宿主现有文件功能的接入说明，本包不增加DOCX转换或全能分镜编辑器。
