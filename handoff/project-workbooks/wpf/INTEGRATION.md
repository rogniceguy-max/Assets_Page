# WPF 工作簿与项目空间合并说明

本目录是独立 net8.0-windows / UseWPF 类库，命名空间 MuYingDrive.ProjectWorkbooks，无 App 入口。`ProjectSpaceShell` 是新的导航壳层，`ProjectWorkbooksView` 是工作簿内容页。工作簿内部字段/API 约定见 `../contracts/WORKFLOW_V3.md`；空间与文件夹接口要求见`../contracts/PROJECT_SPACE_V1.md`。

## 当前项目空间模型

- 左侧全局主导航：`图片`、`视频`、`个人空间`、`传输`、`反馈`；独立分组`项目 +`用来创建可命名、可重命名的共享空间。
- 全局图片/视频请求使用`ProjectId = null`，按所有项目查询，并由宿主按项目归组后通过`ReplaceAllProjectMedia`应用结果。项目树下的图片/视频节点带具体项目 ID；资产图片文件夹节点还带`FolderId`。
- 聚合页只读浏览，不提供无目标项目的上传/软删除动作。项目或文件夹页面才可对明确项目进行导入、软删除及回收站恢复。
- 项目创建事件携带必需的`节拍表`、`资产表`模板类型。宿主必须一次完成项目和两份默认工作簿的创建，成功后调用`ApplyProjectCreated(requestId, snapshot)`；失败调用`FailProjectCreation`。树只显示后端确认快照中的对象。xlsx 导入成功后调用`ApplyProjectSnapshot`更新项目文件卡片；失败通过`SetHostStatus`反馈。
- 创建资产行必须由工作簿 API 原子地创建或绑定图片文件夹。保存成功后，宿主将对应`ProjectSpaceAssetFolderRecord`交给`ApplyAssetRowCreated`；不要根据未保存草稿在客户端造文件夹。
- 全体用户可读写项目空间；个人空间仅本人可访问。个人空间的页面和权限仍由宿主身份/空间服务实现，不能用项目 API 冒充。

### 宿主接线点

设置`ProjectSpaceShell.DataContext = new ProjectSpaceShellViewModel()`后，加载宿主快照并调用`ApplyProjects`。为下列事件接入真实服务，并在成功后调用对应的 Apply/Replace 方法：`CreateProjectRequested`、`RenameProjectRequested`、`MediaRequested`、`WorkbookImportRequested`、`MediaUploadRequested`、`ArchiveMediaRequested`、`MediaOpenRequested`、`RecycleBinRequested`、`RestoreRequested`、`WorkbookOpenRequested`、`HostNavigationRequested`。媒体加载、上传、归档或恢复失败时调用`SetHostStatus`，成功时刷新对应的媒体组或回收站列表。

`WorkbookOpenRequested`由宿主使用已有的`ProjectWorkbooksViewModel`加载指定项目/工作簿，再以`SetHostContent`显示工作簿页；打开前设置当前项目并加载工作簿。该 VM 的`BackToProjectRequested`应调用`shellViewModel.OpenProjectOverview(projectId)`。资产行创建成功后，`AssetFolderCreated`需转发至`shellViewModel.ApplyAssetRowCreated(...)`；图片软删除成功后，`AssetImageArchived`需触发回收站数据刷新。所有事件都代表请求，不代表写入成功。异常或未接入服务应明确反馈，不得提前显示项目、行、文件或回收站状态。

## 宿主接入

1. 以 ProjectReference 引入本目录 csproj，或迁入源文件并保持 XAML x:Class 与命名空间一致。
2. 将`ProjectSpaceShell`接入主窗口原左侧导航区域，避免同时保留旧的“本部门 / 公共素材 / 其他部门 / 项目”分组。工作簿页作为壳层内容挂入；不要再单独增加同名顶层项目入口。
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

- 项目空间默认包含`节拍表`和`资产表`两份工作簿；导入入口仅接受 xlsx。转换过程不作为用户步骤。
- 明确导入新修订、查看修订列表；失败结果未知时保存操作 ID 与文件指纹，用户主动重试。不自动重传，不按行号迁移评论。
- 节拍搜索与等级筛选；查看原文、上下文、六列原批注及来源。原文只读，规划和红黄绿可编辑。来源关联只允许其他修订的真实节拍，不复制旧讨论。
- 节拍讨论、资产整体讨论与图片评价分开。好/不好/撤回，独立发表评论；按权限编辑、删除、处理与重新打开，全部带版本检查。
- 素材按项目、关键词、类别分页检索；元数据先显示、缩略图按需且有限并发加载。选择器显示本表已关联的图片；逐图移除会软删除并进入项目回收站，评论和评价保留。选定从项目素材库选择或拖入单张图片；备选直接拖入一个或多个本地 PNG/JPEG 文件到对应资产文件夹。查看大图支持缩放与平移。
- 用户直接选择选定图片，不要求填写原因，也不记录选定历史；好/不好评价不自动决定选定。图片 ID 对应不可变版本，更换内容须新 ID。
- 原件下载与当前导出分开；导出先取最新文件快照版本，随后修改造成409，而非导出旧文件冒充最新。

