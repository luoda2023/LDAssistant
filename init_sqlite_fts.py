#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
标准规范数据：JSON -> SQLite + FTS5 迁移脚本
输入: all_standards_merged_with_replacement.json
输出: standards.db（含主表 + FTS5 全文索引）
"""
import json
import sqlite3
import time
from pathlib import Path

BASE_DIR = Path(__file__).parent.resolve()
JSON_FILE = BASE_DIR / "all_standards_merged_with_replacement.json"
DB_FILE = BASE_DIR / "standards.db"
BATCH = 5000


def init_db(conn: sqlite3.Connection):
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute("PRAGMA temp_store=MEMORY")
    conn.execute("PRAGMA cache_size=-64000")  # ~64MB

    # 先清理旧对象，确保 schema 最新
    conn.execute("DROP TABLE IF EXISTS standards")
    conn.execute("DROP TABLE IF EXISTS standards_fts")
    conn.execute("DROP INDEX IF EXISTS idx_status")
    conn.execute("DROP INDEX IF EXISTS idx_code")
    conn.execute("DROP INDEX IF EXISTS idx_publisher")
    conn.execute("DROP INDEX IF EXISTS idx_implement_date")
    conn.execute("DROP TRIGGER IF EXISTS standards_ai")
    conn.execute("DROP TRIGGER IF EXISTS standards_ad")
    conn.execute("DROP TRIGGER IF EXISTS standards_au")

    # 主表
    conn.execute("""
    CREATE TABLE IF NOT EXISTS standards (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        code TEXT NOT NULL,
        name TEXT NOT NULL,
        publisher TEXT,
        implement_date TEXT,
        status TEXT,
        detail_url TEXT,
        replacement_raw TEXT,
        replacement_parsed TEXT
    )
    """)

    # 普通索引：用于精确过滤
    conn.execute("CREATE INDEX IF NOT EXISTS idx_status ON standards(status)")
    conn.execute("CREATE INDEX IF NOT EXISTS idx_code ON standards(code)")
    conn.execute("CREATE INDEX IF NOT EXISTS idx_publisher ON standards(publisher)")
    conn.execute("CREATE INDEX IF NOT EXISTS idx_implement_date ON standards(implement_date)")

    # FTS5 虚拟表：用于全文检索（code/name/publisher/status/replacement_raw）
    conn.execute("""
    CREATE VIRTUAL TABLE IF NOT EXISTS standards_fts USING fts5(
        code,
        name,
        publisher,
        status,
        replacement_raw,
        content='standards',
        content_rowid='id'
    )
    """)

    # 触发器：主表写入时同步 FTS
    conn.execute("""
    CREATE TRIGGER IF NOT EXISTS standards_ai AFTER INSERT ON standards BEGIN
        INSERT INTO standards_fts(rowid, code, name, publisher, status, replacement_raw)
        VALUES (new.id, new.code, new.name, new.publisher, new.status, new.replacement_raw);
    END
    """)
    conn.execute("""
    CREATE TRIGGER IF NOT EXISTS standards_ad AFTER DELETE ON standards BEGIN
        INSERT INTO standards_fts(standards_fts, rowid, code, name, publisher, status, replacement_raw)
        VALUES ('delete', old.id, old.code, old.name, old.publisher, old.status, old.replacement_raw);
    END
    """)
    conn.execute("""
    CREATE TRIGGER IF NOT EXISTS standards_au AFTER UPDATE ON standards BEGIN
        INSERT INTO standards_fts(standards_fts, rowid, code, name, publisher, status, replacement_raw)
        VALUES ('delete', old.id, old.code, old.name, old.publisher, old.status, old.replacement_raw);
        INSERT INTO standards_fts(rowid, code, name, publisher, status, replacement_raw)
        VALUES (new.id, new.code, new.name, new.publisher, new.status, new.replacement_raw);
    END
    """)

    conn.commit()


def migrate(conn: sqlite3.Connection):
    import os, time
    # 若文件被占用，先尝试等待释放
    if DB_FILE.exists():
        for _ in range(5):
            try:
                DB_FILE.unlink()
                break
            except PermissionError:
                time.sleep(1)
        else:
            # 仍被占用时，跳过删除，走重建表逻辑
            print("数据库文件被占用，将尝试重建表结构")

    init_db(conn)

    print(f"读取 JSON: {JSON_FILE}")
    t0 = time.time()
    with open(JSON_FILE, "r", encoding="utf-8") as f:
        data = json.load(f)
    print(f"记录数: {len(data)}, 耗时: {time.time()-t0:.1f}s")

    fields = [
        "code", "name", "publisher", "implement_date",
        "status", "detail_url", "replacement_raw", "replacement_parsed"
    ]

    conn.execute("BEGIN")
    total = len(data)
    for i in range(0, total, BATCH):
        batch = data[i:i + BATCH]
        rows = []
        for item in batch:
            rows.append(tuple(item.get(k, "") for k in fields))
        conn.executemany(
            f"INSERT INTO standards ({','.join(fields)}) VALUES ({','.join(['?']*len(fields))})",
            rows
        )
        conn.commit()
        if (i // BATCH) % 5 == 0 or i + BATCH >= total:
            print(f"已插入: {min(i + BATCH, total)} / {total}")
    print(f"导入完成，耗时: {time.time()-t0:.1f}s")

    # 重建 FTS 索引（确保与主表一致）
    print("重建 FTS5 索引...")
    conn.execute("INSERT INTO standards_fts(standards_fts) VALUES('rebuild')")
    conn.commit()
    print("FTS5 索引重建完成")


def main():
    if not JSON_FILE.exists():
        raise SystemExit(f"找不到数据文件: {JSON_FILE}")
    conn = sqlite3.connect(str(DB_FILE))
    try:
        migrate(conn)
    finally:
        conn.close()
    print(f"数据库已生成: {DB_FILE}")


if __name__ == "__main__":
    main()
