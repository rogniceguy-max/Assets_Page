# -*- coding: utf-8 -*-
"""
MuYing Hub - 系统全局配置
"""
import os

HOST = "0.0.0.0"
PORT = 8080
NAS_IP = "192.168.1.153"
NAS_HOSTNAME = "MuYingTV"
SHARE_NAME = "Assets"
MOUNT_DRIVE = "Z:"
DRIVE_LABEL = "MuYing"

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(BASE_DIR, "data")
DB_PATH = os.path.join(DATA_DIR, "muying.db")

# NAS 目录配置
NAS_ASSETS_PATH = "/volume1/Assets"
NAS_FILM_DIR = "/volume1/Assets/01_影视资源库"
NAS_WRITING_DIR = "/volume1/Assets/02_文学创作库"
NAS_PERSONAL_DIR = "/volume1/Assets/03_个人空间"
NAS_PUBLIC_DIR = "/volume1/Assets/00_公共素材"

# 系统组定义
GROUP_ASSETS = "group_assets"
GROUP_FILM = "group_film"
GROUP_WRITING = "group_writing"
