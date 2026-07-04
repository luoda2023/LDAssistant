#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
标准规范数据库访问模块（SQLite + FTS4）
适配新数据库 schema：含 dedup_key / is_eng / std_type / publish_date / act_date 等30万+记录
提供快速查询、全文检索、工程标准标识能力。
"""
import sqlite3
import sys
import re
from pathlib import Path

# 支持 PyInstaller 打包后的路径：sys._MEIPASS（单文件模式）或 __file__（文件夹模式）
if getattr(sys, 'frozen', False):
    _BASE_DIR = Path(sys._MEIPASS)
else:
    _BASE_DIR = Path(__file__).parent.resolve()

# 支持 PyInstaller 打包后的路径：先尝试同目录，再尝试 data/ 子目录
_DB_FILE_CANDIDATES = [
    _BASE_DIR / "standards_new.db",
    _BASE_DIR / "data" / "standards_new.db",
    _BASE_DIR / "standards.db",
    _BASE_DIR / "data" / "standards.db",
]
_DB_FILE = None
for _p in _DB_FILE_CANDIDATES:
    if _p.exists():
        _DB_FILE = _p
        break
if _DB_FILE is None:
    _DB_FILE = _BASE_DIR / "standards_new.db"  # 默认路径，不存在会抛异常

# 全角/符号规范化
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
    conn.execute("PRAGMA cache_size=-32000")  # 32MB cache for 300k records
    return conn


def normalize_for_matching(text: str) -> str:
    """统一格式用于匹配：全角转半角、中文符号转英文符号、去除空格、修正OCR错误"""
    if not text:
        return ''
    result = _FULLWIDTH_RE.sub(lambda m: chr(ord(m.group(0)) - 0xFEE0), text)
    for cn, en in _PUNCT_MAP.items():
        result = result.replace(cn, en)
    result = re.sub(r'\s+', '', result)
    # OCR常见错误修正
    result = re.sub(r'CJJJ', 'CJJ', result, flags=re.IGNORECASE)
    result = re.sub(r'DGJ(?=\d)', 'DG/TJ', result, flags=re.IGNORECASE)
    return result


def clean_status(status: str) -> str:
    """清理状态字段中的乱码，映射为标准状态值"""
    if not status:
        return '未知'
    status = status.strip()
    if '现行' in status and len(status) <= 4:
        return '现行'
    if '废止' in status and len(status) <= 4:
        return '废止'
    if '作废' in status and len(status) <= 4:
        return '作废'
    if '有更新版' in status or '有更新' in status:
        return '有更新版'
    if '即将实施' in status or '将实施' in status:
        return '即将实施'
    if '暂不实施' in status:
        return '暂不实施'
    if '在编' in status:
        return '在编'
    return status


class StandardChecker:
    """标准规范检查器：全量加载 + FTS4 全文检索"""
    def __init__(self, progress_callback=None):
        if not _DB_FILE or not _DB_FILE.exists():
            raise RuntimeError(f"数据库文件不存在。已搜索路径: {', '.join(str(p) for p in _DB_FILE_CANDIDATES)}")

        print(f"[StandardChecker] 使用数据库: {_DB_FILE} (大小: {_DB_FILE.stat().st_size / 1024 / 1024:.1f} MB)")
        self.conn = _connect()
        self.code_index = {}
        self.name_index = {}
        self.dedup_index = {}
        self._load_indexes(progress_callback)

    def _load_indexes(self, progress_callback=None):
        cur = self.conn.cursor()
        cur.execute(
            "SELECT id, code, dedup_key, name, status, publish_date, act_date, "
            "source, std_type, is_eng FROM standards"
        )
        rows = cur.fetchall()
        total = len(rows)
        for i, r in enumerate(rows):
            if progress_callback and i % 1000 == 0:
                progress_callback(i, total, "正在加载规范数据库...")
            code = normalize_for_matching(r['code'])
            if code:
                self.code_index[code] = dict(r)
            name = (r['name'] or '').strip()
            if name:
                norm_name = normalize_for_matching(name)
                if norm_name not in self.name_index:
                    self.name_index[norm_name] = dict(r)
            dedup = (r['dedup_key'] or '').strip()
            if dedup:
                norm_dedup = normalize_for_matching(dedup)
                if norm_dedup not in self.dedup_index:
                    self.dedup_index[norm_dedup] = dict(r)
        if progress_callback:
            progress_callback(total, total, "规范数据库加载完成")

        print(f"[SQLite] 已加载 {len(rows)} 条规范，"
              f"code_index={len(self.code_index)}, "
              f"name_index={len(self.name_index)}, "
              f"dedup_index={len(self.dedup_index)}")

    def check_code(self, code: str, name: str = ''):
        """
        检查规范编号/名称，返回完整信息。
        
        匹配策略：
        1. dedup_key 精确匹配（最高优先级）
        2. code 精确匹配
        3. 名称精确匹配
        4. FTS4 全文检索 + 交叉校验
        """
        normalized = normalize_for_matching(code)
        norm_name = normalize_for_matching(name).strip() if name else ''

        result = {
            'found': False,
            'status': '未找到',
            'std_type': '',
            'is_eng': False,
            'publish_date': '',
            'act_date': '',
            'source': '',
            'matched_code': '',
            'matched_name': '',
            'dual_match': False,
        }

        # 1. dedup_key 精确匹配
        if normalized and normalized in self.dedup_index:
            r = self.dedup_index[normalized]
            result.update(self._record_to_result(r))
            result['matched_name'] = r.get('name', '')
            result['matched_code'] = r.get('code', '')
            if norm_name and self._names_match(norm_name, r.get('name', '')):
                result['dual_match'] = True
            return result

        # 2. code 精确匹配
        if normalized and normalized in self.code_index:
            r = self.code_index[normalized]
            result.update(self._record_to_result(r))
            result['matched_name'] = r.get('name', '')
            result['matched_code'] = r.get('code', '')
            if norm_name and self._names_match(norm_name, r.get('name', '')):
                result['dual_match'] = True
            return result

        # 3. 名称精确匹配
        if norm_name and norm_name in self.name_index:
            r = self.name_index[norm_name]
            result.update(self._record_to_result(r))
            result['matched_name'] = r.get('name', '')
            result['matched_code'] = r.get('code', '')
            if normalized and self._codes_match(normalized, r.get('code', '')):
                result['dual_match'] = True
            return result

        # 4. FTS4 全文检索（名称包含匹配）
        if norm_name and len(norm_name) >= 2:
            fts_result = self._fts_search(norm_name, limit=30)
            best = None
            for row in fts_result:
                db_code = normalize_for_matching(row['code'])
                db_name = normalize_for_matching(row['name'])
                # 优先交叉匹配
                code_match = normalized and (normalized in db_code or db_code in normalized)
                name_match = norm_name and (norm_name in db_name or db_name in norm_name)
                if code_match or name_match:
                    best = row
                    if code_match and name_match:
                        break  # 双重匹配，立即返回
            
            if best:
                result.update(self._record_to_result(best))
                result['matched_name'] = best.get('name', '')
                result['matched_code'] = best.get('code', '')
                db_code = normalize_for_matching(best['code'])
                db_name = normalize_for_matching(best['name'])
                if normalized and (normalized in db_code or db_code in normalized):
                    result['dual_match'] = True
                if norm_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
                return result

        # 5. 模糊匹配（code子串匹配）
        if normalized and len(normalized) >= 3:
            best_match = None
            best_score = 0
            for k, v in self.code_index.items():
                if normalized in k or k in normalized:
                    score = len(normalized) / max(len(k), len(normalized))
                    if score > best_score:
                        best_score = score
                        best_match = v
                elif len(normalized) > 3 and len(k) > 3:
                    # 字符级模糊匹配
                    matches = 0
                    n_idx = 0
                    k_idx = 0
                    while n_idx < len(normalized) and k_idx < len(k):
                        if normalized[n_idx] == k[k_idx]:
                            matches += 1
                            n_idx += 1
                            k_idx += 1
                        else:
                            k_idx += 1
                    similarity = matches / max(len(normalized), len(k))
                    if similarity > 0.8 and similarity > best_score:
                        best_score = similarity
                        best_match = v

            if best_match:
                result.update(self._record_to_result(best_match))
                result['matched_name'] = best_match.get('name', '')
                result['matched_code'] = best_match.get('code', '')
                return result

        return result

    def _record_to_result(self, r: dict) -> dict:
        """将数据库记录转为返回结果"""
        raw_status = r.get('status', '') or ''
        return {
            'found': True,
            'status': clean_status(raw_status),
            'std_type': r.get('std_type', '') or '',
            'is_eng': bool(r.get('is_eng', 0)),
            'publish_date': r.get('publish_date', '') or '',
            'act_date': r.get('act_date', '') or '',
            'source': r.get('source', '') or '',
        }

    def _names_match(self, norm_name: str, db_name: str) -> bool:
        """检查规范化后的名称是否匹配"""
        if not norm_name or not db_name:
            return False
        db_norm = normalize_for_matching(db_name).strip()
        return (norm_name in db_norm or db_norm in norm_name)

    def _codes_match(self, norm_code: str, db_code: str) -> bool:
        """检查规范化后的编号是否匹配"""
        if not norm_code or not db_code:
            return False
        db_norm = normalize_for_matching(db_code).strip()
        return (norm_code in db_norm or db_norm in norm_code)

    def _fts_search(self, query: str, limit: int = 50):
        """FTS4 全文检索"""
        try:
            cur = self.conn.cursor()
            # FTS4 使用 MATCH 语法，escape 查询中的特殊字符
            safe_query = query.replace('"', '""')
            cur.execute(
                """
                SELECT s.id, s.code, s.name, s.status, s.publish_date, s.act_date,
                       s.source, s.std_type, s.is_eng
                FROM standards s
                WHERE s.id IN (
                    SELECT docid FROM standards_fts 
                    WHERE standards_fts MATCH ?
                )
                LIMIT ?
                """,
                (f'"{safe_query}"', limit)
            )
            return cur.fetchall()
        except sqlite3.OperationalError as e:
            # FTS4 might fail on special chars, fallback to LIKE
            print(f"  FTS4 fallback: {e}")
            cur = self.conn.cursor()
            cur.execute(
                """
                SELECT id, code, name, status, publish_date, act_date,
                       source, std_type, is_eng
                FROM standards
                WHERE name LIKE ? OR code LIKE ?
                LIMIT ?
                """,
                (f'%{query}%', f'%{query}%', limit)
            )
            return cur.fetchall()

    def find_similar_codes(self, code: str, limit: int = 5):
        """查找相似的规范编号（用于调试/推荐）"""
        norm_code = normalize_for_matching(code)
        raw_code = code.strip()
        results = []

        if not norm_code:
            return results

        # 从 code_index 中查找包含关系
        seen = set()
        for k, v in self.code_index.items():
            if norm_code in k or k in norm_code:
                if k not in seen:
                    seen.add(k)
                    results.append((k, v.get('code', ''), v.get('name', ''),
                                    v.get('status', ''), 'substring'))
            elif len(norm_code) > 3 and len(k) > 3:
                matches = 0
                n_idx = 0
                k_idx = 0
                while n_idx < len(norm_code) and k_idx < len(k):
                    if norm_code[n_idx] == k[k_idx]:
                        matches += 1
                        n_idx += 1
                        k_idx += 1
                    else:
                        k_idx += 1
                similarity = matches / max(len(norm_code), len(k))
                if similarity > 0.6:
                    if k not in seen:
                        seen.add(k)
                        results.append((k, v.get('code', ''), v.get('name', ''),
                                        v.get('status', ''), f'similar:{similarity:.2f}'))

        # 按匹配度排序
        def _sort_key(x):
            if x[4] == 'substring':
                return 1.0  # 子串匹配最高
            try:
                return float(x[4].split(':')[1])
            except (IndexError, ValueError):
                return 0.0
        results.sort(key=_sort_key, reverse=True)
        return results[:limit]

    def search_by_keyword(self, keyword: str, limit: int = 100):
        """通用关键词搜索（code + name），返回完整字段"""
        if not keyword or not keyword.strip():
            return []
        q = keyword.strip()
        try:
            cur = self.conn.cursor()
            cur.execute(
                """
                SELECT id, code, name, status, publish_date, act_date,
                       source, std_type, is_eng
                FROM standards
                WHERE code LIKE ? OR name LIKE ?
                ORDER BY is_eng DESC, LENGTH(code), code
                LIMIT ?
                """,
                (f'%{q}%', f'%{q}%', limit)
            )
            rows = cur.fetchall()
            return [dict(r) for r in rows]
        except Exception as e:
            print(f"search_by_keyword error: {e}")
            return []

    def search_by_year(self, year: int, limit: int = 500):
        """按年份搜索"""
        try:
            cur = self.conn.cursor()
            cur.execute(
                """
                SELECT id, code, name, status, publish_date, act_date,
                       source, std_type, is_eng
                FROM standards
                WHERE publish_date LIKE ? OR publish_date GLOB ?
                ORDER BY publish_date DESC
                LIMIT ?
                """,
                (f'{year}%', f'{year}-??-??', limit)
            )
            return [dict(r) for r in cur.fetchall()]
        except Exception as e:
            print(f"search_by_year error: {e}")
            return []

    def close(self):
        try:
            self.conn.close()
        except Exception:
            pass
