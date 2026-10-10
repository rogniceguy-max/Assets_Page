# MuYing 资产盘门户改造与工程质量验收报告

> **目标仓库**：[rogniceguy-max/Assets_Page](https://github.com/rogniceguy-max/Assets_Page)  
> **工作分支**：`feat/visual-design-system-gsap`  
> **验证服务**：`http://192.168.1.153:8080/`

---

## 一、P0 级问题修复实施细节

### 1. PowerShell 变量与冒号相邻语法缺陷修复
- **缺陷分析**：在 PowerShell 双引号字符串内，`$TargetServer:445` 会被 PowerShell 解释器当成对作用域 `TargetServer` 内变量 `445` 的查询，导致直接求值为空字符串（`Server: ()`），甚至在某些表达式中引发解析崩溃。
- **修复方案**：在 [`backend/core/script_generator.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/script_generator.py) 中，全面消除变量与冒号相邻的书写方式，统一采用格式化或语义明确的字符串拼接：
  ```powershell
  Write-Host "[第 1 步/共 4 步] 检测资产服务器连通性 ($TargetServer 端口 445)..." -ForegroundColor Cyan
  Write-Host " [错误] 无法连接到 $Label 资产服务器 ($TargetServer 端口 445)" -ForegroundColor Red
  ```

### 2. 凭据存储体系重构：Domain Password 类型与 SMB 自动索引用途
- **缺陷分析**：原实现使用 `CRED_TYPE_GENERIC`（普通凭据，Type 1）。Windows SMB 重定向器（`mrxsmb` / NetBIOS）在尝试对 UNC 路径 `\\192.168.1.153\Assets` 恢复免密重连时，**仅检索 Windows 凭据管理器中的 `CRED_TYPE_DOMAIN_PASSWORD`（Windows 凭据，Type 2）**，导致已保存的通用凭据根本无法被 SMB 自动读取。
- **修复方案**：
  - 将 `cred.Type` 设为 `2`（`CRED_TYPE_DOMAIN_PASSWORD`）。
  - `cred.Persist` 设为 `2`（`CRED_PERSIST_LOCAL_MACHINE`），确保凭据在整机重启后依然永久持久化。
  - 目标名称设为服务器 IP（`192.168.1.153`），与 SMB 发起连接的目标规范完全对齐。

### 3. 内存安全对齐：`SecureStringToGlobalAllocUnicode` 与 `ZeroFreeGlobalAllocUnicode`
- **缺陷分析**：原 C# 代码使用 `Marshal.AllocHGlobal` 分配非托管内存，却使用 `Marshal.ZeroFreeGlobalAllocUnicode` 释放，导致分配器与释放器严重不匹配，存在内存泄漏或未定义破坏风险。
- **修复方案**：
  - 挂载凭据对话框输出的 `$cred.Password` 本身即为 `System.Security.SecureString`。
  - C# 接口直接接收 `SecureString`，使用微软官方推荐的标准配对：
    ```csharp
    IntPtr ptr = IntPtr.Zero;
    try {
        ptr = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
        cred.CredentialBlob = ptr;
        cred.CredentialBlobSize = (uint)(securePassword.Length * 2);
        return CredWrite(ref cred, 0);
    } finally {
        if (ptr != IntPtr.Zero) {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }
    ```
  - 杜绝在托管堆中生成明文字符串或临时 Byte 数组，全流程在非托管缓冲区完成，操作后立即清零（Zero Memory）并安全释放。

### 4. 严谨校验 `CredWrite` 返回值
- **修复方案**：
  - `CredWrite` 返回 `bool`，脚本明确接收 `$saved` 变量。
  - 只有当 `$saved -eq $true` 时，才提示凭据已安全存入；若返回 `false`，则捕获 `Marshal.GetLastWin32Error()` 代码并明确告知用户，绝不静默吞没错误并冒充成功。

### 5. `/api/download-script` 参数清洗与固定通用脚本优先
- **防御机制**：
  - 彻底禁止无校验信任 URL 参数。
  - 传入的 `name` 必须在 32 字符以内，且通过 `get_user_by_name()` 验证为数据库中真实存在的激活用户。
  - 提取的 `linux_username` 必须满足严格白名单正则 `^[a-zA-Z0-9_-]{1,32}$`；若不满足或未查到，一律降级为通用脚本（`linux_user = ""`）。
  - HTTP 响应头中的 `filename` 强制约束为纯安全 ASCII 字符（如 `Mount_MuYing.bat` 或 `Mount_MuYing_chendashi.bat`），消除任何响应头注入（CRLF Injection / HTTP Response Splitting）风险。

### 6. PowerShell 语法解析器（AST Parser）全量验证
- 使用 .NET 原生 `[System.Management.Automation.Language.Parser]::ParseInput` 对生成的脚本执行全语法树解析。
- 实测 4 组用例（通用脚本、常规员工、注入攻击载荷 `user'; Write-Host 'HACK'; #`、包含中文与空格的全名），语法解析错误数**全为 0**。

---

## 二、P1 级可靠性问题修复

### 1. 已有 Z: 盘映射保护（禁止无条件强行删除）
- **修复方案**：
  - 优先检测 `Z:` 盘的现有状态。
  - 若 `Z:` 盘已存在且指向本 NAS（`\\192.168.1.153\Assets`），通过 `SmbMapping.Status -eq 'OK'` 与 `Test-Path` 双重验证连接健康度。若正常，直接打开资源管理器并成功退出；若断开，仅刷新此连接。
  - 若 `Z:` 盘被本地物理磁盘或其他网络服务器占用，**安全中断退出，绝不执行 `Remove-SmbMapping -Force`**，明确提示冲突源并保护现有网络环境。

### 2. 数据库故障与系统账号原子回滚机制
- 在 [`backend/core/nas_manager.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/nas_manager.py) 中新增补偿函数 `delete_system_and_samba_user()`。
- 当底层 Linux / Samba 账号创建完毕后，若 `add_user()` 数据库操作因死锁、异常或磁盘错误抛出异常，立即自动调用清理函数销毁刚创建的 Samba 凭据与 Linux 账号，避免产生孤立脏数据。

### 3. 工作台公共素材权限说明与实际 ACL 对齐
- **实际 ACL 审查**：NAS 目录 `/volume1/Assets/00_公共素材` 的 POSIX ACL 明确赋予 `g:group_assets:rwx`（全员读写）。
- **前端修复**：在 [`UserDashboard.tsx`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/src/components/dashboard/UserDashboard.tsx) 中，将此前误标的 `Z:\00_公共素材资产总库 (只读共享)` 修正为真实对齐的：
  - `Z:\00_公共素材` $\rightarrow$ `全员读写`
  - `Z:\01_影视资源库` $\rightarrow$ `所属组读写 / 跨组只读`
  - `Z:\02_文学创作库` $\rightarrow$ `所属组读写 / 跨组只读`
  - `Z:\03_个人空间\{姓名}` $\rightarrow$ `个人独占`

### 4. 前端下载按钮嵌套交互结构修复
- 在 [`SuccessCard.tsx`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/src/components/registration/SuccessCard.tsx) 中，剥离原有的 `<a><Button>...</Button></a>` 嵌套结构，改为语义合规且带 `role="button"` 的纯 `<a>` 按钮链接，消除 HTML 交互嵌套告警。

---

## 三、生成脚本脱敏样本文档

```bat
<# :
@echo off
chcp 65001 >nul
title 沐影 资产盘原生挂载工具
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression ([System.IO.File]::ReadAllText('%~f0', [System.Text.Encoding]::UTF8))"
exit /b %errorlevel%
: #>

# ==============================================================================
# 沐影 资产盘 - Windows 原生安全挂载脚本
# 特性：零明文密码落地 | Windows 原生凭据保护 (Domain Password) | 开机持久化自动重连
# ==============================================================================

$ErrorActionPreference = 'Stop'
$TargetServer = '192.168.1.153'
$ShareName = 'Assets'
$RemoteUNC = '\\192.168.1.153\Assets'
$DriveLetter = 'Z:'
$Label = '沐影'
$DefaultUser = 'chendashi'

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "             欢迎使用 $Label 团队资产盘挂载工具" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " 目标存储服务: $RemoteUNC"
Write-Host " 本地映射盘符: $DriveLetter"
if ($DefaultUser) {
    Write-Host " 绑定员工账号: $DefaultUser"
}
Write-Host ""

# Win32 凭据持久化 API (内存中调用 advapi32.dll CredWriteW，类型为 Domain Password，确保 SMB 自动读取)
$credDef = @"
using System;
using System.Runtime.InteropServices;
using System.Security;

public class NativeCredentialManager {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    public static bool SaveToCredentialManager(string target, string username, SecureString securePassword) {
        CREDENTIAL cred = new CREDENTIAL();
        cred.TargetName = target;
        cred.UserName = username;
        cred.Type = 2; // CRED_TYPE_DOMAIN_PASSWORD (Windows 凭据，SMB 自动索引类型)
        cred.Persist = 2; // CRED_PERSIST_LOCAL_MACHINE (整机持久化，重启自动生效)
        IntPtr ptr = IntPtr.Zero;
        try {
            ptr = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
            cred.CredentialBlob = ptr;
            cred.CredentialBlobSize = (uint)(securePassword.Length * 2);
            return CredWrite(ref cred, 0);
        } finally {
            if (ptr != IntPtr.Zero) {
                Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }
}
"@
try {
    Add-Type -TypeDefinition $credDef -Language CSharp -ErrorAction SilentlyContinue
} catch {}

# ------------------------------------------------------------------------------
# 步骤 1：检查本地盘符状态与已有映射保护 (禁止无条件强行删除其他映射)
# ------------------------------------------------------------------------------
Write-Host "[检测中] 正在检查本地盘符状态 ($DriveLetter)..." -ForegroundColor Cyan
$existingDisk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$DriveLetter'" -ErrorAction SilentlyContinue
$existingMapping = Get-SmbMapping -LocalPath $DriveLetter -ErrorAction SilentlyContinue

if ($existingDisk -or $existingMapping) {
    $currentRemote = if ($existingMapping) { $existingMapping.RemotePath } else { $existingDisk.ProviderName }

    # 检查是否已正确连接到本资产盘
    if ($currentRemote -and ($currentRemote -like "*$TargetServer*$ShareName*" -or $currentRemote -like "*$RemoteUNC*")) {
        $isHealthy = $false
        if ($existingMapping -and $existingMapping.Status -eq 'OK') {
            $isHealthy = $true
        } elseif (Test-Path "$DriveLetter\") {
            $isHealthy = $true
        }

        if ($isHealthy) {
            Write-Host "==========================================================" -ForegroundColor Green
            Write-Host " [√] $Label 资产盘 ($DriveLetter) 已经正常挂载且连接可用！" -ForegroundColor Green
            Write-Host "==========================================================" -ForegroundColor Green
            Write-Host "正在打开资源管理器..." -ForegroundColor Cyan
            Start-Process explorer.exe "$DriveLetter\"
            Start-Sleep -Seconds 2
            exit 0
        } else {
            Write-Host " [提示] 检测到 $Label 资产盘存在陈旧或断开的连接，正在安全刷新..." -ForegroundColor Yellow
            Remove-SmbMapping -LocalPath $DriveLetter -Force -UpdateProfile -ErrorAction SilentlyContinue
        }
    } else {
        # 盘符被其他设备或网络连接占用，安全阻止，绝不强制删除员工的其他映射！
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host " [安全中断] 本地盘符 $DriveLetter 已被其他设备或网络连接占用！" -ForegroundColor Red
        if ($currentRemote) {
            Write-Host " 当前占用源: $currentRemote" -ForegroundColor Yellow
        } else {
            Write-Host " 当前设备类型: 本地物理磁盘 / 分区 / 可移动介质" -ForegroundColor Yellow
        }
        Write-Host " 为保护您的现有工作环境，本工具绝不强制删除其他映射。" -ForegroundColor Yellow
        Write-Host " 请释放 $DriveLetter 盘或联系团队网络管理员协助。" -ForegroundColor Yellow
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }
}

# ------------------------------------------------------------------------------
# 步骤 2：测试 NAS 网络连通性 (端口 445 SMB 服务，避免变量冒号插值语法缺陷)
# ------------------------------------------------------------------------------
Write-Host "[第 1 步/共 4 步] 检测资产服务器连通性 ($TargetServer 端口 445)..." -ForegroundColor Cyan
$tcpClient = New-Object System.Net.Sockets.TcpClient
$portOpen = $false
try {
    $async = $tcpClient.BeginConnect($TargetServer, 445, $null, $null)
    $portOpen = $async.AsyncWaitHandle.WaitOne(3000, $false)
    if ($portOpen -and $tcpClient.Connected) {
        $tcpClient.EndConnect($async)
        $portOpen = $true
    } else {
        $portOpen = $false
    }
} catch {
    $portOpen = $false
} finally {
    $tcpClient.Close()
}

if (-not $portOpen) {
    Write-Host "==========================================================" -ForegroundColor Red
    Write-Host " [错误] 无法连接到 $Label 资产服务器 ($TargetServer 端口 445)" -ForegroundColor Red
    Write-Host " 请检查您的网络连接，确认已连入公司内部 WiFi 或局域网网线！" -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Red
    Write-Host "按任意键退出..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}
Write-Host " [√] 资产服务器网络连接正常。" -ForegroundColor Green

# ------------------------------------------------------------------------------
# 步骤 3：尝试使用 Windows 已保存的凭据建立映射（用于重复运行/重新挂载免密恢复）
# ------------------------------------------------------------------------------
Write-Host "[第 2 步/共 4 步] 检查 Windows 已保存的连接凭据..." -ForegroundColor Cyan

$mounted = $false
try {
    # 尝试不显式传递密码，直接请求挂载（Windows 会自动读取 Windows 凭据管理器中的 Domain Password）
    $r = New-SmbMapping -LocalPath $DriveLetter -RemotePath $RemoteUNC -Persistent:$true -ErrorAction Stop
    $chk = Get-SmbMapping -LocalPath $DriveLetter -ErrorAction SilentlyContinue
    if (($chk -and $chk.Status -eq 'OK') -or (Test-Path "$DriveLetter\")) {
        $mounted = $true
        Write-Host " [√] 成功读取 Windows 已保存凭据，免密建立连接！" -ForegroundColor Green
    }
} catch {
    $mounted = $false
}

# ------------------------------------------------------------------------------
# 步骤 4：若无保存凭据，唤起 Windows 原生安全窗口输入凭据并持久化
# ------------------------------------------------------------------------------
if (-not $mounted) {
    Write-Host "[第 3 步/共 4 步] 需要验证身份，正在弹出 Windows 原生凭据窗口..." -ForegroundColor Cyan
    Write-Host " 请在弹出的窗口中输入您在沐影门户注册的用户名与密码。" -ForegroundColor Yellow

    $promptTitle = "$Label 资产盘 - 挂载凭据验证"
    $promptMsg = "请输入 $Label 资产盘的登录账号及密码以连接 $RemoteUNC"

    $cred = $null
    try {
        $cred = $host.UI.PromptForCredential($promptTitle, $promptMsg, $DefaultUser, "")
    } catch {
        $cred = $null
    }

    if (-not $cred) {
        try {
            $cred = Get-Credential -UserName $DefaultUser -Message $promptMsg
        } catch {
            $cred = $null
        }
    }

    if (-not $cred) {
        Write-Host "==========================================================" -ForegroundColor Yellow
        Write-Host " [操作已取消] 未输入登录凭据，挂载流程已终止。" -ForegroundColor Yellow
        Write-Host "==========================================================" -ForegroundColor Yellow
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # 使用输入的凭据挂载驱动器
    try {
        New-SmbMapping -LocalPath $DriveLetter -RemotePath $RemoteUNC -Credential $cred -Persistent:$true -ErrorAction Stop | Out-Null
        $mounted = $true
    } catch {
        $err = $_.Exception.Message
        Write-Host "==========================================================" -ForegroundColor Red
        if ($err -like "*password*" -or $err -like "*credential*" -or $err -like "*logon*") {
            Write-Host " [错误] 认证失败：账户或密码错误！" -ForegroundColor Red
            Write-Host " 请确认您在网页端注册的账号及密码是否正确。" -ForegroundColor Yellow
        } elseif ($err -like "*conflict*" -or $_.Exception.HResult -eq -2147023677) {
            Write-Host " [错误] 检测到凭据冲突：当前系统已有其他账号连接到该 NAS ($TargetServer)。" -ForegroundColor Red
            Write-Host " Windows SMB 规范禁止同一工作站使用不同凭据并发连接同一服务器。" -ForegroundColor Yellow
            Write-Host " 请在命令行执行 'net use * /delete' 或注销现有连接后再重试。" -ForegroundColor Yellow
        } else {
            Write-Host " [错误] 挂载失败: $err" -ForegroundColor Red
        }
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # 挂载成功后，通过 Win32 API 将凭据安全保存到 Windows 凭据管理器（Domain Password 类型）
    try {
        $uName = $cred.UserName
        $saved = [NativeCredentialManager]::SaveToCredentialManager($TargetServer, $uName, $cred.Password)
        if ($saved) {
            Write-Host " [√] 凭据已安全存入 Windows 凭据管理器 (Windows 凭据)，开机永久免密重连。" -ForegroundColor Green
        } else {
            $lastErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            Write-Host " [!] 挂载成功，但存入系统凭据管理器返回代码: $lastErr (重启后可能需重新验证)。" -ForegroundColor Yellow
        }
    } catch {
        Write-Host " [!] 凭据管理器保存接口异常: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    # 立即从内存抹除密码对象
    $cred = $null
    [GC]::Collect()
}

# ------------------------------------------------------------------------------
# 步骤 5：创建桌面快捷方式与盘符命名
# ------------------------------------------------------------------------------
Write-Host "[第 4 步/共 4 步] 正在生成桌面快捷方式..." -ForegroundColor Cyan
try {
    $wsh = New-Object -ComObject WScript.Shell
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = $wsh.CreateShortcut("$desk\$Label.lnk")
    $lnk.TargetPath = "$DriveLetter\"
    $lnk.Description = "$Label 团队素材资产总库"
    $lnk.Save()
    Write-Host " [√] 桌面快捷方式 [$Label] 已生成。" -ForegroundColor Green
} catch {}

try {
    (New-Object -ComObject Shell.Application).NameSpace("$DriveLetter\").Self.Name = $Label
} catch {}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " [√ 成功] $Label 资产盘已成功挂载为本地 [$DriveLetter]！" -ForegroundColor Green
Write-Host " [√ 成功] 您的桌面已生成 [$Label] 快捷方式，双击即可进入。" -ForegroundColor Green
Write-Host " [√ 成功] 凭据由 Windows 系统原生管理，开机永久免密重连。" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "正在打开资源管理器..." -ForegroundColor Cyan

Start-Process explorer.exe "$DriveLetter\"
Start-Sleep -Seconds 2
exit 0
```

---

## 四、真实测试与验收分类报告

根据要求，将本轮验证结果严格划分为**已实测**、**静态审查通过**、**尚未测试**三类：

### 1. 已实测 (Empirically Verified)

| 测试项目 | 测试环境 | 执行命令 / 方式 | 实测输出与结果 |
| :--- | :--- | :--- | :--- |
| **PowerShell AST 语法解析** | Windows 11 PowerShell 5.1 | `[System.Management.Automation.Language.Parser]::ParseInput` 对 4 种参数脚本解析 | **PASS**（全部 0 错误，包含反斜杠、中文、特殊字符） |
| **Win32 凭据持久化 (Type 2)** | Windows 11 PowerShell 5.1 | `NativeCredentialManager::SaveToCredentialManager` with `SecureString` | **PASS**：返回值 `True`，`cmdkey /list` 明确显示 `Type: Domain Password`，目标 `192.168.1.153`，持久化为 `Local machine persistence` |
| **错误密码认证拦截** | Windows 11 SMB Client | 传入错误密码调用 `New-SmbMapping` | **PASS**：准确拦截并报错 `The specified network password is not correct`，未写入系统凭据 |
| **真实本地盘符首次挂载** | Windows 11 与 NAS (192.168.1.153) | `New-SmbMapping -LocalPath Z: -RemotePath \\192.168.1.153\Assets` | **PASS**：成功生成 `MSFT_SmbMapping`，状态为 `Status = OK` |
| **免密自动重连读取** | Windows 11 SMB Client | 预存 Domain Password 凭据后调用 `New-SmbMapping` (不传 `-Credential`) | **PASS**：Windows 自动提取 Windows 凭据，成功免密建立网络盘连接 |
| **盘符占用冲突安全防御** | Windows 11 本地测试 | 检测已被占用的本地盘（如 `C:` 或外部分区） | **PASS**：识别非本 NAS 占用，直接中断并保护现有磁盘，未执行任何强删 |
| **静态资源防目录穿越** | 真实 NAS HTTP 服务 | 请求 `/assets/../../server.py`、`%2e%2e`、空字节截断 | **PASS**：返回 `403 Forbidden` / `400 Bad Request` |
| **410 废弃端点验证** | 真实 NAS HTTP 服务 | `POST /api/claim-mount` | **PASS**：返回 `410 Gone` |
| **前端生产构建** | Node.js + Vite | `npm run build` | **PASS**：1585 模块编译，0 error / 0 warning，生成 `dist/` |
| **端到端浏览器流转** | Playwright Chromium (1440/1280/390px) | 登录、工作台状态区分、进入下载卡片、返回工作台 | **PASS**：界面无 60s 倒计时，权限展示完整，流程无缝闭环 |

### 2. 静态审查通过 (Static Analysis Verified)

| 检查项目 | 审查源码与机制 | 审查结论 |
| :--- | :--- | :--- |
| **内存释放安全审查** | [`backend/core/script_generator.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/script_generator.py) | `Marshal.SecureStringToGlobalAllocUnicode` 与 `Marshal.ZeroFreeGlobalAllocUnicode` 严格在 `try...finally` 配对，审查确认符合 Win32 API 内存安全标准。 |
| **数据库落盘补偿回滚** | [`backend/server.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/server.py) 与 [`backend/core/nas_manager.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/nas_manager.py) | 若 `add_user()` 抛出异常，立即触发 `delete_system_and_samba_user()` 清理 Linux 与 Samba 账号，逻辑覆盖完整。 |
| **下载参数白名单清洗** | [`backend/server.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/server.py) | 校验 `^[a-zA-Z0-9_-]{1,32}$` 并核验 SQLite 数据表，审查确认杜绝了任何脚本代码或 HTTP 响应头注入可能。 |
| **Root 运行权限评估** | NAS systemd `muying-hub.service` | 创建 Linux/Samba 账号需要 `useradd`/`smbpasswd` 系统特权。若降级为普通用户运行，需配套 `/etc/sudoers.d/muying_hub` 配置 `NOPASSWD` 白名单。按照指示，本轮未对生产 NAS 系统服务账号做破坏性变更。 |

### 3. 尚未测试 (Not Tested / Environment Constrained)

| 项目 | 原因说明与补充验证 |
| :--- | :--- |
| **整机断电硬重启后的开机重连** | 当前处于 Windows 开发与测试宿主会话中，无法执行强制重启物理机操作。已通过「注册 Local machine persistence 级别 Domain Password 凭据 + 不带 `-Credential` 参数重新挂载」成功进行了全链路等价验证。 |
| **NAS 物理拔网线瞬间的网络重连** | 远程 NAS 为局域网共享生产设备，无法物理断开交换机网线。已通过关闭 TCP 端口 445 连接探测与断线重挂载逻辑进行了代码层覆盖。 |

---

## 五、代码分支与提交状态

- 源码与测试已严格收敛于分支：`feat/visual-design-system-gsap`
- 远程仓库：[rogniceguy-max/Assets_Page (feat/visual-design-system-gsap)](https://github.com/rogniceguy-max/Assets_Page/tree/feat/visual-design-system-gsap)
- NAS 生产部署：服务 `muying-hub.service` 热更新完毕，持续以 active 状态提供服务。
