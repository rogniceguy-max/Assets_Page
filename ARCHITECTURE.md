# 沐影盘代码架构

本文记录当前仓库里实际运行的客户端与后端入口，避免把已移除的旧版网页或文件工作簿服务当成现行实现。

## 核心目录

```text
Assets_Page/
├── backend/
│   ├── server.py                       # 唯一 HTTPS 服务入口
│   ├── project_workbooks/              # SQLite 项目与工作簿 API、存储
│   │   ├── projects_api.py             # 工作簿 HTTP 路由、校验与 SSE
│   │   ├── sqlite_store.py             # 项目、工作簿、文件目录元数据
│   │   ├── workflow_services.py        # 工作簿 API 服务接口
│   │   └── host_adapters.py            # 会话认证与评论服务
│   ├── project_spaces.py               # SQLite 项目列表、创建、软删除与恢复
│   ├── file_storage.py                 # 文件目录、历史、操作和同步变更 API
│   └── tests/                          # 后端测试
├── desktop/
│   ├── MuYingDrive.NativeWpf/          # 原生 WPF 主程序、导航与文件工作区
│   ├── MuYingDrive.ProjectWorkbooks/   # WPF 节拍表与资产表界面组件
│   ├── MuYingDrive.UpdateCore/         # 更新协议与信任校验
│   ├── MuYingDrive.Updater/            # 更新替换进程
│   └── MuYingDrive.Bootstrap/          # 安装包引导程序
├── scripts/deploy.py                   # 后端部署工具
├── services/media_worker/              # 媒体处理服务
└── docs/                               # 需求、验收和运维文档
```

## 工作簿数据路径

WPF 的 `ProjectWorkbooksView` 通过 `IProjectWorkbookService` 调用 `/api/v2/project-workbooks`。后端由 `SqliteProjectWorkbookStore` 提供项目和工作簿读写；`file_storage.py` 提供统一文件目录、历史和同步变更 API。逻辑目录与路径以 SQLite 为准，新项目在 SQLite 中创建节拍表和资产表。

工作簿功能不读取、导入、归档或导出 `.xlsx` 文件。WPF/API 只处理 SQLite 中的节拍表和资产表，也没有来源关联、选图历史或评论解决功能。

图片、视频和普通文件都使用同一 file catalog 与 Rust 传输服务。Python API 保存逻辑路径、版本、软删除状态和变更事件；NAS 只保存不可变文件对象。重命名、移动、删除、恢复和 beat 改号只更新 SQLite，不通过 SMB 或项目 helper 操作 NAS 文件。

访客使用短期只读会话，复用员工的同一 WPF 工作区与导航；个人空间、同步和反馈等不适用入口按只读能力禁用。访客可读公共区、两个部门区和所有项目数据，不可读个人空间，也不能写入文件、工作簿或批注。旧的 visitor NAS reader/socket 已从源码路径移除。

Mac M4 媒体工人通过单独服务令牌领取文件版本任务，用传输票据读取原文件，通过 Rust 传输服务提交 640px WebP 缩略图。缩略图按 `file_id + revision_id + generator_version` 记录，WPF 以相同键缓存和读取；工人不挂载 SMB。

首次启用采用空的素材目录与文件目录；旧 NAS 素材、旧工作簿内容不迁移。本轮只更新源代码和文档，不清理或改写现有生产数据。

## 主要界面映射

- 工作簿页面：`desktop/MuYingDrive.ProjectWorkbooks/ProjectWorkbooksView.xaml`
- 讨论和图片评价：`desktop/MuYingDrive.ProjectWorkbooks/ReviewPanel.xaml`
- 工作簿视图模型：`desktop/MuYingDrive.ProjectWorkbooks/ProjectWorkbooksViewModel.cs`
- 主工作区与项目导航：`desktop/MuYingDrive.NativeWpf/ViewModels/WorkspaceViewModel.cs`
- 文件浏览：`desktop/MuYingDrive.NativeWpf/ViewModels/FilesViewModel.cs`

## 部署入口

```powershell
.\deploy.ps1 -Backend
.\deploy.ps1 -Client
.\deploy.ps1 -All
.\deploy.ps1 -Client -Installer
```
