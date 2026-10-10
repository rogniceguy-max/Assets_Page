# -*- coding: utf-8 -*-
"""
MuYing Hub - SQLite 数据库管理模块（增强安全与零明文策略）
"""
import sqlite3
import os
import hashlib
from datetime import datetime
from config import DB_PATH, DATA_DIR

def init_db():
    if not os.path.exists(DATA_DIR):
        os.makedirs(DATA_DIR, exist_ok=True)
    
    conn = sqlite3.connect(DB_PATH)
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

def hash_password(password: str) -> str:
    return hashlib.sha256(password.encode('utf-8')).hexdigest()

def check_ip_registered(client_ip: str):
    """检查该客户端 IP 是否已经注册过"""
    if not client_ip:
        return None
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM users WHERE register_ip = ? AND status = 'active'", (client_ip,))
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None

def get_user_by_name(fullname: str):
    """通过员工全名查找账户"""
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM users WHERE username = ? OR display_name = ?", (fullname, fullname))
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None

def add_user(fullname: str, linux_username: str, password: str, group_type: str, register_ip: str):
    conn = sqlite3.connect(DB_PATH)
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
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()
    cursor.execute("INSERT INTO download_logs (username, client_ip) VALUES (?, ?)", (username, client_ip))
    conn.commit()
    conn.close()
