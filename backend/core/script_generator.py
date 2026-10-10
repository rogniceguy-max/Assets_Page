# -*- coding: utf-8 -*-
"""
MuYing Hub - 客户端自动化挂载脚本生成器
严格安全保障：仅下发该员工本人注册凭据，绝不包含任何管理员账号密码
"""
from config import NAS_IP, NAS_HOSTNAME, SHARE_NAME, MOUNT_DRIVE, DRIVE_LABEL

def generate_windows_batch(linux_username: str, password: str, fullname: str = "") -> str:
    """
    生成针对该员工本人的 Windows 一键自动化挂载批处理脚本 (.bat)
    包含 CRLF (\r\n) 换行符以确保 Windows 命令提示符完美执行
    """
    safe_pass = password.replace("%", "%%").replace('"', '""')
    
    script_lines = [
        "@echo off",
        "chcp 65001 >nul",
        f"title {DRIVE_LABEL} 资产盘自动挂载工具",
        "color 0B",
        "",
        ":: ==================================================================",
        ":: 注意：本脚本仅包含当前员工专属认证凭据，严禁共享给他人使用",
        ":: ==================================================================",
        "",
        "echo ==================================================================",
        f"echo               欢迎使用 {DRIVE_LABEL} 资产盘自动配置工具",
        "echo ==================================================================",
        f"echo 员工姓名: {fullname or linux_username}",
        f"echo 目标服务器: \\\\{NAS_IP}\\{SHARE_NAME} (或 \\\\{NAS_HOSTNAME}\\{SHARE_NAME})",
        "echo. ",
        "echo [第 1 步/共 4 步] 正在清理旧网络连接与残留凭据...",
        f"net use {MOUNT_DRIVE} /delete /y >nul 2>&1",
        f"cmdkey /delete:{NAS_IP} >nul 2>&1",
        f"cmdkey /delete:{NAS_HOSTNAME} >nul 2>&1",
        "",
        "echo [第 2 步/共 4 步] 正在保存员工登录凭据至 Windows 凭据管理器...",
        f'cmdkey /add:{NAS_IP} /user:"{linux_username}" /pass:"{safe_pass}" >nul 2>&1',
        f'cmdkey /add:{NAS_HOSTNAME} /user:"{linux_username}" /pass:"{safe_pass}" >nul 2>&1',
        "",
        f"echo [第 3 步/共 4 步] 正在挂载网络驱动器 ({MOUNT_DRIVE} 盘)...",
        f'net use {MOUNT_DRIVE} "\\\\{NAS_IP}\\{SHARE_NAME}" "{safe_pass}" /user:"{linux_username}" /persistent:yes >nul 2>&1',
        "if %errorlevel% neq 0 (",
        f'    net use {MOUNT_DRIVE} "\\\\{NAS_HOSTNAME}\\{SHARE_NAME}" "{safe_pass}" /user:"{linux_username}" /persistent:yes >nul 2>&1',
        ")",
        "",
        f"if not exist {MOUNT_DRIVE}\\ (",
        "    echo.",
        "    echo [挂载失败] 请确认您的电脑已连接到局域网 WiFi / 网线，或联系技术支持。",
        "    echo.",
        "    pause",
        "    exit /b 1",
        ")",
        "",
        "echo [第 4 步/共 4 步] 正在生成桌面快捷方式并重命名驱动器...",
        "powershell -NoProfile -ExecutionPolicy Bypass -Command ^",
        f'    "$sh = New-Object -ComObject Shell.Application; try {{ $sh.NameSpace(\'{MOUNT_DRIVE}\\\').Self.Name = \'{DRIVE_LABEL}\' }} catch {{}}; " ^',
        '    "$wsh = New-Object -ComObject WScript.Shell; " ^',
        '    "$desk = [Environment]::GetFolderPath(\'Desktop\'); " ^',
        f'    "$lnk = $wsh.CreateShortcut(\\\"$desk\\{DRIVE_LABEL}.lnk\\\"); " ^',
        f'    "$lnk.TargetPath = \'{MOUNT_DRIVE}\\\'; " ^',
        f'    "$lnk.Description = \'{DRIVE_LABEL} 素材资源库快捷方式\'; " ^',
        '    "$lnk.Save();"',
        "",
        "echo.",
        "echo ==================================================================",
        f"echo  [√ 成功] 资产盘已成功挂载为本地 [{MOUNT_DRIVE} 盘]！",
        f"echo  [√ 成功] 您的桌面已生成 [{DRIVE_LABEL}] 快捷方式，双击即可进入。",
        "echo ==================================================================",
        "echo.",
        f"start explorer {MOUNT_DRIVE}\\",
        "echo 按任意键退出本配置窗口...",
        "pause >nul",
        "exit /b 0"
    ]
    return "\r\n".join(script_lines) + "\r\n"
