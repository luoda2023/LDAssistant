"""biaozhun.org 通用采集器（适用于 guojia/hangye/difang/tuanti/jiliang/qiye 6 个分类）

用法：
  python collect_biaozhun.py <category> [--start=<n>] [--end=<n>]

详情页已分析字段（参考 /guojia/384103.html）：
  - 标准名称：div.title h1 文本中的标准名（去掉编号和" "前缀），
              同时<div class="content"><ul><li>中 "标准名称：" 后值
  - 标准编号：div.title h1 中提取（如 GB 47372-2026）
  - 标准简介：div.info 第一段 p（h3=内容简介 后面的 p）
  - 标准状态：div.time-line h3 含"标准状态" 然后取 label 文本或 data-zt
  - 现行或作废：根据状态推断
  - 替代情况：div.content ul li 中 "代替标准：" 后的 dd
  - 中标分类（CCS）：div.content ul li 中 "中国标准分类号：" 后的 dt
  - ICS分类：div.content ul li 中 "国际标准分类号：" 后的 dt
  - 发布部门（主管部门）：div.content ul li 中 "主管部门：" 后的 dt
  - 发布日期：div.content ul li 中 "发布日期：" 后的 dd
  - 实施日期：div.content ul li 中 "实施日期：" 后的 dd
"""
import sys
import os
import re
import argparse
import time
import random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import (BASE_DIR, DATA_DIR, LOG_DIR, FIELDS,
                    fetch, parse_html, save_record, load_done_ids,
                    log_line, polite_sleep, get_ua, UA_LIST)

SITE = "https://www.biaozhun.org"

def get_pagination_info(category):
    """从首页提取 (分页前缀数字, 末页数)。
    每个分类的分页格式: list-{prefix}-{page}.html
    guojia用1, hangye用2, difang用3, tuanti用4, jiliang用5, qiye用6
    """
    url = f"{SITE}/{category}/"
    html = fetch(url, timeout=30)
    # 找末页 <a href='list-XX-YY.html'>末页</a>
    m = re.search(r"href=['\"]list-(\d+)-(\d+)\.html['\"][^>]*>末页", html)
    if m:
        return int(m.group(1)), int(m.group(2))
    # 备选：找所有 list-N-M.html 中最大的 N（通常为该分类前缀），M 为末页
    all_pages = re.findall(r"list-(\d+)-(\d+)\.html", html)
    if all_pages:
        # 取出现频率最高的 prefix
        from collections import Counter
        prefix_count = Counter(p[0] for p in all_pages)
        prefix = int(prefix_count.most_common(1)[0][0])
        max_page = max(int(p[1]) for p in all_pages if int(p[0]) == prefix)
        return prefix, max_page
    return 1, 1

def parse_list_page(html, category):
    """提取列表页所有标准的 (链接, 编号, 名称)"""
    items = []
    # 主匹配：a 标签带 title 属性 "编号 名称"
    pattern1 = r'<a\s+href="(/%s/(\d+\.html))"[^>]*title="([^"]*)"' % category
    for m in re.finditer(pattern1, html):
        url_path = m.group(1)
        title = m.group(3).strip()
        if not title:
            continue
        parts = title.split(' ', 1)
        if len(parts) == 2:
            code, name = parts[0], parts[1]
        else:
            code, name = "", title
        items.append((SITE + url_path, code, name))
    # 备选：a 标签文本含 "编号 名称"
    if not items:
        pattern2 = r'<a\s+href="(/%s/(\d+\.html))"[^>]*>([^<]+)</a>' % category
        for m in re.finditer(pattern2, html):
            url_path = m.group(1)
            title = m.group(3).strip()
            if not title or len(title) < 4:
                continue
            parts = title.split(' ', 1)
            if len(parts) == 2:
                code, name = parts[0], parts[1]
            else:
                code, name = "", title
            items.append((SITE + url_path, code, name))
    # 去重
    seen = set()
    uniq = []
    for it in items:
        if it[0] not in seen:
            seen.add(it[0])
            uniq.append(it)
    return uniq

