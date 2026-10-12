# 沐影盘原生 Windows 客户端（第一阶段）

本项目是纯 WPF + XAML 界面，使用 C# MVVM。旧 WebView2/React 原型保留在 `desktop/MuYingDrive.Wpf/` 和 `frontend/`，便于比较与回退。

## 运行

在仓库根目录执行：

```powershell
dotnet run --project desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj -c Release
```

### 本地 NAS 配置

仓库不包含实际 NAS 地址。首次连接前，将 `nas-client.example.json` 复制为本地 `nas-client.json`，再填写 NAS HTTPS 地址、证书 SHA-256 指纹和 SMB 共享根路径。`nas-client.json` 已加入 Git 忽略规则，不会随代码提交。也可以通过 `MUYING_V2_API_BASE_URI`、`MUYING_V2_CERT_SHA256` 和 `MUYING_NAS_SHARE_ROOT` 环境变量提供配置。

第一次打开时可在注册页创建本机演示账号。演示账号和登录状态保存在 `%LOCALAPPDATA%\MuYingDrive.NativeWpf\demo-accounts.json`；只保存 PBKDF2 密码摘要，不保存明文密码。退出账号后会回到登录页；正常关闭应用会保留登录状态。

## 第一阶段范围

- 原生登录/注册页面和可隐藏导航。
- Justified Gallery 卡片布局、列表切换、异步图片与 Windows Shell 视频缩略图。
- 单击、Ctrl/Shift 多选、Enter/双击打开、右键菜单、Ctrl+A/Escape/删除键行为。
- 传输中心提供清晰标注的演示任务，暂停/继续/重试只改变演示任务状态。
- 数据目录、上传队列和下载队列都不会连接或写入 NAS。上传只打开本机单文件选择框并生成演示任务；下载也只生成演示任务。

Gallery 载入 2,000 条清晰标记的演示文件记录。卡片使用 `Assets/Demo/` 的五张图片和一个由图片生成的短 MP4，重复素材只用于验证虚拟化、重排和滚动。视频缩略图由 Windows Shell 的 `IShellItemImageFactory` 请求；当前 Windows 若没有对应编解码器，会显示空白占位而不会调用外部服务。

## 运行依赖

程序只引用 .NET 8 WPF 桌面框架和 Windows Shell API，没有 WebView2、React、Node.js、Python 前端服务或 NuGet 前端运行时依赖。正式接入 NAS/SMB 会在本阶段验收后单独实现。
