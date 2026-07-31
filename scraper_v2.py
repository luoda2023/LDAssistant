#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
工程建设标准采集脚本 v2
从 www.csres.com 抓取所有标准列表页面
直接从每页 HTML 的 title 属性提取完整字段，无需访问详情页
"""
import urllib.request, urllib.parse, sqlite3, re, json, os, sys, time, random, signal
import concurrent.futures
from html import unescape

signal.signal(signal.SIGINT, lambda s, f: sys.exit(0))

# ==== 配置 ====
TOTAL_PAGES = 15052
URL = 'http://www.csres.com/s.jsp?pageNum={}'
WORKERS = 8  # 控制在8并发,避免被csres.com限流
# 请求间最小间隔
import threading
_LAST_REQ_TIME = {}
_REQ_LOCK = threading.Lock()
DB = 'standards.db'
PROG = 'progress.json'
HEADERS = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0',
    'Accept': 'text/html,application/xhtml+xml',
    'Accept-Language': 'zh-CN,zh;q=0.9',
    'Referer': 'http://www.csres.com/'
}

# title 属性的正则
TITLE_RE = re.compile(r'title="([^"]+)"', re.DOTALL)
# HREF 正则
HREF_RE = re.compile(r'href="(/detail/\d+\.html)"', re.DOTALL)
# 字段正则
FIELD_RE = re.compile(r'([^\xA0]+?)[:：]\s*([^\xA0]*?)(?:\xA0|$)')

def parse_title_attr(title_str):
    """解析 title 属性字符串到字段字典
    title 格式: 编号：GB/T 8947-1998 &#xA;标题：复合塑料编织袋&#xA;...
    """
    raw = unescape(title_str)
    fields = {}
    # 直接按 &#xA; 切分
    for part in raw.split('\n'):
        part = part.strip()
        if not part:
            continue
        # 找全角冒号
        if '\uFF1A' in part:
            k, v = part.split('\uFF1A', 1)
            fields[k.strip()] = v.strip()
        elif ':' in part.replace('&#',':'):
            k, v = part.split(':', 1)
            fields[k.strip()] = v.strip()
    return fields

def fetch_page(pg):
    """抓取一页，返回 (records, error)"""
    html = None
    for attempt in range(5):  # 5次重试
        try:
            req = urllib.request.Request(URL.format(pg), headers=HEADERS)
            with urllib.request.urlopen(req, timeout=60) as r:  # 60秒超时
                html = r.read().decode('gbk', 'replace')
            break
        except Exception as e:
            if attempt < 4:
                time.sleep(random.uniform(0.5, 2))  # 短间隔重试
            else:
                return [], f'Failed: {e}'

    if not html:
        return [], 'Empty response'

    # 找所有带 title 属性的元素（包含 detail 链接）
    records = []

    # 遍历所有mClk调用来定位标准项（每个标准项都有 onclick="mClk(id);")
    # 找到 tr title="编号..." 后用后面最近的a href提取URL
    # 同时从tr提取页面上的字体颜色状态
    for m in re.finditer(r'onclick="mClk\((\d+)\);"[^>]*\s+title="([^"]+)"', html):
        item_id = m.group(1)
        title_raw = m.group(2)
        fields = parse_title_attr(title_raw)
        if '编号' not in fields:
            continue

        # 找紧随其后的 detail URL
        after_mClk = html[m.end():m.end()+1000]
        href_match = re.search(r'href="(/detail/\d+\.html)"', after_mClk)
        url = 'http://www.csres.com' + href_match.group(1) if href_match else ''

        # 从标题后的区域取状态(表格中的第5个td)
        td_status = re.search(r'<td[^>]*>(.*?)</td>\s*<td[^>]*>'  # 第4个发布日期
                              r'(.*?)</td>\s*<td[^>]*>(.*?)</td>'  # 状态（第5个）
                              r'\s*</tr>', after_mClk, re.DOTALL)
        status = ''
        if td_status:
            status = re.sub(r'<[^>]+>', '', td_status.group(3)).strip()

        # 说明status：页面上显示 "现行"/"作废"/"废止"等
        # replace title中的替代情况来判断是否废止
        replacement = fields.get('替代情况', '')
        if not status:
            status = '废止' if ('废止' in replacement or '代替' in replacement) else '现行'

        record = {
            'code': fields.get('编号', ''),
            'name': fields.get('标题', ''),
            'en_name': fields.get('英文标题', ''),
            'ccs': fields.get('分类', ''),
            'ics': fields.get('ICS', ''),
            'adopt': fields.get('采标情况', ''),
            'replacement': replacement,
            'department': fields.get('发布部门', ''),
            'publish_date': fields.get('发布日期', ''),
            'implement_date': fields.get('实施日期', ''),
            'status': status,
            'detail_url': url,
        }
        # 清理 status 中可能混入的多余内容
        if record['status']:
            # 只保留 现行/作废/废止/即将实施 等状态词
            m_status = re.search(r'(现行|作废|废止|即将实施|替代)', record['status'])
            if m_status:
                record['status'] = m_status.group(1)
        if record['code']:
            records.append(record)

    return records, None

def init_db():
    """初始化数据库"""
    conn = sqlite3.connect(DB, timeout=300)
    # 启用WAL模式,允许多线程并发读写
    conn.execute('PRAGMA journal_mode=WAL')
    conn.execute('PRAGMA synchronous=NORMAL')  # 平衡性能和数据安全
    conn.execute('PRAGMA busy_timeout=300000')  # 5分钟busy等待
    conn.execute('PRAGMA cache_size=-64000')  # 64MB缓存
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
            source_type TEXT DEFAULT 'csres',
            hcno TEXT,
            replacement TEXT,
            adopt TEXT,
            detail_url TEXT UNIQUE
        )
    ''')
    conn.execute('CREATE INDEX IF NOT EXISTS idx_code ON standards(code)')
    conn.execute('CREATE INDEX IF NOT EXISTS idx_name ON standards(name)')
    return conn

def main():
    conn = init_db()

    # 读取已完成页
    done = set()
    if os.path.exists(PROG):
        try:
            done = set(json.load(open(PROG)))
        except:
            pass

    # 已存在的 detail_url 不重复抓
    exist_urls = set()
    for r in conn.execute('SELECT detail_url FROM standards WHERE detail_url IS NOT NULL'):
        exist_urls.add(r[0])

    remain = [p for p in range(1, TOTAL_PAGES + 1) if p not in done]
    print(f'总页数:{TOTAL_PAGES} 已抓:{len(done)} DB中已有:{len(exist_urls)} 待抓:{len(remain)}', flush=True)

    if not remain:
        print('全部完成!', flush=True)
        cnt = conn.execute('SELECT COUNT(*) FROM standards').fetchone()[0]
        print(f'数据库总记录数: {cnt}')
        conn.close()
        return

    start = time.time()
    total_new = 0
    pages_done = 0
    failed_pages = []

    with concurrent.futures.ThreadPoolExecutor(WORKERS) as ex:
        futures = {ex.submit(fetch_page, p): p for p in remain}
        for fut in concurrent.futures.as_completed(futures):
            p = futures[fut]
            pages_done += 1
            try:
                records, err = fut.result()
            except Exception as e:
                err = str(e)
                records = []

            if err:
                if pages_done % 100 == 0:
                    print(f'FAIL p{p}: {err}', flush=True)
                failed_pages.append(p)
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
                              r['adopt'], r['detail_url'], 'csres'))
                        new_count += 1
                    except Exception as e:
                        pass

            total_new += new_count
            elapsed = time.time() - start
            rate = pages_done / elapsed if elapsed > 0 else 0
            eta = (len(remain) - pages_done) / rate if rate > 0 else 0

            # 每完成50页或每200新记录提交一次
            if pages_done % 50 == 0 or pages_done == len(remain):
                conn.commit()
                json.dump(list(done), open(PROG, 'w'))

            if pages_done % 20 == 0 or pages_done == len(remain):
                print(f'[{pages_done}/{len(remain)}] p{p} +{new_count}(总新增{total_new}) '
                      f'{rate:.1f}pg/s ETA:{eta:.0f}s', flush=True)

    conn.commit()
    json.dump(list(done), open(PROG, 'w'))

    cnt = conn.execute('SELECT COUNT(*) FROM standards').fetchone()[0]
    print(f'\n✅ 完成! 抓取 {pages_done} 页, 新增 {total_new} 条, 失败 {len(failed_pages)} 页')
    print(f'数据库总记录: {cnt}')
    print(f'耗时: {time.time()-start:.0f} 秒')
    if failed_pages:
        print(f'失败页(前20): {failed_pages[:20]}')
    conn.close()

if __name__ == '__main__':
    main()
