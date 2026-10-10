# -*- coding: utf-8 -*-
"""
MuYing Hub - 客户端自动化挂载脚本生成器
安全规格：脚本文件绝不存储明文密码，通过 60 秒一次性安全令牌动态兑换并直接注入 Windows 凭据管理器
"""
from config import NAS_IP, NAS_HOSTNAME, SHARE_NAME, MOUNT_DRIVE, DRIVE_LABEL, PORT

def generate_windows_batch(linux_username: str, token: str, fullname: str = "") -> str:
    """
    生成针对该员工本人的 Windows 动态凭据挂载脚本 (.bat)
    包含 CRLF (\r\n) 换行符以确保 Windows 命令提示符执行
    """
    clean_name = fullname or linux_username
    claim_url = f"http://{NAS_IP}:{PORT}/api/claim-mount"
    json_body = '{\\"token\\":\\"' + token + '\\"}'

    script_lines = [
        "@echo off",
        "chcp 65001 >nul",
        f"title {DRIVE_LABEL} 资产盘安全自动化挂载工具",
        "color 0B",
        "",
        ":: ==================================================================",
        ":: MuYing 资产盘 - 60 秒一次性凭据安全挂载",
        ":: 注意：本脚本通过高熵安全令牌动态兑换凭据，本地文件绝不存储明文密码",
        ":: ==================================================================",
        "",
        "echo ==================================================================",
        f"echo               欢迎使用 {DRIVE_LABEL} 资产盘安全配置工具",
        "echo ==================================================================",
        f"echo 员工姓名: {clean_name}",
        f"echo 目标服务器: \\\\{NAS_IP}\\{SHARE_NAME} (或 \\\\{NAS_HOSTNAME}\\{SHARE_NAME})",
        "echo. ",
        "echo [安全检测] 正在通过 60 秒安全窗口向服务器兑换一次性凭据...",
        "",
        "powershell -NoProfile -ExecutionPolicy Bypass -Command ^",
        '    "$ErrorActionPreference = \'Stop\'; " ^',
        '    "try { " ^',
        f'        "$resp = Invoke-RestMethod -Uri \'{claim_url}\' -Method Post -ContentType \'application/json; charset=utf-8\' -Body \'{json_body}\' -TimeoutSec 10; " ^',
        '        "if (-not $resp.success) { " ^',
        '            "Write-Host (\'[错误] \' + $resp.error) -ForegroundColor Red; " ^',
        '            "exit 1; " ^',
        '        } " ^',
        '        "$u = $resp.linux_username; $p = $resp.password; " ^',
        '        "Write-Host \'[第 1 步/共 4 步] 正在清理旧网络连接与残留凭据...\' -ForegroundColor Cyan; " ^',
        f'        "cmdkey /delete:{NAS_IP} 2>$null | Out-Null; " ^',
        f'        "cmdkey /delete:{NAS_HOSTNAME} 2>$null | Out-Null; " ^',
        f'        "net use {MOUNT_DRIVE} /delete /y 2>$null | Out-Null; " ^',
        '        "Write-Host \'[第 2 步/共 4 步] 正在保存登录凭据至 Windows 凭据管理器...\' -ForegroundColor Cyan; " ^',
        f'        "cmdkey /add:{NAS_IP} /user:\\\"$u\\\" /pass:\\\"$p\\\" | Out-Null; " ^',
        f'        "cmdkey /add:{NAS_HOSTNAME} /user:\\\"$u\\\" /pass:\\\"$p\\\" | Out-Null; " ^',
        f'        "Write-Host \'[第 3 步/共 4 步] 正在挂载网络驱动器 ({MOUNT_DRIVE} 盘)...\' -ForegroundColor Cyan; " ^',
        f'        "$m1 = net use {MOUNT_DRIVE} \'\\\\{NAS_IP}\\{SHARE_NAME}\' \\\"$p\\\" /user:\\\"$u\\\" /persistent:yes 2>&1; " ^',
        '        "if ($LASTEXITCODE -ne 0) { " ^',
        f'            "$m2 = net use {MOUNT_DRIVE} \'\\\\{NAS_HOSTNAME}\\{SHARE_NAME}\' \\\"$p\\\" /user:\\\"$u\\\" /persistent:yes 2>&1; " ^',
        '        } " ^',
        '        "$p = $null; " ^',
        f'        "if (-not (Test-Path \'{MOUNT_DRIVE}\\\')) {{ " ^',
        '            "Write-Host \'[挂载失败] 请确认您的电脑已连接到局域网 WiFi 或网线。\' -ForegroundColor Red; " ^',
        '            "exit 1; " ^',
        '        } " ^',
        '        "Write-Host \'[第 4 步/共 4 步] 正在生成桌面快捷方式并重命名盘符...\' -ForegroundColor Cyan; " ^',
        '        "$wsh = New-Object -ComObject WScript.Shell; " ^',
        '        "$desk = [Environment]::GetFolderPath(\'Desktop\'); " ^',
        f'        "$lnk = $wsh.CreateShortcut(\\\"$desk\\{DRIVE_LABEL}.lnk\\\"); " ^',
        f'        "$lnk.TargetPath = \'{MOUNT_DRIVE}\\\'; " ^',
        f'        "$lnk.Description = \'{DRIVE_LABEL} 素材资源库快捷方式\'; " ^',
        '        "$lnk.Save(); " ^',
        f'        "try {{ (New-Object -ComObject Shell.Application).NameSpace(\'{MOUNT_DRIVE}\\\').Self.Name = \'{DRIVE_LABEL}\' }} catch {{}}; " ^',
        '        "Write-Host \'\'; " ^',
        '        "Write-Host \'==================================================================\' -ForegroundColor Green; " ^',
        f'        "Write-Host \' [√ 成功] 资产盘已成功挂载为本地 [{MOUNT_DRIVE} 盘]！\' -ForegroundColor Green; " ^',
        f'        "Write-Host \' [√ 成功] 您的桌面已生成 [{DRIVE_LABEL}] 快捷方式，双击即可进入。\' -ForegroundColor Green; " ^',
        '        "Write-Host \' [√ 成功] 凭据已加密保存，以后每天开机永久免输密码。\' -ForegroundColor Green; " ^',
        '        "Write-Host \'==================================================================\' -ForegroundColor Green; " ^',
        '        "Write-Host \'\'; " ^',
        f'        "Start-Process explorer.exe -ArgumentList \'{MOUNT_DRIVE}\\\'; " ^',
        '    } catch { " ^',
        '        "Write-Host (\'[错误] \' + $_.Exception.Message) -ForegroundColor Red; " ^',
        '        "exit 1; " ^',
        '    }"',
        "",
        "if %errorlevel% neq 0 (",
        "    echo.",
        "    echo ==================================================================",
        "    echo  [安全提示] 挂载未完成。",
        "    echo  若已超过 60 秒安全窗口期或令牌已使用，",
        "    echo  请回到网页端点击【重新挂载】获取全新配置脚本。",
        "    echo ==================================================================",
        "    echo.",
        "    pause",
        "    exit /b 1",
        ")",
        "",
        "echo 按任意键退出本配置窗口...",
        "pause >nul",
        "exit /b 0"
    ]
    return "\r\n".join(script_lines) + "\r\n"
