# WPF v3.1 合并说明

本目录是独立 net8.0-windows / UseWPF 类库，命名空间 MuYingDrive.ProjectWorkbooks，无 App 入口。仅交付源码：未编译、未启动程序、未运行测试。优先合同为 ../contracts/WORKFLOW_V3.md。

## 宿主接入

1. 以 ProjectReference 引入本目录 csproj，或迁入源文件并保持 XAML x:Class 与命名空间一致。
2. 在现有侧栏“其他部门”之后新增“项目”，用已有导航机制显示 ProjectWorkbooksView。
3. 注入宿主现有认证与证书校验策略的 HttpClient。类库不创建或释放它；不得绕过证书校验、反射取得私有 client，或安装自动重试写请求的 handler。
4. token 与稳定账号 ID 均从当前会话读取。账号 ID 是后端认证主体稳定 ID，不是昵称或会变化的 token。以下函数名仅为接线占位，不假定重构后的字段。

```csharp
var service = new HttpProjectWorkbookService(
    hostHttpClient,
    new Uri(serverOrigin + "/api/v2/project-workbooks/"),
    getCurrentSessionToken,
    new WorkbookTransportOptions { MaxUploadBytes = 64L * 1024 * 1024 },
    getCurrentStableActorId);
var page = new ProjectWorkbooksView
{
    DataContext = new ProjectWorkbooksViewModel(service)
};
// 将 page 交给宿主现有 Workspace 导航机制。
```

切换账号/服务器时销毁旧页面和服务并重新创建，避免在旧页面继续操作旧项目。OperationScope 为 API 根地址与稳定账号 ID 的 SHA256；待处理创建/导入按 scope 分目录持久化，提交前核对 scope。缺少账号回调时 v3 创建/导入明确失败，不默认所有账号共享待重试状态。客户端隔离不能代替后端 actor + operation_id 幂等及权限检查。

## 已接入的工作流

- 先选择项目，再查看 SQLite 中的工作簿；xlsx 仅用于导入、导出快照和旧数据迁移。新项目自动建立节拍表和资产表，转换过程不作为用户步骤。
- 明确导入新修订、查看修订列表；失败结果未知时保存操作 ID 与文件指纹，用户主动重试。不自动重传，不按行号迁移评论。
- 节拍搜索与等级筛选；查看故事内容、上下文、六列原批注及来源。故事内容可单击编辑，Enter 插入换行、Ctrl+Enter 保存；规划和红黄绿在详情中编辑。来源关联只允许其他修订的真实节拍，不复制旧讨论。
- 工作簿行文字可拖选复制；资产类别和名称可单击编辑，Enter 或失焦保存，Esc 取消。资产行保存类别/名称时，后端同步移动对应目录并保留稳定图片 ID。
- 节拍讨论、资产整体讨论与图片评价分开。好/不好/撤回，独立发表评论；按权限编辑、删除、处理与重新打开，全部带版本检查。
- 素材按项目、关键词、类别分页检索；元数据先显示、缩略图按需且有限并发加载。图片显示名称是 SQLite 元数据，重命名不改磁盘文件名；图片对比、缩放与平移；单张选定、多张备选，从后端选择或本地拖入 PNG/JPEG。
- 选定变化可附原因并查看历史，高票不自动选定。图片 ID 对应不可变版本，更换内容须新 ID。
- 原件下载与当前导出分开；导出先取最新文件快照版本，随后修改造成409，而非导出旧文件冒充最新。

## 主题与布局

复用 DynamicResource：Brush.Surface.Canvas、Brush.Surface.Panel、Brush.Border.Subtle、Brush.Text.Primary、Brush.Text.Secondary、Brush.Accent.Primary、Brush.Status.Danger、Typography.FontFamily、Button.Primary、Button.Secondary。本地回退只在本模块内，加载时让位于宿主资源；不写入 App.Resources。弹窗继承页面主题。

