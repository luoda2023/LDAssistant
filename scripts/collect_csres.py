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
    # csres 页面中 a 标签形如: <a href="/sort/Chtype/A00_1.html" class="sh14lian">A00&nbsp;标准化、质量管理<font color=red>[215]</font></href>
    # 需使用 re.DOTALL 让 .+? 跨行（虽实际不跨行，但确保捕获）
    for m in re.finditer(r'href="(/sort/Chtype/(\w+?)_1\.html)"[^>]*class="[^"]*sh14lian[^"]*"[^>]*>([\s\S]+?)</a>', html, re.IGNORECASE | re.DOTALL):
        path, code, raw_name = m.group(1), m.group(2), m.group(3)
        # 清理名
        name = re.sub(r'<[^>]+>', '', raw_name).replace('&nbsp;', ' ').strip()
        if not code or code == '_':
            continue
        subs.append((SITE + path, code, name))
    # 去重(用code)
    seen = set()
    uniq = []
    for s in subs:
        if s[1] not in seen:
            seen.add(s[1])
            uniq.append(s)
    print(f"大类 {letter}: 提取到 {len(uniq)} 个子类")
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
    """详情页解析 11 字段 - csres.com 格式
    csres详情页主要从meta description提取信息：
    《GB/T 46961-2025 专利密集型产品评价方法》本文件规定了...本文件适用于...状态：现行
    """
    rec = {f: "" for f in FIELDS}

    # meta description - csres 主要数据源
    m_desc = re.search(r'<meta\s+name="description"\s+content="([^"]+)"', html, re.IGNORECASE)
    if m_desc:
        desc = m_desc.group(1)
        # 提取标准编号和名称：《GB/T 46961-2025 专利密集型产品评价方法》...
        # 编号格式如：GB/T 46961-2025、GB 28381-2026、DB11/T 3047-2025
        m = re.match(r'《((?:[A-Z]{1,5}/[A-Z]?\s*|[A-Z]{1,5}\s*)?\d+(?:\.\d+)?-\d{4})\s+(.+?)》', desc)
        if m:
            rec['标准编号'] = re.sub(r'\s+', ' ', m.group(1).strip())
            rec['标准名称'] = m.group(2).strip()
        else:
            m = re.match(r'《([^》]+)》', desc)
            if m:
                inner = m.group(1).strip()
                # 用正则拆：标准编号 + 中文名
                m2 = re.match(r'^([\w/\.\-]+\s+\d+(?:\.\d+)?-\d{4})\s+(.+)$', inner)
                if m2:
                    rec['标准编号'] = m2.group(1).strip()
                    rec['标准名称'] = m2.group(2).strip()
                else:
                    rec['标准名称'] = inner
        # 提取简介：《XX》后面到 状态： 之前的内容
        m = re.search(r'》(.+?)(?:\s+状态[：:]|\s*$)', desc, re.DOTALL)
        if m:
            intro = m.group(1).strip()
            if intro:
                rec['标准简介'] = intro[:500]
        # 状态
        m = re.search(r'状态[：:]\s*([^\s。]+)', desc)
        if m:
            rec['标准状态'] = m.group(1).strip()

    # title 备用：GB/T 46961-2025 专利密集型产品评价方法 -工标网
    if not rec['标准编号'] or not rec['标准名称']:
        m_t = re.search(r'<title>(.+?)\s*-\s*工标网</title>', html)
        if m_t:
            title_part = m_t.group(1).strip()
            m = re.match(r'^(\S+)\s+(.+)$', title_part)
            if m:
                if not rec['标准编号']: rec['标准编号'] = m.group(1).strip()
                if not rec['标准名称']: rec['标准名称'] = m.group(2).strip()

    # meta keywords 可能含其他变体标准号
    m_kw = re.search(r'<meta\s+name="keywords"\s+content="([^"]+)"', html, re.IGNORECASE)
    if m_kw:
        kw = m_kw.group(1)
        # 提取拼音/英文别名
        if not rec['标准编号']:
            m = re.search(r'([a-z]{1,5}[-/t]*\d+(?:\.\d+)?-\d{2,4})', kw, re.IGNORECASE)
            if m:
                rec['标准编号'] = m.group(1).strip()

    # 从 table 字段提取（备用）
    for m in re.finditer(r'(标准编号|标准名称|标准状态|中标分类|CCS|ICS分类|ICS分类号|发布部门|发布日期|实施日期|代替标准|替代情况|归口单位|起草单位|范围)[：:]\s*</?(?:td|span|div|p)[^>]*>\s*([^<\n]+)', html, re.IGNORECASE):
        k, v = m.group(1), m.group(2).strip()
        v = re.sub(r'&nbsp;', ' ', v).strip()
        if '标准编号' in k and not rec['标准编号']: rec['标准编号'] = v
        elif '标准名称' in k and not rec['标准名称']: rec['标准名称'] = v
        elif '标准状态' in k and not rec['标准状态']: rec['标准状态'] = v
        elif '替代' in k and not rec['替代情况']: rec['替代情况'] = v
        elif '中标分类' in k and not rec['中标分类']: rec['中标分类'] = v
        elif 'CCS' in k and not rec['中标分类']: rec['中标分类'] = v
        elif 'ICS' in k and not rec['ICS分类']: rec['ICS分类'] = v
        elif '发布部门' in k and not rec['发布部门']: rec['发布部门'] = v
        elif '发布日期' in k and not rec['发布日期']: rec['发布日期'] = v
        elif '实施日期' in k and not rec['实施日期']: rec['实施日期'] = v

    # 现行或作废
    state = rec['标准状态']
    if state:
        if '现行' in state: rec['现行或作废'] = '现行'
        elif '废止' in state or '作废' in state: rec['现行或作废'] = '作废'
        elif '即将实施' in state: rec['现行或作废'] = '现行'
        elif '被代替' in state: rec['现行或作废'] = '被代替'
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
