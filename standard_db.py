#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
标准规范数据库访问模块（SQLite + FTS5）
支持从加密 DLL 加载数据库
"""
import sqlite3
import re
import tempfile
import os
from pathlib import Path
from html import unescape

_BASE_DIR = Path(__file__).parent.resolve()
_DLL_FILE = _BASE_DIR / "standards.dll"
_DB_FILE = _BASE_DIR / "standards.db"

_FULLWIDTH_RE = re.compile(r'[\uFF01-\uFF5E]')
_PUNCT_MAP = {
    '\u3002': '.', '\u3001': ',', '\u301C': '~',
    '\u2014': '-', '\u2013': '-', '\u2026': '...',
    '\u201C': '"', '\u201D': '"', '\u2018': "'", '\u2019': "'",
    '\u00D7': 'x',
}

def _decrypt_dll_to_temp():
    """从 DLL 解密密文到临时文件"""
    try:
        from decrypt_module import decrypt_dll
        data = decrypt_dll(str(_DLL_FILE))
        fd, tmp_path = tempfile.mkstemp(suffix='.db')
        os.close(fd)
        with open(tmp_path, 'wb') as f:
            f.write(data)
        return tmp_path
    except Exception as e:
        raise RuntimeError(f"Failed to decrypt DLL: {e}")

def _connect() -> sqlite3.Connection:
    if _DLL_FILE.exists():
        tmp_db = _decrypt_dll_to_temp()
        conn = sqlite3.connect(tmp_db, check_same_thread=False)
        conn._tmp_db_path = tmp_db
        return conn
    elif _DB_FILE.exists():
        conn = sqlite3.connect(str(_DB_FILE), check_same_thread=False)
        conn._tmp_db_path = None
        return conn
    else:
        raise FileNotFoundError(f"Neither {_DLL_FILE} nor {_DB_FILE} found")

def close_connection(conn):
    if conn:
        conn.close()
        tmp = getattr(conn, '_tmp_db_path', None)
        if tmp and os.path.exists(tmp):
            try:
                os.unlink(tmp)
            except:
                pass