# 沐影盘 (MuYing Drive) 代码架构指引

> 本文档为仓库核心导航，明确标出真实前后端入口，避免历史遗留与跨模块误导。

---

## 一、核心工程布局

```text
Assets_Page/
├── backend/
│   └── v2/                         # 【唯一生产后端】Python 3.10+ / HTTPS / NAS 原生系统
│       ├── server.py               # 后端 HTTPS 入口与主路由器
│       ├── project_workbooks/      # 节拍表、资产表、批注、制作难度核心服务
│       │   ├── projector.py        # 工作簿投影与表头动态分类（无硬编码）
│       │   ├── projects_api.py     # 工作簿 RESTful 接口
│       │   ├── nas_adapter.py      # NAS 本地文件与模板读写适配器
│       │   └── workflow_services.py# 批注与审阅服务
│       ├── project_spaces.py       # 空间与媒体文件递归检索过滤（图片/视频）
│       ├── project_helper.py       # 项目模板与工作区辅助类
│       ├── database.py             # SQLite 凭据与审计数据库
│       ├── deploy/                 # systemd 守护进程单元与配置模板
│       └── tests/                  # 后端单元测试集 (128+ tests)
│
├── desktop/                        # 【生产 Windows 客户端】C# / .NET 8 / WPF 原生
│   ├── MuYingDrive.NativeWpf/      # 客户端主程序（外壳、导航、文件树、更新检查、登录）
│   │   ├── Views/WorkspaceView.xaml# 主工作区与左侧目录树视图
│   │   ├── Views/FilesView.xaml    # 资源库文件浏览器视图
│   │   └── ViewModels/             # 主视图模型与业务协调器
│   ├── MuYingDrive.ProjectWorkbooks/ # 【内嵌工作簿引擎】（节拍表/资产表/批注/难度）
│   │   ├── ProjectWorkbooksView.xaml # 工作簿主界面与主页卡片
│   │   ├── ReviewPanel.xaml        # 批注侧边栏
│   │   └── ViewModels/             # 工作簿数据绑定与单元格映射
│   ├── MuYingDrive.UpdateCore/     # 离线 ECDSA 数字签名校验与更新信任锚点
│   ├── MuYingDrive.Updater/        # 独立安全替换更新进程（单文件无损热更）
│   └── MuYingDrive.Bootstrap/      # 首次安装引导打包器（Setup.exe）
│
├── scripts/
│   └── deploy.py                   # 一键自动化极速部署工具（方案 3 & 4）
├── deploy.ps1                      # 极速部署快捷包装脚本
├── services/media_worker/          # Mac mini M4 媒体工作站处理服务
└── docs/                           # 需求与技术规范文档
```

---

## 二、关键业务模块映射

### 1. 节拍表 / 资产表与工作簿
- **前端页面**：`desktop/MuYingDrive.ProjectWorkbooks/ProjectWorkbooksView.xaml`
- **批注侧边栏**：`desktop/MuYingDrive.ProjectWorkbooks/ReviewPanel.xaml`
- **数据结构与视图模型**：`desktop/MuYingDrive.ProjectWorkbooks/ProjectWorkbooksViewModel.cs`
- **后端动态分类（按表头内容，不靠写死文件名）**：`backend/v2/project_workbooks/projector.py`
- **后端模板与文件持久化**：`backend/v2/project_workbooks/nas_adapter.py`

### 2. 左侧导航树与文件浏览
- **项目节点导航分发**：`desktop/MuYingDrive.NativeWpf/ViewModels/WorkspaceViewModel.cs`
- **媒体过滤（仅图片 / 仅视频）**：`desktop/MuYingDrive.NativeWpf/Services/SmbSpaceResolver.cs`
- **文件列表与内嵌双击打开工作簿**：`desktop/MuYingDrive.NativeWpf/ViewModels/FilesViewModel.cs`

---

## 三、部署与更新常用命令

已集成 30 秒自动化极速流水线：

```powershell
# 仅更新后端（秒级热更生效）：
.\deploy.ps1 -Backend

# 日常客户端更新（~30秒全自动打包、签名、直传 NAS）：
.\deploy.ps1 -Client

# 全量发布（前后端）：
.\deploy.ps1 -All

# 制作全新机器安装包：
.\deploy.ps1 -Client -Installer
```
