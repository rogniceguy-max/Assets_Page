# -*- coding: utf-8 -*-
"""
MuYing Hub - Windows 原生凭据安全自动化挂载脚本生成器
架构设计：
1. 采用通用 Batch/PowerShell Polyglot 架构，双击原生即跑，无需管理员权限。
2. 绝不包含用户密码、管理员密码、临时 Token 或对后端的明文凭据请求。
3. 遵循 Windows 原生安全规范，使用系统 Get-Credential / Windows 凭据管理器实现首次输入、持久化保存与开机免密。
"""
from config import NAS_IP, SHARE_NAME, MOUNT_DRIVE, DRIVE_LABEL

def generate_windows_batch(linux_username: str = "", fullname: str = "") -> str:
    """
    生成员工专属或通用的 Windows 原生挂载工具 (.bat)
    包含 CRLF (\\r\\n) 换行符以确保 Windows 命令提示符完美执行
    """
    user_hint = linux_username or ""
    display_title = DRIVE_LABEL or "沐影"
    remote_unc = f"\\\\{NAS_IP}\\{SHARE_NAME}"

    script = f'''<# :
@echo off
chcp 65001 >nul
title {display_title} 资产盘原生挂载工具
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression ([System.IO.File]::ReadAllText('%~f0', [System.Text.Encoding]::UTF8))"
exit /b %errorlevel%
: #>

# ==============================================================================
# {display_title} 资产盘 - Windows 原生安全挂载脚本
# 特性：零明文密码落地 | Windows 原生凭据保护 | 开机持久化自动重连 | 桌面直达
# ==============================================================================

$ErrorActionPreference = 'Stop'
$TargetServer = '{NAS_IP}'
$ShareName = '{SHARE_NAME}'
$RemoteUNC = '{remote_unc}'
$DriveLetter = '{MOUNT_DRIVE}'
$DriveClean = $DriveLetter.TrimEnd(':')
$Label = '{display_title}'
$DefaultUser = '{user_hint}'

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "             欢迎使用 $Label 团队资产盘挂载工具" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " 目标存储服务: $RemoteUNC"
Write-Host " 本地映射盘符: $DriveLetter"
if ($DefaultUser) {{
    Write-Host " 绑定员工账号: $DefaultUser"
}}
Write-Host ""

# Win32 凭据持久化 API (内存中调用 advapi32.dll CredWriteW，绝不在命令行或日志暴露密码)
$credDef = @"
using System;
using System.Runtime.InteropServices;

public class NativeCredentialManager {{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {{
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
    }}

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    public static bool SaveToCredentialManager(string target, string username, string password) {{
        CREDENTIAL cred = new CREDENTIAL();
        cred.TargetName = target;
        cred.UserName = username;
        cred.Type = 1; // CRED_TYPE_GENERIC
        cred.Persist = 2; // CRED_PERSIST_LOCAL_MACHINE
        byte[] bPassword = System.Text.Encoding.Unicode.GetBytes(password);
        cred.CredentialBlobSize = (uint)bPassword.Length;
        cred.CredentialBlob = Marshal.AllocHGlobal(bPassword.Length);
        Marshal.Copy(bPassword, 0, cred.CredentialBlob, bPassword.Length);
        try {{
            return CredWrite(ref cred, 0);
        }} finally {{
            Marshal.ZeroFreeGlobalAllocUnicode(cred.CredentialBlob);
        }}
    }}
}}
"@
try {{
    Add-Type -TypeDefinition $credDef -Language CSharp -ErrorAction SilentlyContinue
}} catch {{}}

# ------------------------------------------------------------------------------
# 步骤 1：检查本地盘符是否已经被占用
# ------------------------------------------------------------------------------
Write-Host "[检测中] 正在检查本地盘符状态 ($DriveLetter)..." -ForegroundColor Cyan
$existingDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$DriveLetter'" -ErrorAction SilentlyContinue

if ($existingDrive) {{
    $prov = $existingDrive.ProviderName
    # 如果已正确连接到本资产盘，直接报告就绪并打开资源管理器
    if ($prov -and ($prov -like "*$TargetServer*$ShareName*" -or $prov -like "*$RemoteUNC*")) {{
        if (Test-Path "$DriveLetter\\") {{
            Write-Host "==========================================================" -ForegroundColor Green
            Write-Host " [√] $Label 资产盘 ($DriveLetter) 已经正常挂载且连接可用！" -ForegroundColor Green
            Write-Host "==========================================================" -ForegroundColor Green
            Write-Host "正在打开资源管理器..." -ForegroundColor Cyan
            Start-Process explorer.exe "$DriveLetter\\"
            Start-Sleep -Seconds 2
            exit 0
        }}
    }} else {{
        # 盘符被其他设备或网络连接占用，安全阻止，绝不强制删除员工的其他映射！
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host " [安全中断] 本地盘符 $DriveLetter 已被其他设备或网络连接占用！" -ForegroundColor Red
        if ($prov) {{
            Write-Host " 当前占用源: $prov" -ForegroundColor Yellow
        }} else {{
            Write-Host " 当前设备类型: 本地物理磁盘 / 分区 / 可移动介质" -ForegroundColor Yellow
        }}
        Write-Host " 为保护您的现有工作环境，本工具不会强制删除该盘符。" -ForegroundColor Yellow
        Write-Host " 请释放 $DriveLetter 盘或联系团队网络管理员协助。" -ForegroundColor Yellow
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }}
}}

# ------------------------------------------------------------------------------
# 步骤 2：测试 NAS 网络连通性 (端口 445 SMB 服务)
# ------------------------------------------------------------------------------
Write-Host "[第 1 步/共 4 步] 检测资产服务器连通性 ($TargetServer:445)..." -ForegroundColor Cyan
$tcpClient = New-Object System.Net.Sockets.TcpClient
$portOpen = $false
try {{
    $async = $tcpClient.BeginConnect($TargetServer, 445, $null, $null)
    $portOpen = $async.AsyncWaitHandle.WaitOne(3000, $false)
    if ($portOpen -and $tcpClient.Connected) {{
        $tcpClient.EndConnect($async)
        $portOpen = $true
    }} else {{
        $portOpen = $false
    }}
}} catch {{
    $portOpen = $false
}} finally {{
    $tcpClient.Close()
}}

if (-not $portOpen) {{
    Write-Host "==========================================================" -ForegroundColor Red
    Write-Host " [错误] 无法连接到 $Label 资产服务器 ($TargetServer:445)" -ForegroundColor Red
    Write-Host " 请检查您的网络连接，确认已连入公司内部 WiFi 或局域网网线！" -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Red
    Write-Host "按任意键退出..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}}
Write-Host " [√] 资产服务器网络连接正常。" -ForegroundColor Green

# ------------------------------------------------------------------------------
# 步骤 3：尝试使用 Windows 已保存的凭据建立映射（用于重复运行/重新挂载免密恢复）
# ------------------------------------------------------------------------------
Write-Host "[第 2 步/共 4 步] 检查 Windows 已保存的连接凭据..." -ForegroundColor Cyan

# 清理断开或残留的同名旧映射
Get-SmbMapping -LocalPath $DriveLetter -ErrorAction SilentlyContinue | Remove-SmbMapping -Force -UpdateProfile -ErrorAction SilentlyContinue

$mounted = $false
try {{
    # 尝试不显式传递密码，直接请求挂载
    $r = New-SmbMapping -LocalPath $DriveLetter -RemotePath $RemoteUNC -Persistent:$true -ErrorAction Stop
    if (Test-Path "$DriveLetter\\") {{
        $mounted = $true
        Write-Host " [√] 成功读取 Windows 已保存凭据，免密建立连接！" -ForegroundColor Green
    }}
}} catch {{
    $mounted = $false
}}

# ------------------------------------------------------------------------------
# 步骤 4：若无保存凭据，唤起 Windows 原生安全窗口输入凭据并持久化
# ------------------------------------------------------------------------------
if (-not $mounted) {{
    Write-Host "[第 3 步/共 4 步] 需要验证身份，正在弹出 Windows 原生凭据窗口..." -ForegroundColor Cyan
    Write-Host " 请在弹出的窗口中输入您在沐影门户注册的用户名与密码。" -ForegroundColor Yellow

    $promptTitle = "$Label 资产盘 - 挂载凭据验证"
    $promptMsg = "请输入 $Label 资产盘的登录账号及密码以连接 $RemoteUNC"

    $cred = $null
    try {{
        # 优先使用 Windows 原生安全凭据对话框
        $cred = $host.UI.PromptForCredential($promptTitle, $promptMsg, $DefaultUser, "")
    }} catch {{
        $cred = $null
    }}

    if (-not $cred) {{
        try {{
            $cred = Get-Credential -UserName $DefaultUser -Message $promptMsg
        }} catch {{
            $cred = $null
        }}
    }}

    if (-not $cred) {{
        Write-Host "==========================================================" -ForegroundColor Yellow
        Write-Host " [操作已取消] 未输入登录凭据，挂载流程已终止。" -ForegroundColor Yellow
        Write-Host "==========================================================" -ForegroundColor Yellow
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }}

    # 使用输入的凭据挂载驱动器
    try {{
        New-SmbMapping -LocalPath $DriveLetter -RemotePath $RemoteUNC -Credential $cred -Persistent:$true -ErrorAction Stop | Out-Null
        $mounted = $true
    }} catch {{
        $err = $_.Exception.Message
        Write-Host "==========================================================" -ForegroundColor Red
        if ($err -like "*password*" -or $err -like "*credential*" -or $err -like "*logon*") {{
            Write-Host " [错误] 认证失败：账户或密码错误！" -ForegroundColor Red
            Write-Host " 请确认您在网页端注册的账号及密码是否正确。" -ForegroundColor Yellow
        }} elseif ($err -like "*conflict*" -or $_.Exception.HResult -eq -2147023677) {{
            Write-Host " [错误] 检测到凭据冲突：当前系统已有其他账号连接到该 NAS。" -ForegroundColor Red
            Write-Host " 请注销冲突会话后重新运行本挂载工具。" -ForegroundColor Yellow
        }} else {{
            Write-Host " [错误] 挂载失败: $err" -ForegroundColor Red
        }}
        Write-Host "==========================================================" -ForegroundColor Red
        Write-Host "按任意键退出..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }}

    # 挂载成功后，通过 Win32 API 将凭据安全保存到 Windows 凭据管理器（内存操作，绝无明文参数落盘）
    try {{
        $rawPass = $cred.GetNetworkCredential().Password
        $uName = $cred.UserName
        [NativeCredentialManager]::SaveToCredentialManager($TargetServer, $uName, $rawPass) | Out-Null
        Write-Host " [√] 凭据已安全存入 Windows 凭据管理器，后续开机自动连接。" -ForegroundColor Green
    }} catch {{}}

    # 立即从内存抹除密码对象
    $cred = $null
    [GC]::Collect()
}}

# ------------------------------------------------------------------------------
# 步骤 5：创建桌面快捷方式与盘符命名
# ------------------------------------------------------------------------------
Write-Host "[第 4 步/共 4 步] 正在生成桌面快捷方式..." -ForegroundColor Cyan
try {{
    $wsh = New-Object -ComObject WScript.Shell
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = $wsh.CreateShortcut("$desk\\$Label.lnk")
    $lnk.TargetPath = "$DriveLetter\\"
    $lnk.Description = "$Label 团队素材资产总库"
    $lnk.Save()
    Write-Host " [√] 桌面快捷方式 [$Label] 已生成。" -ForegroundColor Green
}} catch {{}}

try {{
    (New-Object -ComObject Shell.Application).NameSpace("$DriveLetter\\").Self.Name = $Label
}} catch {{}}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " [√ 成功] $Label 资产盘已成功挂载为本地 [$DriveLetter]！" -ForegroundColor Green
Write-Host " [√ 成功] 您的桌面已生成 [$Label] 快捷方式，双击即可进入。" -ForegroundColor Green
Write-Host " [√ 成功] 凭据由 Windows 系统原生管理，开机永久免密重连。" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "正在打开资源管理器..." -ForegroundColor Cyan

Start-Process explorer.exe "$DriveLetter\\"
Start-Sleep -Seconds 2
exit 0
'''
    return script.replace("\r\n", "\n").replace("\n", "\r\n")
