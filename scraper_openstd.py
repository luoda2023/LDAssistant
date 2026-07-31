#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
国家标准全文公开系统(openstd.samr.gov.cn)采集脚本
从官方国家标准信息服务平台抓取所有国家标准
"""
import urllib.request, urllib.parse, sqlite3, re, json, os, sys, time, random, signal
import concurrent.futures
from html import unescape

signal.signal(signal.SIGINT, lambda s, f: sys.exit(0))

# ==== 配置 ====
URL = 'https://openstd.samr.gov.cn/bzgk/gb/std_list'
WORKERS = 5  # 5线程并发,避免被限流
DB = 'standards.db'
PROG = 'progress_openstd.json'
HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'zh-CN,zh;q=0.9',
    'Referer': 'https://openstd.samr.gov.cn/'
}

PAGE_SIZE = 100
# openstd 国标共约 69946 条 (现行50110 + 即将实施2128 + 废止17708)
TOTAL_RECORDS = 70000
TOTAL_PAGES = (TOTAL_RECORDS + PAGE_SIZE - 1) // PAGE_SIZE  # 700页

# 字段名映射
TITLE_RE = re.compile(r'title="([^"]+)"', re.DOTALL)
HREF_RE = re.compile(r'href="(/bzgk/gb/std_detail[^"]+)"', re.DOTALL)

def parse_openstd_html(html):
    """解析openstd的标准列表HTML,返回记录列表"""
    records = []
    trs = re.findall(r'<tr[^>]*>(.*?)</tr>', html, re.DOTALL)
    for tr in trs:
        if 'showInfo' not in tr:
            continue
        show_match = re.search(r"showInfo\('([A-F0-9]+)'\)", tr)
        if not show_match:
            continue

        # 找所有<a>标签文本
        all_a = re.findall(r'<a[^>]*>(.*?)</a>', tr, re.DOTALL)
        a_texts = [re.sub(r'<[^>]+>|\s+', '', a).strip() for a in all_a if re.sub(r'<[^>]+>|\s+', '', a).strip()]
        if not a_texts:
            continue

        code = a_texts[0] if a_texts else ''
        name = a_texts[1] if len(a_texts) > 1 else ''

        # td内容
        tds = re.findall(r'<td[^>]*>(.*?)</td>', tr, re.DOTALL)
        cells = []
        for td in tds:
            text = re.sub(r'<[^>]+>', '', td)
            text = re.sub(r'\s+', '', text).strip()
            cells.append(text)

        status = cells[6] if len(cells) > 6 else ''
        publish_date = cells[7][:10] if len(cells) > 7 else ''
        implement_date = cells[8][:10] if len(cells) > 8 else ''

        # 必须以 GB 开头
        if not code or not code.startswith('GB'):
            continue
        # 跳过表头
        if '标准号' in code or '序号' in code:
            continue

        # 规范化code: GB2024-2016 -> GB 2024-2016, GBT12345-2018 -> GB/T 12345-2018
        code_normalized = code
        if code_normalized.startswith('GBT'):
            code_normalized = 'GB/T ' + code_normalized[3:]
        elif code_normalized.startswith('GB'):
            code_normalized = 'GB ' + code_normalized[2:]

        record = {
            'code': code_normalized,
            'name': name,
            'en_name': '',
            'status': status or '现行',
            'ics': '',
            'ccs': '',
            'publish_date': publish_date.replace('/', '-'),
            'implement_date': implement_date.replace('/', '-'),
            'department': '国家市场监督管理总局',
            'replacement': '',
            'adopt': '',
            'detail_url': 'https://openstd.samr.gov.cn/bzgk/gb/std_detail?id=' + show_match.group(1),
        }
        records.append(record)

    return records

def fetch_page(pg):
    """抓取一页, 返回(records, error)"""
    # openstd 使用 page 参数，总页数约 69946/100 ≈ 700 页
    url = URL + '?page=' + str(pg) + '&pageSize=' + str(PAGE_SIZE)
    for attempt in range(5):
        try:
            req = urllib.request.Request(url, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=30) as r:
                html = r.read().decode('utf-8', errors='replace')
            records = parse_openstd_html(html)
            return records, None
        except Exception as e:
            if attempt < 4:
                time.sleep(random.uniform(1, 3))
            else:
                return [], f'Failed: {e}'
    return [], 'No response'

def init_db():
    conn = sqlite3.connect(DB, timeout=300)
    conn.execute('PRAGMA journal_mode=WAL')
    conn.execute('PRAGMA busy_timeout=300000')
    conn.execute('''
        CREATE TABLE IF NOT EXISTS standards (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            code TEXT,
            name TEXT,
            en_name TEXT,
            status TEXT,
            ics TEXT,
            ccs TEXT,
            publish_date TEXT,
            implement_date TEXT,
            department TEXT,
            manager TEXT,
            issuer TEXT,
            source_type TEXT DEFAULT 'openstd',
            hcno TEXT,
            replacement TEXT,
            adopt TEXT,
            detail_url TEXT UNIQUE
        )
    ''')
    conn.execute('CREATE INDEX IF NOT EXISTS idx_code ON standards(code)')
    conn.execute('CREATE INDEX IF NOT EXISTS idx_name ON standards(name)')
    conn.commit()
    return conn

def main():
    conn = init_db()
    # 已完成
    done = set()
    if os.path.exists(PROG):
        try: done = set(json.load(open(PROG)))
        except: pass

    # 已有url
    exist_urls = set()
    for r in conn.execute("SELECT detail_url FROM standards WHERE source_type='openstd'"):
        exist_urls.add(r[0])

    remain = [p for p in range(1, TOTAL_PAGES + 1) if p not in done]
    print(f'openstd采集 总页数:{TOTAL_PAGES} 已抓:{len(done)} DB中已有:{len(exist_urls)} 待抓:{len(remain)}', flush=True)

    if not remain:
        print('全部完成!')
        cnt = conn.execute("SELECT COUNT(*) FROM standards WHERE source_type='openstd'").fetchone()[0]
        print(f'openstd记录: {cnt}')
        conn.close()
        return

    start = time.time()
    pages_done = 0
    total_new = 0
    failed_pages = []

    with concurrent.futures.ThreadPoolExecutor(WORKERS) as ex:
        futures = {ex.submit(fetch_page, p): p for p in remain}
        for fut in concurrent.futures.as_completed(futures):
            p = futures[fut]
            pages_done += 1
            try:
                records, err = fut.result()
            except Exception as e:
                err = str(e); records = []
            if err:
                failed_pages.append(p)
                print(f'FAIL p{p}: {err}', flush=True)
                continue

            done.add(p)
            new_count = 0
            for r in records:
                if r['detail_url'] and r['detail_url'] not in exist_urls:
                    exist_urls.add(r['detail_url'])
                    try:
                        conn.execute('''
                            INSERT OR IGNORE INTO standards
                            (code, name, en_name, status, ics, ccs, publish_date,
                             implement_date, department, replacement, adopt, detail_url, source_type)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        ''', (r['code'], r['name'], r['en_name'], r['status'],
                              r['ics'], r['ccs'], r['publish_date'],
                              r['implement_date'], r['department'], r['replacement'],
                              r['adopt'], r['detail_url'], 'openstd'))
                        new_count += 1
                    except:
                        pass
            total_new += new_count
            conn.commit()
            if pages_done % 20:
                json.dump(list(done), open(PROG,'w'))
            if pages_done % 5 == 0 or pages_done == len(remain):
                el = time.time() - start
                rate = pages_done / el if el > 0 else 0
                eta = (len(remain) - pages_done) / rate if rate > 0 else 0
                print(f'[{pages_done}/{len(remain)}] p{p} +{new_count}(总{total_new}) {rate:.1f}pg/s ETA:{eta:.0f}s', flush=True)

    conn.commit()
    json.dump(list(done), open(PROG,'w'))
    cnt = conn.execute("SELECT COUNT(*) FROM standards WHERE source_type='openstd'").fetchone()[0]
    total = conn.execute("SELECT COUNT(*) FROM standards").fetchone()[0]
    print(f'\n✅ 完成! 抓取{pages_done}页 新增{total_new}条 失败{len(failed_pages)}页')
    print(f'openstd记录: {cnt}, 总记录: {total}')
    conn.close()

if __name__ == '__main__':
    main()
