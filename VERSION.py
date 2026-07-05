#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
LDAssistant - Version management
Run bump_version.bat before each build to auto-increment patch number.
"""

# 主版本号 (major, minor, patch)
VERSION = (10, 3, 2)

# 派生版本字符串
VERSION_STR = f"{VERSION[0]}.{VERSION[1]}.{VERSION[2]}"
VERSION_TAG = f"v{VERSION[0]}_{VERSION[1]}_{VERSION[2]}"
VERSION_DISPLAY = f"v{VERSION[0]}"
VERSION_APP = f"{VERSION[0]}.{VERSION[1]}"

