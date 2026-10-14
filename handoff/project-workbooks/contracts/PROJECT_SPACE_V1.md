# 项目空间与媒体接口要求 v1

状态：WPF 合并所需的新服务契约草案，当前参考后端尚未实现。宿主可映射到现有 API，但必须保持下述行为；界面只有在收到服务端成功响应后才更新。

## 权限与空间

- 项目空间对所有已登录用户可见、可读、可写。评论和评价仍记录真实登录用户身份。
- 个人空间完全按当前登录用户隔离；客户端传入的 user ID 不可作为授权依据。
- 全局图片/视频查询返回所有项目中的共享媒体，按项目 ID/名称分组；个人空间媒体只通过个人空间入口返回，不混入跨项目项目媒体。
- 每个新项目必须在一个后端事务中创建项目、`节拍表`（`scene_beats`）和`资产表`（`asset_review`）。失败时不得留下只有一张表的半成品项目。

## 建议路由

路径可由宿主调整，但 DTO、原子性、软删除和权限语义须保留。

| 方法 | 路由 | 行为 |
| --- | --- | --- |
| GET | `/api/v2/project-spaces` | 返回当前用户可见项目及其工作簿、图片文件夹导航快照。 |
| POST | `/api/v2/project-spaces` | 请求 `{name}`；原子创建项目和两张默认表，返回`ProjectSpaceSnapshot`。 |
| PATCH | `/api/v2/project-spaces/{project_id}` | 请求 `{name}`；只修改显示名称，稳定 ID 不变。 |
| GET | `/api/v2/media/images`、`/api/v2/media/videos` | 可省略`project_id`查询全部项目；响应按真实项目 ID/名称归组。提供`project_id`时限定该项目，可选`folder_id`限定图片文件夹。 |
| POST | `/api/v2/project-spaces/{project_id}/media/images`、`.../videos` | 将上传媒体保存至指定项目；图片文件夹页面可传`folder_id`。 |
| GET | `/api/v2/project-spaces/{project_id}/recycle-bin` | 返回该项目中已软删除的媒体及原文件夹信息。 |
| POST | `/api/v2/project-spaces/{project_id}/recycle-bin/{item_id}/restore` | 恢复同一媒体对象及原文件夹归属，不复制文件或评论。 |
| GET / POST | `/api/v2/personal-space/...` | 由宿主定义个人目录与媒体 API；服务端强制 owner-only。 |

`GET /api/v2/project-spaces`响应示例：

```json
{
  "items": [
    {
      "id": "project-id",
      "name": "孟婆",
      "workbooks": [
        {"id": "beat-file-id", "name": "节拍表", "kind": "scene_beats"},
        {"id": "asset-file-id", "name": "资产表", "kind": "asset_review"}
      ],
      "image_folders": [
        {"id": "folder-id", "project_id": "project-id", "name": "孟婆", "parent_folder_id": "role-folder-id", "asset_row_id": "row-id", "path": "图片/角色/孟婆"}
      ]
    }
  ]
}
```

媒体聚合响应必须保留所属项目，例如 `{ "groups": [{"project_id":"...", "project_name":"孟婆", "items":[...] }] }`。不可仅依赖当前选中项目过滤全局媒体。

现有工作簿素材选择器继续使用`GET /api/v2/project-workbooks/image-assets?project_id=...`分页检索；返回的媒体 ID 必须与项目图片页相同。全局媒体页可以用聚合媒体路由，不得创建第二份图片对象。

## 资产行与图片文件夹

### 创建资产行

`POST /api/v2/project-workbooks/{file_id}/sheets/{sheet_id}/asset-rows`

请求：

```json
{"expected_workbook_version": 12, "operation_id": "d5a7f8446ece4b8188e76c8e3a1bb6d9", "category": "角色", "name": "孟婆", "variant": null, "appearance": null}
```

响应：`CreateAssetRowResult`，至少包含更新后的工作簿版本和`AssetRow`；`AssetRow.image_folder`必须包含文件夹稳定 ID、所属项目、父文件夹 ID、路径/显示名及`asset_row_id`。写入资产行和创建/绑定`图片/角色/孟婆`文件夹必须原子完成。按`actor + operation_id + 操作种类`幂等，重复提交相同内容返回同一个结果，内容不一致返回409。名称相同也不能把不同资产行错误绑定到同一文件夹。

### 拖入图片

`POST /api/v2/project-workbooks/{file_id}/asset-rows/{row_id}/images`

请求使用现有流式上传格式，附带以下头：

```text
X-Project-Id: <project id>
X-Asset-Folder-Id: <row's folder id>
X-Expected-Row-Version: <current row version>
X-Asset-Image-Role: selected | candidate
X-File-Name: <encoded original basename>
Content-Type: image/png | image/jpeg
```

响应为更新后的`AssetRow`，包含`workbook_version`。服务端在一个操作中存储同一个图片文件对象、加入目标资产文件夹并更新选定/备选关系；候选拖入允许多张，客户端逐张提交并使用每次返回的新行版本。图片库和工作表必须显示同一图片 ID，不复制文件。超过行版本时返回 409，不静默覆盖。

### 选图和移入回收站

`PUT /api/v2/project-workbooks/{file_id}/asset-rows/{row_id}/images`保留现有 `expected_version / selected_image_id / candidate_image_ids` 请求。响应的`AssetRow`带`workbook_version`。选择项目现有图片时，服务端把同一个图片 ID 关联到该资产文件夹和工作表行，不复制媒体。

从“本项目素材选择器”移除图片时调用：

`POST /api/v2/project-workbooks/{file_id}/asset-rows/{row_id}/images/{image_id}/trash`

请求`{"expected_version": 8}`，响应`ArchiveAssetRowImageResult`，包含更新后的行、工作簿版本和回收站记录。此操作隐藏媒体、解除当前资产行关系并记下原文件夹；不得物理删除文件，不得删除或迁移该图片 ID 上的评论和“好 / 不好”评价。恢复后继续使用原 ID 和讨论记录。

若同一图片仍被其他资产行/工作表引用，宿主必须先定义并告知范围；不得通过级联删除使其他引用失联。

## WPF 事件对应

`ProjectSpaceShellViewModel`发出的`CreateProjectRequested`、`RenameProjectRequested`、`MediaRequested`、`WorkbookImportRequested`、`MediaUploadRequested`、`ArchiveMediaRequested`、`RecycleBinRequested`、`RestoreRequested`、`WorkbookOpenRequested`和`HostNavigationRequested`都是宿主服务请求。成功后由宿主调用相应的`ApplyProjectCreated`、`ApplyProjectRenamed`、`ApplyProjectSnapshot`、`ReplaceAllProjectMedia`、`ReplaceProjectMedia`、`ReplaceRecycleBin`、`ApplyAssetRowCreated`或`SetHostContent`。加载或写入失败时通过`SetHostStatus`报告；不得假称成功。

`ProjectWorkbooksViewModel.AssetFolderCreated`和`AssetImageArchived`供宿主转发到项目树/回收站刷新。若后端请求失败或连接结果未知，保留错误状态并重新读取确认；不要本地伪造成功。
