import requests, json, sqlite3, os, re, time, hashlib, base64, gzip
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed

DB_PATH = os.path.join(os.path.dirname(__file__), '..', 'standards.db')
print("=== 多源标准数据采集脚本 ===")

def get_existing_codes():
    if not os.path.exists(DB_PATH): return set()
    conn = sqlite3.connect(DB_PATH)
    codes = {r[0] for r in conn.execute('SELECT code FROM standards').fetchall()}
    conn.close()
    return codes

existing = get_existing_codes()
print(f"现有数据库: {DB_PATH}")
print(f"已有数据: {len(existing)} 条")
if os.path.exists(DB_PATH):
    size_mb = os.path.getsize(DB_PATH) / 1024 / 1024
    print(f"数据库大小: {size_mb:.1f} MB")
print("OK - 采集脚本就绪")