## 主题与布局

复用 DynamicResource：Brush.Surface.Canvas、Brush.Surface.Panel、Brush.Border.Subtle、Brush.Text.Primary、Brush.Text.Secondary、Brush.Accent.Primary、Brush.Status.Danger、Typography.FontFamily、Button.Primary、Button.Secondary。本地回退只在本模块内，加载时让位于宿主资源；不写入 App.Resources。弹窗继承页面主题。

主表字段随宽度重排、图片换行，主页面禁用横向滚动；窄窗口纵向展示字段。通用工作表提供按列顺序的单元格值兜底并明确 values_only，不宣称完整复刻 Excel 排版。图片放大后的平移与主表无横向滚动的目标不同。

## 传输与并发

默认上传64MiB，与后端 upload_limit 同步配置。确定 Content-Length 的流式 HttpContent，非 seek 流有界临时落盘，保留调用者流所有权。普通请求90秒、上传/下载180秒，JSON响应25MiB、预览64MiB、下载256MiB；通过 WorkbookTransportOptions 配置有限正值。宿主 HttpClient.Timeout 若更短会先终止请求，需协调；服务端解析/导出计算也必须自行限制资源和时间。

下载使用同目录临时文件，检查大小、MIME 和 ZIP 头，完整成功才替换目标，失败不截断已有文件。这不代替服务端真实 xlsx 结构验证。真实格式、图片像素、xlsx 展开上限与 ACL 由宿主适配器检查。

行/评论写入使用 expected_version；409 不自动重试。编辑草稿不能因重新加载或切换悄悄丢失。有限并发/缓存并不表示超大工作簿性能已验证；评论和工作簿暂无分页，超出有界响应限制须显式失败。

WPF 原生解码可靠范围 PNG/JPEG；后端 WebP 查看接口需实际转码，不能只改 Content-Type。所有图像通过同一认证客户端获取。

## 仍由宿主完成

项目/NAS 映射、ACL、模板真实创建、xlsx 完整解析与未知列保留、不可变媒体版本、缩略图、当前快照 xlsx 导出及 ExportTouch 事务接线。缺适配器返回503，不伪造成功或以原件替代当前导出。关联剧本/分镜沿用宿主文件功能，不含 DOCX 转换。

项目创建时的两份默认表由宿主版本化模板提供。资产表现已提供“新建资产”入口，但宿主需实现`CreateAssetRowAsync`：保存资产行和对应图片文件夹，并返回工作簿版本及稳定文件夹映射。备选拖入与素材移除分别依赖文件夹内上传/关联和软删除接口；接口要求见`../contracts/PROJECT_SPACE_V1.md`。通用单元格编辑、公式重算仍未提供。

## 合并后验收

用户允许运行后，再编译并按 ../ACCEPTANCE.md 验收深浅主题、窄窗口、真实31工作表/209节拍、独立第26集、107张素材、26,910,388字节大图、并发409、网络失败重试、账号切换、权限和导出一致性。本次静态检查见 ../STATIC_REVIEW_V3.md，不能替代运行验收。


## v3.1 界面简化

节拍主行仅保留五个字段，故事内容和拆分规划默认完整显示，不截断、不要求展开。点击整行或聚焦后按 Enter / Space 打开详情；事件排除子控件操作，避免按钮冒泡误开。详情首先显示六列原批注，保留空单元格与来源位置；下方编辑等级和规划，原文/上下文位于底部折叠区，固定工具栏提供保存、讨论和来源。没有重复的展开/编辑行内按钮。

项目首页由壳层列出服务端确认的两份默认工作簿；不重复显示“新建节拍表/新建资产表”按钮或拖入说明。工作簿主页面和讨论窗口没有常驻刷新按钮，打开时读取数据，不轮询。请求失败、断网、409或大图读取失败时才出现“重新加载”；恢复操作不自动重放写请求。规划、来源选择、评论及解决说明草稿保留，评论必须重新确认目标版本后才能保存。规划/来源遇到不确定结果会阻止再次保存，直至用户明确重新加载并核对；导入/新建继续使用保留操作标识的“重试这次操作”入口。

默认全文增加长节拍的行高；主表控件仍未虚拟化，不能据静态源码声称性能已验收。此次只修改独立包界面及说明，未编译、运行或执行程序测试。
## v3.2 布局补充
每条节拍顶部三列Grid并排显示场景号、节拍号、制作难度，下方纵向显示完整正文和规划。点击行查看批注保持不变。