def parse_detail_page(html):
    """解析详情页，返回 dict"""
    soup = parse_html(html)
    rec = {f: "" for f in FIELDS}

    # 标准名称与编号：h1 文本，如 "GB 47372-2026 移动电源安全技术规范"
    h1 = soup.find('h1')
    if h1:
        full = re.sub(r'\s+', ' ', h1.get_text(strip=True)).strip()
        # 用正则提取标准编号（GB/T、GB/Z、DB、QB、HG、JJF 等各种前缀+数字-年份，编号可能有 /T、/Z 等后缀）
        m = re.match(r'^([A-Za-z]{1,5}(?:/[A-Za-z])?(?:\s*[（(]?[^)）\s]*[)）])?\s*\d+(?:\.\d+)*(?:-\d{4})?)\s+(.+)$', full)
        if m:
            rec['标准编号'] = m.group(1).replace(' ', ' ')  # 保留原格式
            rec['标准名称'] = m.group(2).strip()
        else:
            # 备用：第一个空格前为编号
            parts = full.split(' ', 1)
            if len(parts) == 2:
                rec['标准编号'] = parts[0]
                rec['标准名称'] = parts[1]
            else:
                rec['标准名称'] = full

    # content ul 内容字段
    content = soup.find('div', class_='content')
    if content:
        # 标准名称 li
        for li in content.find_all('li'):
            txt = li.get_text(strip=True)
            # 标准名称：
            m = re.match(r'^标准名称：(.+)$', txt)
            if m and not rec['标准名称']:
                rec['标准名称'] = m.group(1).strip()
            # 代替标准 / 代替标准：
            m = re.match(r'^代替标准[：:](.*)$', txt)
            if m:
                rec['替代情况'] = m.group(1).strip()
            # 标准分类（中标）
            m = re.match(r'^标准分类[：:](.+)$', txt)
            if m:
                rec['中标分类'] = m.group(1).strip()
        # dd/dt 配对结构：每对 dd 和 dt 在同一<li>内并列，按文档结构分别处理
        for li in content.find_all('li'):
            # dd 都在 dt 前面，分开遍历，避免 get_text 合并
            for dd in li.find_all('dd', recursive=False):
                spans = dd.find_all('span', recursive=False)
                label = spans[0].get_text(strip=True).rstrip('：:') if spans else ''
                value_parts = [c for c in dd.strings if c.parent is dd or c.parent.name != 'span']
                # 更稳：取 spans[0] 之后的 NavigableString
                if spans:
                    span = spans[0]
                    rest = ''.join(str(s) for s in span.next_siblings)
                    value = rest.strip()
                else:
                    value = dd.get_text(strip=True)
                # 清理尾部 dt 文本：去掉 "主管部门xxx" 或 "技术归口xxx" 等不需要的尾巴
                # 形如 "代替GB 12021.4-2013主管部门：国家标准委" → "代替GB 12021.4-2013"
                value = re.sub(r'(?:主管部门|技术归口|国际标准分类号|中国标准分类号)[：:].*$', '', value).strip()
                # 形如 "(2013)(主管部门:XXX)" 等带括号
                value = re.sub(r'[（(][^)）]*(?:主管部门|技术归口)[^)）]*[)）]\s*$', '', value).strip()
                if label == '标准号':
                    if not rec['标准编号']:
                        rec['标准编号'] = value
                elif label == '发布日期':
                    rec['发布日期'] = value
                elif label == '实施日期':
                    rec['实施日期'] = value
                elif label == '代替标准':
                    if not rec['替代情况']:
                        rec['替代情况'] = value
            for dt in li.find_all('dt', recursive=False):
                spans = dt.find_all('span', recursive=False)
                label = spans[0].get_text(strip=True).rstrip('：:') if spans else ''
                if spans:
                    span = spans[0]
                    rest = ''.join(str(s) for s in span.next_siblings)
                    value = rest.strip()
                else:
                    value = dt.get_text(strip=True)
                if label == '中国标准分类号':
                    rec['中标分类'] = value
                elif label == '国际标准分类号':
                    rec['ICS分类'] = value
                elif label in ('技术归口', '主管部门'):
                    if not rec['发布部门']:
                        rec['发布部门'] = value

    # 状态：从 s-status 取 data-zt
    s = soup.find(id='s-status')
    if s:
        rec['标准状态'] = s.get('data-zt', '').strip()
    # 列表页可能有"即将实施"
    if not rec['标准状态']:
        m = re.search(r'class=["\']state["\']>([^<]+)<', html)
        if m:
            rec['标准状态'] = m.group(1).strip()

    # 现行或作废：状态包含 "现行" 即"现行"，否则按状态名
    state = rec['标准状态']
    if state:
        if '现行' in state:
            rec['现行或作废'] = '现行'
        elif '废止' in state or '作废' in state:
            rec['现行或作废'] = '作废'
        elif '即将实施' in state or '实施' in state:
            rec['现行或作废'] = '现行'  # 即将实施视作现行
        elif '被代替' in state or '更新版' in state:
            rec['现行或作废'] = '被代替'
        else:
            rec['现行或作废'] = state

    # 简介：div.info 第1个 p（h3=内容简介 之后）
    for info in soup.find_all('div', class_='info'):
        h3 = info.find('h3')
        if h3 and '内容简介' in h3.get_text(strip=True):
            p = info.find('p')
            if p:
                rec['标准简介'] = p.get_text(strip=True)
                break

    return rec

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('category')
    ap.add_argument('--start', type=int, default=1)
    ap.add_argument('--end', type=int, default=None)
    args = ap.parse_args()

    category = args.category
    outfile = os.path.join(DATA_DIR, f"biaozhun_{category}_standards.txt")
    logfile = os.path.join(LOG_DIR, f"biaozhun_{category}.log")

    prefix, total_pages = get_pagination_info(category)
    end_page = args.end or total_pages
    log_line(logfile, f"===== 开始采集 {category} 分页前缀={prefix} {args.start}~{end_page} 页 (总{total_pages}页) =====")

    # 已采编号（用标准编号做key，因为URL不能放入done且后续匹配用的也是标准编号）
    done_codes = load_done_ids(outfile)
    log_line(logfile, f"已采集 {len(done_codes)} 条，将跳过")

    ok_count = 0
    fail_count = 0
    start_ts = time.time()

    # 1) 抓所有列表页 → 收集 URL（分页URL: /{category}/list-{prefix}-{page}.html）
    all_items = []
    for p in range(args.start, end_page + 1):
        url = f"{SITE}/{category}/list-{prefix}-{p}.html"
        try:
            html = fetch(url, timeout=30)
            items = parse_list_page(html, category)
            all_items.extend(items)
            log_line(logfile, f"列表页 {p} OK，提取 {len(items)} 条")
            polite_sleep()
        except Exception as e:
            fail_count += 1
            log_line(logfile, f"列表页 {p} 失败：{e}")
            time.sleep(2)
        if p % 10 == 0:
            log_line(logfile, f"已扫 {p}/{end_page} 页，收集 {len(all_items)} 条候选")

    # URL 去重
    seen_urls = set()
    items = []
    for u, c, n in all_items:
        if u not in seen_urls:
            seen_urls.add(u)
            items.append((u, c, n))
    log_line(logfile, f"列表采集完成，去重后 {len(items)} 条候选")

    # 2) 逐个抓详情
    for i, (url, code, name) in enumerate(items):
        # 用列表页提取的标准编号作为去重key
        if code and code in done_codes:
            continue
        try:
            html = fetch(url, timeout=30)
            rec = parse_detail_page(html)
            if not rec['标准名称']:
                rec['标准名称'] = name
            if not rec['标准编号']:
                rec['标准编号'] = code
            save_record(outfile, rec)
            ok_count += 1
            done_codes.add((code or rec['标准编号']) or url)
            if ok_count % 10 == 0:
                log_line(logfile, f"已采集 {ok_count}/{len(items)}：{rec['标准编号']} {rec['标准名称'][:30]}")
            polite_sleep(0.6, 1.2)
        except Exception as e:
            fail_count += 1
            if fail_count <= 5:
                log_line(logfile, f"详情失败 [{fail_count}] {url}: {type(e).__name__}: {e}")
            time.sleep(1.5)
        # 每50条输出进度速览
        if i > 0 and i % 50 == 0:
            log_line(logfile, f"进度：已遍历 {i}/{len(items)} 个候选，成功 {ok_count} 条，失败 {fail_count} 条")

    elapsed = time.time() - start_ts
    log_line(logfile, f"===== {category} 完成 =====")
    log_line(logfile, f"成功 {ok_count} 条，失败 {fail_count}，耗时 {elapsed:.1f}s")
    log_line(logfile, f"输出：{outfile}")

if __name__ == '__main__':
    main()
