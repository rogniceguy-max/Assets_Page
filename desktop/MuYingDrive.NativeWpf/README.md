# 沐影盘原生 Windows 客户端

这是使用 C#、WPF 和 XAML 的桌面客户端。旧 WebView2/React 原型已从当前工作树移除。

## 文件和传输

WPF 通过 HTTPS API 操作 SQLite 中的逻辑文件、目录、软删除和版本历史。大文件及批量小文件通过随客户端发布的 Rust transfer agent 传输；WPF 通过当前 Windows 用户专属的命名管道提交任务并显示进度。客户端不保存 NAS 账号，也不通过 SMB 直接读取、写入或删除 NAS 文件。

同步目录由 agent 监听。agent 把稳定后的本地变更写入本机 SQLite 队列，再通过服务端变更 feed 协同；WPF 关闭后 agent 可继续运行，退出登录会清除内存令牌并停止该用户同步监听。

## 配置和开发

`nas-client.json` 是保留的配置文件名，只包含 API HTTPS 地址、证书 SHA-256 指纹和 Windows Credential Manager 中应用会话令牌的目标名，不包含共享路径或 NAS 用户凭据。它已加入 Git 忽略规则。也可用 `MUYING_V2_API_BASE_URI` 和 `MUYING_V2_CERT_SHA256` 提供地址与证书指纹。

正式构建还需要 Rust 1.85.0、`x86_64-pc-windows-msvc` target 和可用的 MSVC linker。仓库中的 `transfer/rust-toolchain.toml` 固定 Rust 版本；构建 WPF 时 MSBuild 会构建 Rust agent，并把它复制到 `transfer/MuYingDrive.TransferAgent.exe`。开发者需要安装 Visual Studio C++ Build Tools。没有 Rust 工具链时，可用 `-p:BuildTransferAgent=false` 仅检查 C# WPF 项目；这种输出不包含 agent，不能用于完整运行。

```powershell
dotnet build desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj -c Release
dotnet run --project desktop/MuYingDrive.NativeWpf/MuYingDrive.NativeWpf.csproj -c Release
```

## 其他本地文件

Updater 只下载和验证应用发布包；临时预览文件、登录令牌、客户端 ID、主题设置和 agent 本地队列均写入用户本机数据目录。它们不表示用户素材通过 SMB 访问。
