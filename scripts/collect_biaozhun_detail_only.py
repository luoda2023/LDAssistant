"""biaozhun.org 新版：先保存列表到JSON，再单独采集详情
用法：
  python collect_biaozhun_detail_only.py list <category>     # 只采集列表页到JSON
  python collect_biaozhun_detail_only.py detail <category>   # 从JSON采集详情
  python collect_biaozhun_detail_only.py all <category>      # 全流程（可能被kill）
"""
import sys, os, json, re, argparse, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import (BASE_DIR, DATA_DIR, LOG_DIR, FIELDS,
                    fetch, parse_html, save_record, load_done_ids,
                    log_line, polite_sleep, get_ua)

SITE = "https://www.biaozhun.org"

def get_pagination_info(category):
    url = f"{SITE}/{category}/"
    html = fetch(url, timeout=30)
    m = re.search(r"href=['\"]list-(\d+)-(\d+)\.html['\"][^>]*>末页", html)
    if m:
        return int(m.group(1)), int(m.group(2))
    all_pages = re.findall(r"list-(\d+)-(\d+)\.html", html)
    if all_pages:
        from collections import Counter
        prefix_count = Counter(p[0] for p in all_pages)
        prefix = int(prefix_count.most_common(1)[0][0])
        max_page = max(int(p[1]) for p in all_pages if int(p[0]) == prefix)
        return prefix, max_page
    return 1, 1

def collect_list_to_json(category):
    """采集列表页，保存为JSON文件"""
    prefix, total_pages = get_pagination_info(category)
    jsonfile = os.path.join(DATA_DIR, f"biaozhun_{category}_list.json")
    logfile = os.path.join(LOG_DIR, f"biaozhun_{category}_list.log")
    
    all_items = []
    for p in range(1, total_pages + 1):
        url = f"{SITE}/{category}/list-{prefix}-{p}.html"
        try:
            html = fetch(url, timeout=30)
            items = []
            for m in re.finditer(r'<a\s+href="(/%s/(\d+\.html))"[^>]*title="([^"]*)"' % category, html):
                url_path = m.group(1)
                title = m.group(3).strip()
                if not title: continue
                parts = title.split(' ', 1)
                code, name = (parts[0], parts[1]) if len(parts)==2 else ("", title)
                items.append({"url": SITE + url_path, "code": code, "name": name})
            if not items:
                # 尝试无title属性匹配
                for m in re.finditer(r'<a\s+href="(/%s/(\d+\.html))"[^>]*>([^<]+)</a>' % category, html):
                    url_path = m.group(1)
                    title = m.group(3).strip()
                    if not title or len(title)<4: continue
                    parts = title.split(' ', 1)
                    code, name = (parts[0], parts[1]) if len(parts)==2 else ("", title)
                    items.append({"url": SITE + url_path, "code": code, "name": name})
            all_items.extend(items)
            log_line(logfile, f"列表页 {p}/{total_pages} OK，提取 {len(items)} 条")
            polite_sleep()
        except Exception as e:
            log_line(logfile, f"列表页 {p} 失败: {e}")
            time.sleep(2)
    
    # 去重
    seen = set()
    unique = []
    for item in all_items:
        if item["url"] not in seen:
            seen.add(item["url"])
            unique.append(item)
    
    with open(jsonfile, 'w', encoding='utf-8') as f:
        json.dump(unique, f, ensure_ascii=False, indent=2)
    
    log_line(logfile, f"列表采集完成：{len(unique)} 条 -> {jsonfile}")
    return jsonfile, unique

