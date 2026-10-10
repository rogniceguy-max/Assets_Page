# -*- coding: utf-8 -*-
"""
MuYing Hub - NAS Linux 与 Samba 用户系统管理核心
支持全名转系统安全账号与个人空间自动创建
"""
import subprocess
import os
from config import GROUP_ASSETS, GROUP_FILM, GROUP_WRITING, NAS_PERSONAL_DIR
from core.pinyin_dict import name_to_ascii

def get_unique_linux_username(fullname: str) -> str:
    """根据全名生成唯一的 Linux 系统用户名"""
    base_slug = name_to_ascii(fullname)
    candidate = base_slug
    idx = 2
    
    while True:
        # 检查系统中是否已有同名系统用户
        res = subprocess.run(["id", candidate], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if res.returncode != 0:
            # 用户名可用
            return candidate
        # 已存在则追加递增序号
        candidate = f"{base_slug}{idx}"
        idx += 1

def create_system_and_samba_user(linux_username: str, password: str, group_type: str, fullname: str) -> tuple[bool, str]:
    """
    在 NAS 系统中创建 Linux 账号与 Samba 共享账号
    """
    target_group = GROUP_FILM if group_type == "film" else GROUP_WRITING
    
    # 1. 检查或创建 Linux 用户
    check_user = subprocess.run(["id", linux_username], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check_user.returncode != 0:
        res = subprocess.run([
            "sudo", "useradd", "-M", "-s", "/usr/sbin/nologin",
            "-G", f"{GROUP_ASSETS},{target_group},ughomeusers",
            linux_username
        ], capture_output=True, text=True)
        if res.returncode != 0:
            return False, f"创建 Linux 系统用户失败: {res.stderr}"
    else:
        subprocess.run(["sudo", "usermod", "-aG", f"{GROUP_ASSETS},{target_group},ughomeusers", linux_username])

    # 2. 设置并启用 Samba 密码
    p_input = f"{password}\n{password}\n"
    smb_proc = subprocess.Popen(
        ["sudo", "smbpasswd", "-s", "-a", linux_username],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True
    )
    smb_out, smb_err = smb_proc.communicate(input=p_input)
    if smb_proc.returncode != 0:
        return False, f"配置 Samba 用户密码失败: {smb_err}"

    subprocess.run(["sudo", "smbpasswd", "-e", linux_username], capture_output=True)

    # 3. 创建个人专属空间目录（以员工全名命名，方便在 Windows 中直观查看）
    personal_user_dir = os.path.join(NAS_PERSONAL_DIR, fullname)
    try:
        subprocess.run(["sudo", "mkdir", "-p", personal_user_dir], check=True)
        subprocess.run(["sudo", "chown", "-R", f"{linux_username}:{GROUP_ASSETS}", personal_user_dir], check=True)
        subprocess.run(["sudo", "chmod", "-R", "770", personal_user_dir], check=True)
    except Exception as e:
        print(f"Warning: 个人空间目录创建异常: {e}")

    return True, "Samba 用户及权限配置成功"