主表字段随宽度重排、图片换行，主页面禁用横向滚动；窄窗口纵向展示字段。通用工作表提供按列顺序的单元格值兜底并明确 values_only，不宣称完整复刻 Excel 排版。图片放大后的平移与主表无横向滚动的目标不同。

## 传输与并发

默认上传64MiB，与后端 upload_limit 同步配置。确定 Content-Length 的流式 HttpContent，非 seek 流有界临时落盘，保留调用者流所有权。普通请求90秒、上传/下载180秒，JSON响应25MiB、预览64MiB、下载256MiB；通过 WorkbookTransportOptions 配置有限正值。宿主 HttpClient.Timeout 若更短会先终止请求，需协调；服务端解析/导出计算也必须自行限制资源和时间。

下载使用同目录临时文件，检查大小、MIME 和 ZIP 头，完整成功才替换目标，失败不截断已有文件。这不代替服务端真实 xlsx 结构验证。真实格式、图片像素、xlsx 展开上限与 ACL 由宿主适配器检查。

表格和图片显示名称写入通过字段版本或稳定 ID 定位：同一表格字段并发冲突返回 409，不同字段可独立提交。SQLite 事务提交后通过 SSE 发送变更，其他打开同一项目的客户端收到后立即重读受影响数据。远端刷新时保留本地输入草稿。

WPF 原生解码可靠范围 PNG/JPEG；后端 WebP 查看接口需实际转码，不能只改 Content-Type。所有图像通过同一认证客户端获取。

## 仍由宿主完成

项目/NAS 映射、ACL、模板真实创建、xlsx 完整解析与未知列保留、不可变媒体版本、缩略图、当前快照 xlsx 导出及 ExportTouch 事务接线。缺适配器返回503，不伪造成功或以原件替代当前导出。关联剧本/分镜沿用宿主文件功能，不含 DOCX 转换。

新建节拍表和资产表由 SQLite 建立逻辑表结构，初始为空；界面提供新增节拍行和资产行。图片显示名称只改元数据，类别/资产名称变化才触发资产目录移动。XLSX 只用于导入和导出快照，不是多人实时编辑的数据源。

## 合并后验收

用户允许运行后，再编译并按 ../ACCEPTANCE.md 验收深浅主题、窄窗口、真实31工作表/209节拍、独立第26集、107张素材、26,910,388字节大图、并发409、网络失败重试、账号切换、权限和导出一致性。本次静态检查见 ../STATIC_REVIEW_V3.md，不能替代运行验收。


## v3.1 界面简化

节拍主行仅保留五个字段，故事内容和拆分规划默认完整显示，不截断、不要求展开。故事内容文本框可单击编辑，普通 Enter 插入换行、Ctrl+Enter 保存；点击其他行区域或按 Enter / Space 打开详情。事件排除子控件操作，避免按钮冒泡误开。详情首先显示六列原批注，保留空单元格与来源位置；下方编辑等级和规划，原文/上下文位于底部折叠区，固定工具栏提供保存、讨论和来源。

首页采用“新建节拍表”“新建资产表”，删除拖入说明。主页面通过项目 SSE 变更事件实时更新，不对整张表做定时轮询。请求失败、断网、409或大图读取失败时才出现“重新加载”；恢复操作不自动重放写请求。规划、来源选择、评论及解决说明草稿保留，评论必须重新确认目标版本后才能保存。规划/来源遇到不确定结果会阻止再次保存，直至用户明确重新加载并核对；导入/新建继续使用保留操作标识的“重试这次操作”入口。

默认全文增加长节拍的行高；主表控件仍未虚拟化，不能据静态源码声称性能已验收。此次只修改独立包界面及说明，未编译、运行或执行程序测试。
## v3.2 布局补充
每条节拍顶部三列Grid并排显示场景号、节拍号、制作难度，下方纵向显示完整正文和规划。点击行查看批注保持不变。
