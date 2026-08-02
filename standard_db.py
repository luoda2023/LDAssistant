#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
标准规范数据库访问模块（SQLite + FTS5）
支持从加密 DLL 加载数据库，按需查询不加载全部数据到内存
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


def normalize_for_matching(text):
    """统一格式用于匹配 — 全角转半角 + 中文标点转英文 + 去空格 + OCR 修正"""
    if not text:
        return ''
    result = _FULLWIDTH_RE.sub(lambda m: chr(ord(m.group()) - 0xFEE0), text)
    for cn, en in _PUNCT_MAP.items():
        result = result.replace(cn, en)
    # 额外标点
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
    # OCR 常见识别错误修正
    result = re.sub(r'CJJJ', 'CJJ', result, flags=re.IGNORECASE)
    result = re.sub(r'DGJ(?=\d)', 'DG/TJ', result, flags=re.IGNORECASE)
    result = re.sub(r'[LlI](?=[A-Z\d])', '1', result)
    result = re.sub(r'(?<=[A-Z\d])[LlI]', '1', result)
    result = re.sub(r'[Oo](?=\d)', '0', result)
    return result.upper()


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


def _find_db() -> Path:
    """查找可用的数据库文件路径"""
    if _DLL_FILE.exists():
        tmp_path = _decrypt_dll_to_temp()
        return Path(tmp_path)
    if _DB_FILE.exists():
        return _DB_FILE
    # 也检查打包目录
    import sys
    if getattr(sys, 'frozen', False) and hasattr(sys, '_MEIPASS'):
        meipass = Path(sys._MEIPASS)
        p = meipass / "standards.db"
        if p.exists():
            return p
    return None


def close_connection(conn, tmp_path=None):
    if conn:
        conn.close()
    if tmp_path and os.path.exists(tmp_path):
        try:
            os.unlink(tmp_path)
        except Exception:
            pass


