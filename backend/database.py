# -*- coding: utf-8 -*-
"""
MuYing Hub - SQLite 数据库管理模块（增强安全与零明文策略）
"""
import sqlite3
import os
import hashlib
from datetime import datetime
from config import DB_PATH, DATA_DIR

def get_db_connection():
    if not os.path.exists(DATA_DIR):
        os.makedirs(DATA_DIR, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS users (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT UNIQUE NOT NULL,
        linux_username TEXT UNIQUE NOT NULL,
        display_name TEXT,
        password_hash TEXT NOT NULL,
        raw_password_encrypted TEXT,
        group_type TEXT NOT NULL,
        register_ip TEXT,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        last_login TIMESTAMP,
        status TEXT DEFAULT 'active'
    )
    """)
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS download_logs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT NOT NULL,
        client_ip TEXT,
        downloaded_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    )
    """)
    conn.commit()
    return conn

def init_db():
    conn = get_db_connection()
    cursor = conn.cursor()
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS users (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT UNIQUE NOT NULL,            -- 全名（如：张三）
        linux_username TEXT UNIQUE NOT NULL,      -- 系统账号（如：zhangsan）
        display_name TEXT,                        -- 兼容字段（全名）
        password_hash TEXT NOT NULL,
        raw_password_encrypted TEXT,             -- 废弃持久化明文，保持为 NULL
        group_type TEXT NOT NULL,                 -- film 或 writing
        register_ip TEXT,                         -- 注册电脑客户端 IP
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
        last_login TIMESTAMP,
        status TEXT DEFAULT 'active'
    )
    """)
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS download_logs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT NOT NULL,
        client_ip TEXT,
        downloaded_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    )
    """)
    # 彻底抹除数据库中历史上可能暂存的明文密码，践行零明文合规
    cursor.execute("UPDATE users SET raw_password_encrypted = NULL WHERE raw_password_encrypted IS NOT NULL")
    conn.commit()
    conn.close()

import secrets

def hash_password(password: str) -> str:
    """使用 Python 标准库 scrypt + 独立随机盐生成密码哈希"""
    salt = secrets.token_bytes(16)
    n, r, p = 16384, 8, 1
    key = hashlib.scrypt(password.encode('utf-8'), salt=salt, n=n, r=r, p=p)
    return f"scrypt${n}${r}${p}${salt.hex()}${key.hex()}"

def verify_password(password: str, stored_hash: str) -> tuple:
    """
    验证密码，返回 (is_valid, needs_rehash)。
    - is_valid: 密码是否匹配
    - needs_rehash: 如果是旧版 SHA-256，需升级重哈希为 scrypt
    """
    if not stored_hash or not password:
        return False, False

    if stored_hash.startswith("scrypt$"):
        parts = stored_hash.split("$")
        if len(parts) == 6:
            _, n_str, r_str, p_str, salt_hex, key_hex = parts
            try:
                n, r, p = int(n_str), int(r_str), int(p_str)
                salt = bytes.fromhex(salt_hex)
                expected_key = bytes.fromhex(key_hex)
                calculated_key = hashlib.scrypt(password.encode('utf-8'), salt=salt, n=n, r=r, p=p)
                is_valid = secrets.compare_digest(calculated_key, expected_key)
                return is_valid, False
            except Exception:
                return False, False

    # 兼容旧版本无盐 SHA-256（安全对比，避免时序攻击）
    old_hash = hashlib.sha256(password.encode('utf-8')).hexdigest()
    if secrets.compare_digest(old_hash, stored_hash):
        return True, True  # 匹配成功，需要升级为 scrypt

    return False, False

def update_user_password_hash(username: str, new_hash: str):
    """迁移升级用户密码哈希到 scrypt"""
    conn = get_db_connection()
    cursor = conn.cursor()
    cursor.execute("UPDATE users SET password_hash = ? WHERE username = ? OR display_name = ? OR linux_username = ?", 
                   (new_hash, username, username, username))
    conn.commit()
    conn.close()

def check_ip_registered(client_ip: str):
    """检查该客户端 IP 是否已经注册过"""
    if not client_ip:
        return None
    conn = get_db_connection()
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM users WHERE register_ip = ? AND status = 'active'", (client_ip,))
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None

def get_user_by_name(fullname: str):
    """通过员工全名或系统账号查找账户"""
    conn = get_db_connection()
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM users WHERE username = ? OR display_name = ? OR linux_username = ?", (fullname, fullname, fullname))
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None

def add_user(fullname: str, linux_username: str, password: str, group_type: str, register_ip: str):
    conn = get_db_connection()
    cursor = conn.cursor()
    p_hash = hash_password(password)
    # 安全策略：绝不将明文或弱加密密码持久化写入数据库，字段置为 NULL
    cursor.execute("""
    INSERT INTO users (username, linux_username, display_name, password_hash, raw_password_encrypted, group_type, register_ip)
    VALUES (?, ?, ?, ?, NULL, ?, ?)
    """, (fullname, linux_username, fullname, p_hash, group_type, register_ip))
    conn.commit()
    conn.close()

def log_download(username: str, client_ip: str):
    conn = get_db_connection()
    cursor = conn.cursor()
    cursor.execute("INSERT INTO download_logs (username, client_ip) VALUES (?, ?)", (username, client_ip))
    conn.commit()
    conn.close()
