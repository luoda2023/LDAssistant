#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
标准规范数据库访问模块（SQLite + FTS5）
替代原来的 JSON 加载方式，提供更快的查询和全文检索能力。
"""
import sqlite3
import re
from pathlib import Path
from html import unescape

_BASE_DIR = Path(__file__).parent.resolve()
_DB_FILE = _BASE_DIR / "standards.db"

# 保持与旧逻辑一致的全角/符号规范化
_FULLWIDTH_RE = re.compile(r'[\uFF01-\uFF5E]')
_PUNCT_MAP = {
    '\u3002': '.', '\u3001': ',', '\u301C': '~',
    '\u2014': '-', '\u2013': '-', '\u2026': '...',
    '\u201C': '"', '\u201D': '"', '\u2018': "'", '\u2019': "'",
    '\u00D7': 'x',
}


def _connect() -> sqlite3.Connection:
    conn = sqlite3.connect(str(_DB_FILE))
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    return conn


def normalize_for_matching(text: str) -> str:
    if not text:
        return ''
    result = _FULLWIDTH_RE.sub(lambda m: chr(ord(m.group(0)) - 0xFEE0), text)
    for cn, en in _PUNCT_MAP.items():
        result = result.replace(cn, en)
    result = re.sub(r'\s+', '', result)
    result = re.sub(r'CJJJ', 'CJJ', result, flags=re.IGNORECASE)
    result = re.sub(r'DGJ(?=\d)', 'DG/TJ', result, flags=re.IGNORECASE)
    return result


def _escape_fts5(query: str) -> str:
    """对 FTS5 查询词做最小转义，避免特殊字符导致语法错误。"""
    if not query:
        return ''
    # FTS5 中需要转义的部分字符：空格用于分词，引号用于短语
    # 这里尽量保留用户输入空格语义，仅处理明显的特殊字符
    # 如需精确搜索，调用方可以使用引号包裹
    escaped = query.replace('"', '""')
    return escaped


class StandardChecker:
    def __init__(self):
        if not _DB_FILE.exists():
            raise RuntimeError(f"数据库不存在: {_DB_FILE}")
        self.conn = _connect()
        self.code_index = {}
        self.name_index = {}
        self._load_indexes()

    def _load_indexes(self):
        cur = self.conn.cursor()
        cur.execute("SELECT code, name, status, replacement_raw, publisher, implement_date FROM standards")
        rows = cur.fetchall()
        for r in rows:
            code = normalize_for_matching(r['code'])
            if code:
                self.code_index[code] = dict(r)
            name = (r['name'] or '').strip()
            if name:
                norm_name = normalize_for_matching(name)
                self.name_index[norm_name] = dict(r)
        print(f"[SQLite] 已加载 {len(rows)} 条规范，code_index={len(self.code_index)}, name_index={len(self.name_index)}")

    def check_code(self, code: str, name: str = ''):
        normalized = normalize_for_matching(code)
        result = {'found': False, 'status': '未找到', 'replacement_raw': '', 'publisher': '', 'implement_date': ''}

        # 1. 精确 code 匹配（优先走主表索引）
        record = self.code_index.get(normalized)
        if record:
            result.update({
                'found': True,
                'status': record.get('status', ''),
                'replacement_raw': record.get('replacement_raw', ''),
                'publisher': record.get('publisher', ''),
                'implement_date': record.get('implement_date', ''),
                'matched_name': record.get('name', ''),
            })
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(record.get('name', '')).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
            return result

        # 2. 名称精确/包含匹配（可降级为 FTS5 文本检索）
        if name:
            norm_name = normalize_for_matching(name).strip()
            if norm_name in self.name_index:
                record = self.name_index[norm_name]
                result.update({
                    'found': True,
                    'status': record.get('status', ''),
                    'replacement_raw': record.get('replacement_raw', ''),
                    'publisher': record.get('publisher', ''),
                    'implement_date': record.get('implement_date', ''),
                    'matched_name': record.get('name', ''),
                })
                if code:
                    norm_code = normalize_for_matching(code).strip()
                    db_code = normalize_for_matching(record.get('code', '')).strip()
                    if norm_code and db_code and (norm_code in db_code or db_code in norm_code):
                        result['dual_match'] = True
                return result

            # 名称部分匹配：先用 FTS5 做全文检索，再在结果集里做 code 交叉校验
            if norm_name and len(norm_name) >= 2:
                try:
                    cur = self.conn.cursor()
                    # 用子查询从 FTS5 取 rowid，再回主表查完整字段
                    cur.execute(
                        """
                        SELECT s.code, s.name, s.status, s.publisher, s.implement_date, s.replacement_raw
                        FROM standards s
                        WHERE s.id IN (
                            SELECT rowid FROM standards_fts WHERE standards_fts MATCH ?
                        )
                        LIMIT 50
                        """,
                        (norm_name,)
                    )
                    rows = cur.fetchall()
                except sqlite3.OperationalError:
                    rows = []
                for r in rows:
                    db_code = normalize_for_matching(r['code'])
                    db_name = normalize_for_matching(r['name'])
                    # 若未提供 code，仅按 name/status FTS5 召回，直接接受首条结果
                    if not normalized:
                        result.update({
                            'found': True,
                            'status': r['status'],
                            'replacement_raw': r['replacement_raw'],
                            'publisher': r['publisher'],
                            'implement_date': r['implement_date'],
                            'matched_name': r['name'],
                        })
                        if code:
                            norm_code = normalize_for_matching(code).strip()
                            db_code_norm = normalize_for_matching(r['code']).strip()
                            if norm_code and db_code_norm and (norm_code in db_code_norm or db_code_norm in norm_code):
                                result['dual_match'] = True
                        return result
                    # 若同时提供了 code，则要求 code/name 至少一项交叉匹配
                    if (normalized in db_code or db_code in normalized or
                        norm_name in db_name or db_name in norm_name):
                        result.update({
                            'found': True,
                            'status': r['status'],
                            'replacement_raw': r['replacement_raw'],
                            'publisher': r['publisher'],
                            'implement_date': r['implement_date'],
                            'matched_name': r['name'],
                        })
                        if code:
                            norm_code = normalize_for_matching(code).strip()
                            db_code_norm = normalize_for_matching(r['code']).strip()
                            if norm_code and db_code_norm and (norm_code in db_code_norm or db_code_norm in norm_code):
                                result['dual_match'] = True
                        return result

        return result

    def close(self):
        try:
            self.conn.close()
        except Exception:
            pass
