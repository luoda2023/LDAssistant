#!/usr/bin/env python3
"""CI: 删除 SQLite 索引并 VACUUM，减小数据库体积。"""
import sqlite3
import os

db = "standards_new.db"
if not os.path.exists(db):
    print("standards_new.db not found, skipping optimization")
    exit(0)

orig = os.path.getsize(db)
print(f"Original size: {orig / 1048576:.0f} MB")

conn = sqlite3.connect(db)
conn.execute("PRAGMA journal_mode=OFF")
conn.execute("PRAGMA synchronous=OFF")

indexes = [
    "idx_dedup_key",
    "idx_std_type",
    "idx_source",
    "idx_is_eng",
    "idx_status",
    "idx_publish_date",
    "idx_code",
]
for idx in indexes:
    try:
        conn.execute("DROP INDEX IF EXISTS " + idx)
        print(f"  Dropped index: {idx}")
    except Exception:
        pass

conn.execute("VACUUM")
conn.close()

newsz = os.path.getsize(db)
print(f"Optimized size: {newsz / 1048576:.0f} MB (saved {(orig - newsz) / 1048576:.0f} MB)")
