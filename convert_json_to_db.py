#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
将标准规范数据库从 JSON 格式转换为 SQLite 格式（含 FTS5 全文索引）
用于 CI 构建和本地开发加速
"""
import json
import sqlite3
import re
import os
import sys
from pathlib import Path
from collections import Counter

_BASE_DIR = Path(__file__).parent.resolve()
_JSON_FILE = _BASE_DIR / "data" / "all_standards_merged_20260629_092235.json"
_DB_FILE = _BASE_DIR / "standards.db"

# 统一格式用于匹配 — 全角转半角 + 中文标点转英文 + 去空格 + OCR 修正
# 内联此函数以避免导入 standard_db.py（CI 中可能因路径问题导入失败）
_FULLWIDTH_RE = re.compile(r'[\uFF01-\uFF5E]')
_PUNCT_MAP = {
    '\u3002': '.', '\u3001': ',', '\u301C': '~',
    '\u2014': '-', '\u2013': '-', '\u2026': '...',
    '\u201C': '"', '\u201D': '"', '\u2018': "'", '\u2019': "'",
    '\u00D7': 'x',
}
def normalize_for_matching(text):
    if not text:
        return ''
    result = _FULLWIDTH_RE.sub(lambda m: chr(ord(m.group()) - 0xFEE0), text)
    for cn, en in _PUNCT_MAP.items():
        result = result.replace(cn, en)
    extra = {
        '\u00F7': '/', '\u00B7': '-', '\u2022': '-',
        '\u2032': "'", '\u2033': '"',
        '\u3008': '<', '\u3009': '>', '\u300A': '<', '\u300B': '>',
        '\u3010': '[', '\u3011': ']', '\u3014': '[', '\u3015': ']',
        '\uFF08': '(', '\uFF09': ')', '\uFF1A': ':', '\uFF1B': ';',
        '\uFF0C': ',', '\u3000': ' ',
    }
    for cn, en in extra.items():
        result = result.replace(cn, en)
    result = re.sub(r'\s+', '', result)
    result = re.sub(r'CJJJ', 'CJJ', result, flags=re.IGNORECASE)
    result = re.sub(r'DGJ(?=\d)', 'DG/TJ', result, flags=re.IGNORECASE)
    result = re.sub(r'[LlI](?=[A-Z\d])', '1', result)
    result = re.sub(r'(?<=[A-Z\d])[LlI]', '1', result)
    result = re.sub(r'[Oo](?=\d)', '0', result)
    return result.upper()


def _clean_status(status: str) -> str:
    """清理状态字段中的乱码"""
    if not status:
        return '未知'
    status = status.strip()
    # 常见乱码修复
    known = {
        '现行', '废止', '作废', '有更新版', '即将实施', '即将废止',
        '暂不实施', '在编',
    }
    for k in known:
        if k in status:
            return k
    # 尝试模糊匹配
    if '行' in status:
        return '现行'
    if '废' in status:
        if '更' in status or '版' in status:
            return '有更新版'
        return '废止'
    if '实施' in status:
        return '即将实施'
    return '未知'


def main():
    # 1. 加载 JSON
    if not _JSON_FILE.exists():
        print(f"❌ JSON 文件不存在: {_JSON_FILE}")
        sys.exit(1)

    json_size = _JSON_FILE.stat().st_size
    print(f"📂 加载 JSON ({json_size / 1024 / 1024:.0f} MB)...")
    with open(_JSON_FILE, 'r', encoding='utf-8') as f:
        records = json.load(f)
    print(f"✅ 加载完成: {len(records)} 条记录")

    # 2. 创建 SQLite 数据库
    if _DB_FILE.exists():
        _DB_FILE.unlink()
        print(f"🗑️  删除旧数据库")

    print(f"🔨 创建 SQLite 数据库: {_DB_FILE}")
    conn = sqlite3.connect(str(_DB_FILE))
    cursor = conn.cursor()

    # 启用 WAL 模式（读写性能更好）
    cursor.execute("PRAGMA journal_mode=WAL")
    cursor.execute("PRAGMA synchronous=NORMAL")

    # 创建主表
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS standards (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT '',
            publisher TEXT NOT NULL DEFAULT '',
            implement_date TEXT NOT NULL DEFAULT '',
            detail_url  TEXT NOT NULL DEFAULT '',
            replacement_raw  TEXT NOT NULL DEFAULT '',
            replacement_parsed TEXT NOT NULL DEFAULT '',
            source_type TEXT NOT NULL DEFAULT '',
            norm_code TEXT NOT NULL DEFAULT '',
            norm_name TEXT NOT NULL DEFAULT ''
        )
    """)

    # 创建 FTS5 全文索引
    cursor.execute("""
        CREATE VIRTUAL TABLE IF NOT EXISTS standards_fts USING fts5(
            code, name, status,
            content='standards',
            content_rowid='id',
            tokenize='unicode61'
        )
    """)

    # 创建索引
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_code ON standards(code)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_norm_code ON standards(norm_code)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_status ON standards(status)")

    # 3. 批量插入数据
    print("📝 写入数据...")
    insert_sql = """
        INSERT INTO standards
        (code, name, status, publisher, implement_date, detail_url,
         replacement_raw, replacement_parsed, source_type, norm_code, norm_name)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """

    batch_size = 10000
    total = len(records)
    status_counter = Counter()

    for i in range(0, total, batch_size):
        batch = records[i:i + batch_size]
        rows = []
        fts_rows = []
        for r in batch:
            code = (r.get('code') or '').strip()
            name = (r.get('name') or '').strip()
            status = _clean_status(r.get('status', ''))
            norm_code = normalize_for_matching(code)
            norm_name = normalize_for_matching(name)

            rows.append((
                code, name, status,
                (r.get('publisher') or '').strip(),
                (r.get('implement_date') or '').strip(),
                (r.get('detail_url') or '').strip(),
                (r.get('replacement_raw') or '').strip(),
                (r.get('replacement_parsed') or '').strip(),
                (r.get('source_type') or '').strip(),
                norm_code, norm_name,
            ))
            fts_rows.append((code, name, status))
            status_counter[status] += 1

        cursor.executemany(insert_sql, rows)
        conn.commit()

        # 插入 FTS（需要先获取插入的 id 范围）
        last_id = cursor.lastrowid
        start_id = last_id - len(batch) + 1
        for j, (c, n, s) in enumerate(fts_rows):
            cursor.execute(
                "INSERT INTO standards_fts(rowid, code, name, status) VALUES (?, ?, ?, ?)",
                (start_id + j, c, n, s)
            )
        conn.commit()

        progress = min(i + batch_size, total)
        print(f"  {progress}/{total} ({progress * 100 // total}%)")

    # 4. 重建 FTS5 索引（外部内容表需要显式 rebuild）
    print("🔨 重建 FTS5 全文索引...")
    cursor.execute("INSERT INTO standards_fts(standards_fts) VALUES('rebuild')")
    print("✅ FTS5 索引重建完成")

    # 5. 统计信息
    print(f"\n✅ 转换完成!")
    print(f" 总记录: {total}")
    print(f" 数据库大小: {_DB_FILE.stat().st_size / 1024 / 1024:.0f} MB")
    print(f" 状态分布:")
    for s, cnt in status_counter.most_common():
        print(f"  {s}: {cnt}")

    # 6. 验证
    print(f"\n🔍 验证...")
    cursor.execute("SELECT COUNT(*) FROM standards")
    count = cursor.fetchone()[0]
    cursor.execute("SELECT COUNT(*) FROM standards_fts")
    fts_count = cursor.fetchone()[0]
    print(f" standards 表: {count} 条")
    print(f" standards_fts 表: {fts_count} 条")

    # 测试查询
    cursor.execute("SELECT code, name, status FROM standards WHERE norm_code = ? LIMIT 5",
                   [normalize_for_matching("GB 50010-2010")])
    results = cursor.fetchall()
    print(f" 查询测试 'GB 50010-2010': {len(results)} 条结果")
    for r in results[:2]:
        print(f" - {r[0]}: {r[1]} ({r[2]})")

    conn.close()
    print(f"\n✅ 完成! 数据库已保存到: {_DB_FILE}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        import traceback
        print(f"❌ 错误: {e}")
        traceback.print_exc()
        sys.exit(1)