def collect_detail_from_json(category, start_from=0):
    """从JSON文件采集详情"""
    jsonfile = os.path.join(DATA_DIR, f"biaozhun_{category}_list.json")
    outfile = os.path.join(DATA_DIR, f"biaozhun_{category}_standards.txt")
    logfile = os.path.join(LOG_DIR, f"biaozhun_{category}_detail.log")
    
    if not os.path.exists(jsonfile):
        log_line(logfile, f"JSON文件不存在: {jsonfile}")
        return
    
    with open(jsonfile, 'r', encoding='utf-8') as f:
        items = json.load(f)
    
    done_codes = load_done_ids(outfile)
    log_line(logfile, f"从 {jsonfile} 加载 {len(items)} 条候选，已采 {len(done_codes)} 条")
    
    ok = 0; fail = 0; skipped = 0
    start_ts = time.time()
    # 从指定位置开始
    for i, item in enumerate(items):
        if i < start_from:
            continue
        code = item.get("code", "")
        url = item["url"]
        name = item.get("name", "")
        
        if code and code in done_codes:
            skipped += 1
            continue
        
        try:
            html = fetch(url, timeout=30)
            soup = parse_html(html)
            rec = {f: "" for f in FIELDS}
            
            # h1 编号
            h1 = soup.find('h1')
            if h1:
                full = re.sub(r'\s+', ' ', h1.get_text(strip=True)).strip()
                m = re.match(r'^([A-Za-z]{1,5}(?:/[A-Za-z])?(?:\s*[（(]?[^)）\s]*[)）])?\s*\d+(?:\.\d+)*(?:-\d{4})?)\s+(.+)$', full)
                if m:
                    rec['标准编号'] = m.group(1).replace(' ',' ')
                    rec['标准名称'] = m.group(2).strip()
                else:
                    parts = full.split(' ', 1)
                    if len(parts)==2:
                        rec['标准编号'] = parts[0]
                        rec['标准名称'] = parts[1]
                    else:
                        rec['标准名称'] = full
            
            # content
            content = soup.find('div', class_='content')
            if content:
                for li in content.find_all('li'):
                    txt = li.get_text(strip=True)
                    m = re.match(r'^标准名称：(.+)$', txt)
                    if m and not rec['标准名称']: rec['标准名称'] = m.group(1).strip()
                    m = re.match(r'^代替标准[：:](.*)$', txt)
                    if m: rec['替代情况'] = m.group(1).strip()
                    m = re.match(r'^标准分类[：:](.+)$', txt)
                    if m: rec['中标分类'] = m.group(1).strip()
                for li in content.find_all('li'):
                    for dd in li.find_all('dd', recursive=False):
                        spans = dd.find_all('span', recursive=False)
                        if spans:
                            span = spans[0]
                            rest = ''.join(str(s) for s in span.next_siblings)
                            value = re.sub(r'(?:主管部门|技术归口)[：:].*$', '', rest).strip()
                            label = span.get_text(strip=True).rstrip('：:')
                            if label == '标准号': rec['标准编号'] = value
                            elif label == '发布日期': rec['发布日期'] = value
                            elif label == '实施日期': rec['实施日期'] = value
                            elif label == '代替标准': rec['替代情况'] = value
                    for dt in li.find_all('dt', recursive=False):
                        spans = dt.find_all('span', recursive=False)
                        if spans:
                            span = spans[0]
                            rest = ''.join(str(s) for s in span.next_siblings)
                            value = rest.strip()
                            label = span.get_text(strip=True).rstrip('：:')
                            if label == '中国标准分类号': rec['中标分类'] = value
                            elif label == '国际标准分类号': rec['ICS分类'] = value
                            elif label in ('技术归口', '主管部门'): rec['发布部门'] = value
            
            # 状态
            s = soup.find(id='s-status')
            if s: rec['标准状态'] = s.get('data-zt', '').strip()
            if not rec['标准状态']:
                m = re.search(r'class=["\']state["\']>([^<]+)<', html)
                if m: rec['标准状态'] = m.group(1).strip()
            
            state = rec['标准状态']
            if state:
                if '现行' in state: rec['现行或作废'] = '现行'
                elif '废止' in state or '作废' in state: rec['现行或作废'] = '作废'
                elif '即将实施' in state: rec['现行或作废'] = '现行'
                elif '被代替' in state: rec['现行或作废'] = '被代替'
                else: rec['现行或作废'] = state
            
            # 简介
            for info in soup.find_all('div', class_='info'):
                h3 = info.find('h3')
                if h3 and '内容简介' in h3.get_text(strip=True):
                    p = info.find('p')
                    if p: rec['标准简介'] = p.get_text(strip=True)
                    break
            
            if not rec['标准名称']: rec['标准名称'] = name
            if not rec['标准编号']: rec['标准编号'] = code
            
            save_record(outfile, rec)
            ok += 1
            # 存入完整编号（不存前缀"GB"），避免后续全被跳过
            full_code = rec['标准编号'] or code
            if full_code and full_code not in ("GB", "GBZ"):
                done_codes.add(full_code)
            if ok % 10 == 0:
                log_line(logfile, f"已采 {ok}/{len(items)} [{i}]: {rec['标准编号']}")
            polite_sleep(0.6, 1.2)
        except Exception as e:
            fail += 1
            if fail <= 5:
                log_line(logfile, f"失败 [{fail}] {url}: {type(e).__name__}: {e}")
            time.sleep(1.5)
    
    elapsed = time.time() - start_ts
    log_line(logfile, f"===== 完成：成功 {ok} 失败 {fail} 跳过 {skipped} 耗时 {elapsed:.1f}s =====")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['list', 'detail', 'all'])
    ap.add_argument('category')
    ap.add_argument('--start', type=int, default=0)
    args = ap.parse_args()
    
    if args.mode == 'list':
        collect_list_to_json(args.category)
    elif args.mode == 'detail':
        collect_detail_from_json(args.category, start_from=args.start)
    else:
        collect_list_to_json(args.category)
        collect_detail_from_json(args.category)

if __name__ == '__main__':
    main()
