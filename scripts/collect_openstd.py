"""openstd.samr.gov.cn 采集器（国家标准全文公开系统 - 官方源）
- 强制性国标 GB: /bzgk/std/std_list_type?p.p1=1&p.p2=5  共约 288 条
- 推荐性国标 GB/T: /bzgk/std/std_list_type?p.p1=2&p.p2=5  共约 6995 条
- 国家标准化指导性技术文件: /bzgk/std/std_list_type?p.p1=3

每页 pageSize=10，可用 pageSize=50 提速。

用法：
  python collect_openstd.py mandatory    # 仅采强制性国标 GB (288条 ~3分钟)
  python collect_openstd.py recommend    # 采推荐性国标 GB/T (6995条 ~30分钟)
  python collect_openstd.py all          # 全采
"""
import sys, os, re, argparse, time, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import (BASE_DIR, DATA_DIR, LOG_DIR, FIELDS,
                    fetch, save_record, load_done_ids,
                    log_line, polite_sleep)

SITE = "https://openstd.samr.gov.cn"

# 类型映射
# p.p1=1 强制性、p.p1=2 推荐性、p.p1=3 国家标准化指导性技术文件
TYPE_MAP = {
    'mandatory': {'p1': '1', 'name': '强制性国标', 'prefix': 'openstd_mandatory'},
    'recommend': {'p1': '2', 'name': '推荐性国标', 'prefix': 'openstd_recommend'},
    'guide':     {'p1': '3', 'name': '国家标准化指导性技术文件', 'prefix': 'openstd_guide'},
}

PAGE_SIZE = 50

def fetch_page(p1, page, p2=5, retries=5):
    """抓取一页标准列表"""
    url = f'{SITE}/bzgk/std/std_list_type?r={random.random():.16f}&page={page}&pageSize={PAGE_SIZE}&p.p1={p1}&p.p2={p2}&p.p90=circulation_date&p.p91=desc'
    """抓取一页标准列表"""
    url = f'{SITE}/bzgk/std/std_list_type?r={random.random():.16f}&page={page}&pageSize={PAGE_SIZE}&p.p1={p1}&p.p2=5&p.p90=circulation_date&p.p91=desc'
    last_err = None
    for i in range(retries):
        try:
            html = fetch(url, timeout=30, retries=1, encoding='utf-8')
            return html
        except Exception as e:
            last_err = e
            time.sleep(2)
    raise last_err

def parse_page(html):
    """解析一页，返回记录列表
    结构：
      <tr><td>1</td><td><a onclick="showInfo('HASH')">GB 47955-2026</a></td>
      <td>...</td><td class="mytxt"><a onclick="showInfo('HASH')">智能网联汽车 ...</a></td>
      <td>...即将实施...</td><td>2026-06-27 00:00:00.0</td>
      <td>2027-01-01 00:00:00.0</td><td><button></td></tr>
    """
    # 每个 tr 含一个标准记录
    trs = re.findall(r'<tr>\s*<td>\d+</td>[\s\S]*?</tr>', html)
    records = []
    for tr in trs:
        # 提取 hashId（showInfo 参数）
        m_hash = re.search(r"showInfo\('([A-F0-9]+)'\)", tr)
        hash_id = m_hash.group(1) if m_hash else ''
        # 提取标准编号 - 第一个 <a onclick="showInfo('HASH')">GB XXXX</a>
        code = ''
        name = ''
        status = ''
        publish_date = ''
        implement_date = ''
        links = re.findall(r"showInfo\('[A-F0-9]+'\)[^>]*>([^<]+)</a>", tr)
        if len(links) >= 1:
            code = links[0].strip()
        if len(links) >= 2:
            name = links[1].strip()
        # 提取状态
        m = re.search(r'<span class="text-(?:warning|success|danger)"[^>]*>([^<]+)</span>', tr)
        status = m.group(1).strip() if m else ''
        if not status:
            m = re.search(r'>(现行|即将实施|废止|作废|已废止|确认有效)<', tr)
            status = m.group(1).strip() if m else ''
        # 提取日期 - 每条有 1 或 2 个 "YYYY-MM-DD"（无时间）
        publish_date = ''
        implement_date = ''
        dates = re.findall(r'(\d{4}-\d{2}-\d{2})', tr)
        if len(dates) >= 1:
            publish_date = dates[0]
            implement_date = dates[1] if len(dates) >= 2 else ''
        # 现行/作废
        si = ''
        if status:
            if '现行' in status or '即' in status or '确认' in status: si = '现行'
            elif '废止' in status or '作废' in status: si = '作废'

        if code:
            rec = {f: "" for f in FIELDS}
            rec['标准编号'] = code
            rec['标准名称'] = name
            rec['标准状态'] = status
            rec['现行或作废'] = si
            rec['发布日期'] = publish_date
            rec['实施日期'] = implement_date
            rec['标准简介'] = f'hashId={hash_id}'
            records.append(rec)
    return records, hash_id

def get_count(html):
    """从页面提取总数"""
    m = re.search(r"\bcount\s*=\s*(\d+)", html)
    if m: return int(m.group(1))
    return 0

def collect_type(type_key, p2_override=None):
    cfg = TYPE_MAP[type_key]
    p2 = p2_override if p2_override is not None else 5
    suffix = f'_p2_{p2}' if p2_override is not None else ''
    outfile = os.path.join(DATA_DIR, f'{cfg["prefix"]}{suffix}_standards.txt')
    logfile = os.path.join(LOG_DIR, f'{cfg["prefix"]}{suffix}.log')
    log_line(logfile, f'===== 开始采集 {cfg["name"]} p.p2={p2} =====')
    try:
        first_html = fetch_page(cfg['p1'], 1, p2)
        total = get_count(first_html)
        total_pages = (total + PAGE_SIZE - 1) // PAGE_SIZE
        log_line(logfile, f'总数={total} 总页数={total_pages} (pageSize={PAGE_SIZE})')
    except Exception as e:
        log_line(logfile, f'首页失败: {e}')
        return

    done = load_done_ids(outfile)
    log_line(logfile, f'已采 {len(done)} 条')
    ok = 0; fail = 0

    for p in range(1, total_pages + 1):
        try:
            html = first_html if p == 1 else fetch_page(cfg['p1'], p, p2)
            recs, _ = parse_page(html)
            for rec in recs:
                code = rec['标准编号']
                if code in done: continue
                save_record(outfile, rec)
                done.add(code)
                ok += 1
            if p % 5 == 0 or p == 1:
                log_line(logfile, f'页 {p}/{total_pages} 累计 ok={ok} fail={fail}')
            polite_sleep(0.5, 1.0)
        except Exception as e:
            fail += 1
            log_line(logfile, f'页 {p} 失败: {type(e).__name__}: {str(e)[:80]}')
            time.sleep(2)

    log_line(logfile, f'===== {cfg["name"]} p.p2={p2} 完成：成功 {ok} 失败 {fail} =====')

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['mandatory','recommend','guide','all','batch'])
    ap.add_argument('--p1', type=int, default=2)
    ap.add_argument('--p2', type=int, default=5)
    args = ap.parse_args()
    if args.mode == 'batch':
        # 批量模式：指定 p1 和 p2
        type_key = 'recommend' if args.p1 == 2 else 'guide' if args.p1 == 3 else 'mandatory'
        collect_type(type_key, p2_override=args.p2)
    elif args.mode == 'all':
        for k in ['mandatory','recommend','guide']:
            collect_type(k)
    else:
        collect_type(args.mode)

if __name__ == '__main__':
    main()
