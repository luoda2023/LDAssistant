"""csres.com（工标网）通用采集器
- 中标分类23大类：A B C D E F G H J K L M N P Q R S T U V X Y Z
- 行业分类：/sort/industry.jsp
- ICS分类：/sort/ics.jsp

用法：
  python collect_csres.py zhongbiao <字母或ALL>      # 中标分类
  python collect_csres.py industry                    # 行业分类
  python collect_csres.py ics                          # ICS分类
"""
import sys, os, re, argparse, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import (BASE_DIR, DATA_DIR, LOG_DIR, FIELDS,
                    fetch, parse_html, save_record, load_done_ids,
                    log_line, polite_sleep, get_ua)

SITE = "http://www.csres.com"

# 23个中标大类字母（无I、O、W）
ZHONGBIAO_LETTERS = ['A','B','C','D','E','F','G','H','J','K','L','M',
                     'N','P','Q','R','S','T','U','V','X','Y','Z']

def fetch_gbk(url, timeout=60, retries=5):
    """csres.com 用 GBK 编码、HTTP-only、连接不稳"""
    return fetch(url, timeout=timeout, retries=retries, encoding='gb18030')

def get_subcategories(letter):
    """从大类详情页 /sort/chsortdetail/{X}.html 提取子类链接 /sort/Chtype/{code}_1.html"""
    url = f"{SITE}/sort/chsortdetail/{letter}.html"
    html = fetch_gbk(url)
    subs = []
    # 形如 href="/sort/Chtype/A00_1.html" 后跟文本 "A00&nbsp;标准化、质量管理"
    for m in re.finditer(r'href="(/sort/Chtype/([A-Z]\w+?)_\d+\.html)"[^>]*>(.+?)</a>', html):
        path, code, name = m.group(1), m.group(2), m.group(3)
        name = re.sub(r'<[^>]+>', '', name).replace('&nbsp;', ' ').strip()
        subs.append((SITE + path, code, name))
    # 去重(用code)
    seen = set()
    uniq = []
    for s in subs:
        if s[1] not in seen:
            seen.add(s[1])
            uniq.append(s)
    return uniq

def get_total_pages_subcategory(sub_url):
    """从子类首页推断末页"""
    # 形如 /sort/Chtype/A00_1.html，分页为 A00_2.html
    html = fetch_gbk(sub_url)
    pages = re.findall(r'/sort/Chtype/(\w+)_(\d+)\.html', html)
    if pages:
        max_p = max(int(p[1]) for p in pages)
        return max_p
    # 备选：找 jsp?pagenum=
    pn = re.findall(r'pagenum=(\d+)', html)
    if pn:
        return max(int(p) for p in pn)
    return 1

def parse_subcategory_page(html):
    """从子类列表页提取标准详情链接"""
    items = []
    # csres 详情链接: /detail/{id}.html  形如 <a href="/detail/445319.html" target="_blank"><font color="#000000">GB/T 46961-2025</font></a>
    for m in re.finditer(r'href="(/detail/\d+\.html)"[^>]*>([^<]+)</a>', html):
        path = m.group(1)
        name = re.sub(r'<[^>]+>', '', m.group(2)).strip()
        if not name:
            continue
        if path.startswith('http'):
            url_full = path
        else:
            url_full = SITE + path
        items.append((url_full, name))
    return items

