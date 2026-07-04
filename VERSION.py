#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
工程助手 LDAssistant — 版本号统一管理
每次构建/提交前运行 bump_version.bat 自动更新补丁号
"""
import os

# 主版本号 (major, minor, patch)
VERSION = (10, 1, 0)

# 派生版本字符串
VERSION_STR = f"{VERSION[0]}.{VERSION[1]}.{VERSION[2]}"
VERSION_TAG = f"v{VERSION[0]}_{VERSION[1]}_{VERSION[2]}"
VERSION_DISPLAY = f"v{VERSION[0]}"  # 界面短显示
VERSION_APP = f"{VERSION[0]}.{VERSION[1]}"  # AppVersion 用

# 内部版本号（CI 构建时基于日期，展示用）
BUILD_META = ""


def get_build_tag():
    """获取 CI 构建用的完整 tag 名"""
    import datetime
    return f"v{VERSION[0]}-build-{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}"


if __name__ == '__main__':
    print(f"VERSION = {VERSION}")
    print(f"VERSION_STR = {VERSION_STR}")
    print(f"VERSION_TAG = {VERSION_TAG}")
    print(f"VERSION_DISPLAY = {VERSION_DISPLAY}")
    print(f"VERSION_APP = {VERSION_APP}")