class StandardChecker:
    """标准规范数据库查询器（按需查询，不加载全部数据到内存）"""

    def __init__(self):
        self._conn = None
        self._tmp_path = None
        try:
            db_path = _find_db()
            if db_path is None:
                raise FileNotFoundError(f"Neither {_DLL_FILE} nor {_DB_FILE} found")
            self._conn = sqlite3.connect(str(db_path), check_same_thread=False)
            self._conn.row_factory = sqlite3.Row
            if _DLL_FILE.exists():
                self._tmp_path = str(db_path)
            self._verify()
            print(f"[StandardChecker] SQLite 连接成功")
        except Exception as e:
            raise RuntimeError(f"SQLite 连接失败: {e}")

    def _verify(self):
        """验证数据库表结构"""
        c = self._conn.cursor()
        tables = [r['name'] for r in c.execute(
            "SELECT name FROM sqlite_master WHERE type='table'"
        ).fetchall()]
        if 'standards' not in tables:
            raise RuntimeError(f"数据库缺少 standards 表，现有: {tables}")
        c.execute("SELECT COUNT(*) FROM standards")
        count = c.fetchone()[0]
        print(f"[StandardChecker] 数据库就绪: {count} 条记录")

    def check_code(self, code, name=''):
        """查询规范：返回 {'found': bool, 'status': str, ...}"""
        normalized = normalize_for_matching(code)
        result = {
            'found': False, 'status': '未找到',
            'replacement_raw': '', 'publisher': '',
            'implement_date': '', 'matched_name': '',
            'dual_match': False,
        }

        c = self._conn.cursor()

        # 1) 精确匹配 norm_code
        c.execute(
            "SELECT * FROM standards WHERE norm_code = ? LIMIT 1",
            (normalized,)
        )
        row = c.fetchone()
        if row:
            result.update({
                'found': True,
                'status': row['status'],
                'replacement_raw': row['replacement_raw'],
                'publisher': row['publisher'],
                'implement_date': row['implement_date'],
                'matched_name': row['name'],
            })
            # 双重确认：名称也匹配
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(row['name']).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
            return result

        # 2) 精确匹配原始 code
        c.execute(
            "SELECT * FROM standards WHERE code = ? LIMIT 1",
            (code.strip(),)
        )
        row = c.fetchone()
        if row:
            result.update({
                'found': True,
                'status': row['status'],
                'replacement_raw': row['replacement_raw'],
                'publisher': row['publisher'],
                'implement_date': row['implement_date'],
                'matched_name': row['name'],
            })
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(row['name']).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
            return result

        # 3) 名称匹配（如果有名称输入）
        if name:
            norm_name = normalize_for_matching(name).strip()
            if norm_name:
                c.execute(
                    "SELECT * FROM standards WHERE norm_name = ? LIMIT 1",
                    (norm_name,)
                )
                row = c.fetchone()
                if row:
                    result.update({
                        'found': True,
                        'status': row['status'],
                        'replacement_raw': row['replacement_raw'],
                        'publisher': row['publisher'],
                        'implement_date': row['implement_date'],
                        'matched_name': row['name'],
                    })
                    if code:
                        norm_code = normalize_for_matching(code).strip()
                        db_code = normalize_for_matching(row['code']).strip()
                        if norm_code and db_code and (norm_code in db_code or db_code in norm_code):
                            result['dual_match'] = True
                    return result

        # 4) FTS5 全文搜索（模糊匹配）
        try:
            # 用 FTS5 搜索 code
            fts_query = '"' + normalized + '"'
            c.execute(
                """SELECT s.* FROM standards s
                JOIN standards_fts fts ON s.id = fts.rowid
                WHERE standards_fts MATCH ? LIMIT 5""",
                (fts_query,)
            )
            rows = c.fetchall()
            if rows:
                best = self._pick_best_match(rows, normalized, name)
                if best:
                    result.update({
                        'found': True,
                        'status': best['status'],
                        'replacement_raw': best['replacement_raw'],
                        'publisher': best['publisher'],
                        'implement_date': best['implement_date'],
                        'matched_name': best['name'],
                    })
                    return result
        except Exception:
            pass

        return result

    def find_similar_codes(self, query, limit=20):
        """模糊搜索，返回匹配的规范列表"""
        normalized = normalize_for_matching(query)
        if len(normalized) < 2:
            return []

        c = self._conn.cursor()
        results = []

        # 先用 LIKE 模糊匹配 code
        c.execute(
            """SELECT * FROM standards
            WHERE code LIKE ? OR norm_code LIKE ?
            LIMIT ?""",
            (f'%{normalized}%', f'%{normalized}%', limit)
        )
        results.extend([dict(r) for r in c.fetchall()])

        if len(results) < limit:
            # 再用 FTS5 搜索
            try:
                fts_query = ' OR '.join(f'"{w}"' for w in normalized if len(w) >= 2)
                if fts_query:
                    c.execute(
                        """SELECT s.* FROM standards s
                        JOIN standards_fts fts ON s.id = fts.rowid
                        WHERE standards_fts MATCH ? LIMIT ?""",
                        (fts_query, limit - len(results))
                    )
                    seen = {r['id'] for r in results}
                    for r in c.fetchall():
                        if r['id'] not in seen:
                            results.append(dict(r))
                            seen.add(r['id'])
            except Exception:
                pass

        return results[:limit]

    def get_status_counts(self):
        """获取各状态统计"""
        c = self._conn.cursor()
        c.execute("SELECT status, COUNT(*) as cnt FROM standards GROUP BY status ORDER BY cnt DESC")
        return {r['status']: r['cnt'] for r in c.fetchall()}

    def _pick_best_match(self, rows, normalized, name):
        """从多个 FTS5 结果中选最佳匹配"""
        best = None
        best_score = -1
        for row in rows:
            score = 0
            db_norm = normalize_for_matching(row['code'])
            # 完全匹配加分
            if db_norm == normalized:
                score += 100
            elif db_norm.startswith(normalized) or normalized.startswith(db_norm):
                score += 50
            # 名称匹配加分
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(row['name']).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    score += 30
            if score > best_score:
                best_score = score
                best = row
        return best

    def close(self):
        """关闭数据库连接"""
        if self._conn:
            conn = self._conn
            self._conn = None
            close_connection(conn, self._tmp_path)