def parse_detail_page(html):
    """详情页解析 11 字段"""
    soup = parse_html(html)
    rec = {f: "" for f in FIELDS}

    # 工标网详情页结构需实际探查。先做通用解析。
    # 常见模式：<table>中含 "标准号：XXX" "标准名称：XXX" 等
    text = soup.get_text(separator='\n')
    lines = [l.strip() for l in text.split('\n') if l.strip()]

    for i, line in enumerate(lines):
        m = re.match(r'^标准名称[：:]\s*(.+)$', line)
        if m: rec['标准名称'] = m.group(1).strip()
        m = re.match(r'^标准编号[：:]\s*(.+)$', line)
        if m: rec['标准编号'] = m.group(1).strip()
        m = re.match(r'^标准号[：:]\s*(.+)$', line)
        if m and not rec['标准编号']: rec['标准编号'] = m.group(1).strip()
        m = re.match(r'^中文标准名称[：:]\s*(.+)$', line)
        if m: rec['标准名称'] = m.group(1).strip()
        m = re.match(r'^英文标准名称[：:]\s*(.+)$', line)
        if m and not rec['标准简介']:
            rec['标准简介'] = 'English: ' + m.group(1).strip()
        m = re.match(r'^标准简介[：:]\s*(.+)$', line)
        if m: rec['标准简介'] = m.group(1).strip()
        m = re.match(r'^范围[：:]\s*(.+)$', line)
        if m and not rec['标准简介']:
            rec['标准简介'] = m.group(1).strip()
        m = re.match(r'^状态[：:]\s*(.+)$', line)
        if m: rec['标准状态'] = m.group(1).strip()
        m = re.match(r'^标准状态[：:]\s*(.+)$', line)
        if m: rec['标准状态'] = m.group(1).strip()
        m = re.match(r'^现行[？?]\s*(.+)$', line)
        if m: rec['现行或作废'] = m.group(1).strip()
        m = re.match(r'^替代[关系情况]*[：:]\s*(.+)$', line)
        if m: rec['替代情况'] = m.group(1).strip()
        m = re.match(r'^被代替[：:]\s*(.+)$', line)
        if m and not rec['替代情况']: rec['替代情况'] = m.group(1).strip()
        m = re.match(r'^中标分类[：:]\s*(.+)$', line)
        if m: rec['中标分类'] = m.group(1).strip()
        m = re.match(r'^CCS[：:]\s*(.+)$', line)
        if m and not rec['中标分类']: rec['中标分类'] = m.group(1).strip()
        m = re.match(r'^ICS[分类]*[：:]\s*(.+)$', line)
        if m: rec['ICS分类'] = m.group(1).strip()
        m = re.match(r'^发布部门[：:]\s*(.+)$', line)
        if m: rec['发布部门'] = m.group(1).strip()
        m = re.match(r'^发布日期[：:]\s*(.+)$', line)
        if m: rec['发布日期'] = m.group(1).strip()
        m = re.match(r'^实施日期[：:]\s*(.+)$', line)
        if m: rec['实施日期'] = m.group(1).strip()
        m = re.match(r'^发布[：:]\s*(.+)$', line)
        if m and not rec['发布日期']:
            rec['发布日期'] = m.group(1).strip()
        m = re.match(r'^实施[：:]\s*(.+)$', line)
        if m and not rec['实施日期']:
            rec['实施日期'] = m.group(1).strip()
        m = re.match(r'^归口单位[：:]\s*(.+)$', line)
        if m and not rec['发布部门']:
            rec['发布部门'] = m.group(1).strip()
        m = re.match(r'^起草单位[：:]\s*(.+)$', line)
        if m and not rec['发布部门']:
            rec['发布部门'] = m.group(1).strip()

    state = rec['标准状态']
    if state:
        if '现行' in state: rec['现行或作废'] = '现行'
        elif '废止' in state or '作废' in state: rec['现行或作废'] = '作废'
        else: rec['现行或作废'] = state

    return rec

def collect_zhongbiao(letters):
    """采集中标分类一组字母的所有子类所有详情"""
    for letter in letters:
        outfile = os.path.join(DATA_DIR, f"csres_{letter}_standards.txt")
        logfile = os.path.join(LOG_DIR, f"csres_{letter}.log")
        log_line(logfile, f"===== 开始采集中标分类 {letter} =====")
        try:
            subs = get_subcategories(letter)
            log_line(logfile, f"子类数：{len(subs)}")
        except Exception as e:
            log_line(logfile, f"获取子类失败 {letter}: {e}")
            continue
        done = load_done_ids(outfile)
        log_line(logfile, f"已采 {len(done)} 条")
        ok = 0; fail = 0
        for sub_url, code, sub_name in subs:
            try:
                total_pages = get_total_pages_subcategory(sub_url)
                log_line(logfile, f"子类 {code} {sub_name} 总{total_pages}页")
                for p in range(1, total_pages + 1):
                    page_url = re.sub(r'_(\d+)\.html', f'_{p}.html', sub_url)
                    try:
                        phtml = fetch_gbk(page_url)
                        items = parse_subcategory_page(phtml)
                        for detail_url, detail_name in items:
                            if detail_name in done:
                                continue
                            try:
                                dhtml = fetch_gbk(detail_url)
                                rec = parse_detail_page(dhtml)
                                if not rec['标准名称']:
                                    rec['标准名称'] = detail_name
                                save_record(outfile, rec)
                                done.add(detail_name or detail_url)
                                ok += 1
                                if ok % 10 == 0:
                                    log_line(logfile, f"{letter}.{code} 已采 {ok} 条")
                                polite_sleep(1.0, 2.0)
                            except Exception as e:
                                fail += 1
                                log_line(logfile, f"详情失败 {detail_url}: {e}")
                                time.sleep(2)
                        polite_sleep(0.8, 1.5)
                    except Exception as e:
                        fail += 1
                        log_line(logfile, f"子类页失败 {page_url}: {e}")
                        time.sleep(3)
            except Exception as e:
                fail += 1
                log_line(logfile, f"子类失败 {code}: {e}")
        log_line(logfile, f"===== {letter} 完成：成功 {ok} 失败 {fail} =====")

def collect_industry():
    """采集行业分类"""
    url = f"{SITE}/sort/industry.jsp"
    outfile = os.path.join(DATA_DIR, "csres_industry_standards.txt")
    logfile = os.path.join(LOG_DIR, "csres_industry.log")
    log_line(logfile, f"===== 开始采集行业分类 =====")
    try:
        html = fetch_gbk(url)
        # 提取链接 /sort/industry/NNN_1.html
        cats = re.findall(r'href="(/sort/industry/\d+_\d+\.html)"[^>]*>(.+?)</a>', html)
        seen = set(); uniq = []
        for path, name in cats:
            full = SITE + path
            if full not in seen:
                seen.add(full); uniq.append((full, name))
        log_line(logfile, f"行业子类数：{len(uniq)}")
    except Exception as e:
        log_line(logfile, f"获取行业首页失败：{e}")
        return
    done = load_done_ids(outfile)
    ok = fail = 0
    for cat_url, cat_name in uniq:
        try:
            for p in range(1, 50):
                page_url = re.sub(r'_(\d+)\.html', f'_{p}.html', cat_url)
                try:
                    phtml = fetch_gbk(page_url)
                    items = parse_subcategory_page(phtml)
                    if not items and p > 1: break
                    for detail_url, detail_name in items:
                        if detail_name in done: continue
                        try:
                            dhtml = fetch_gbk(detail_url)
                            rec = parse_detail_page(dhtml)
                            if not rec['标准名称']: rec['标准名称'] = detail_name
                            save_record(outfile, rec)
                            done.add(detail_name or detail_url)
                            ok += 1
                            if ok % 10 == 0:
                                log_line(logfile, f"已采 {ok} 条")
                            polite_sleep(1.0, 2.0)
                        except Exception as e:
                            fail += 1; log_line(logfile, f"详情失败 {detail_url}: {e}"); time.sleep(2)
                    polite_sleep(0.8, 1.5)
                except Exception as e:
                    fail += 1; break
        except Exception as e:
            fail += 1; log_line(logfile, f"分类失败 {cat_name}: {e}")
    log_line(logfile, f"===== 行业完成：成功 {ok} 失败 {fail} =====")

def collect_ics():
    """采集 ICS 分类"""
    url = f"{SITE}/sort/ics.jsp"
    outfile = os.path.join(DATA_DIR, "csres_ics_standards.txt")
    logfile = os.path.join(LOG_DIR, "csres_ics.log")
    log_line(logfile, f"===== 开始采集 ICS 分类 =====")
    try:
        html = fetch_gbk(url)
        cats = re.findall(r'href="(/sort/icsdetail/\d+\.html)"[^>]*>(.+?)</a>', html)
        seen = set(); uniq = []
        for path, name in cats:
            full = SITE + path
            if full not in seen:
                seen.add(full); uniq.append((full, name))
        log_line(logfile, f"ICS 子类数：{len(uniq)}")
    except Exception as e:
        log_line(logfile, f"获取 ICS 首页失败：{e}")
        return
    done = load_done_ids(outfile)
    ok = fail = 0
    for cat_url, cat_name in uniq:
        try:
            html = fetch_gbk(cat_url)
            sub_cats = re.findall(r'href="(/sort/ics(?:detail|Chtype)/[^"]+)"', html)
            seen = set(); uniq2 = []
            for s in sub_cats:
                full = SITE + s
                if full not in seen:
                    seen.add(full); uniq2.append(full)
            for sub_url in uniq2:
                for p in range(1, 50):
                    page_url = re.sub(r'(_\d+)\.html', f'_{p}.html', sub_url) if '_' in sub_url else sub_url
                    try:
                        phtml = fetch_gbk(page_url)
                        items = parse_subcategory_page(phtml)
                        if not items and p > 1: break
                        for detail_url, detail_name in items:
                            if detail_name in done: continue
                            try:
                                dhtml = fetch_gbk(detail_url)
                                rec = parse_detail_page(dhtml)
                                if not rec['标准名称']: rec['标准名称'] = detail_name
                                save_record(outfile, rec)
                                done.add(detail_name or detail_url)
                                ok += 1
                                if ok % 10 == 0:
                                    log_line(logfile, f"已采 {ok} 条")
                                polite_sleep(1.0, 2.0)
                            except Exception as e:
                                fail += 1; log_line(logfile, f"详情失败: {e}"); time.sleep(2)
                        polite_sleep(0.8, 1.5)
                    except Exception as e:
                        fail += 1; break
        except Exception as e:
            fail += 1; log_line(logfile, f"失败 {cat_name}: {e}")
    log_line(logfile, f"===== ICS 完成：成功 {ok} 失败 {fail} =====")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['zhongbiao', 'industry', 'ics'])
    ap.add_argument('arg', nargs='?', default='ALL')
    args = ap.parse_args()
    if args.mode == 'zhongbiao':
        if args.arg == 'ALL':
            letters = ZHONGBIAO_LETTERS
        else:
            letters = list(args.arg)
        collect_zhongbiao(letters)
    elif args.mode == 'industry':
        collect_industry()
    elif args.mode == 'ics':
        collect_ics()

if __name__ == '__main__':
    main()
