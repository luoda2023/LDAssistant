# -*- coding: utf-8 -*-
"""csres v4 采集器（修正解析版）
源: /new/N.html, /new/update/N.html, /new/input/N.html, /sort/chsortdetail/{字母}.html
"""
import os, sys, re, json, time, random
sys.stdout.reconfigure(encoding='utf-8')

import requests
DATA_DIR = r'C:\ZCODE\data'
LOG_DIR = r'C:\ZCODE\logs'
OUT_FILE = os.path.join(DATA_DIR, 'csres_v4_standards.txt')
LOG_FILE = os.path.join(LOG_DIR, 'collect_csres_v4.log')
DONE_FILE = os.path.join(DATA_DIR, 'csres_v4_done.json')
os.makedirs(DATA_DIR, exist_ok=True); os.makedirs(LOG_DIR, exist_ok=True)

SITE = 'http://www.csres.com'
UA_POOL = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edge/121.0.0.0',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36',
]

def log(text):
    line = f'{time.strftime("%Y-%m-%d %H:%M:%S")} | {text}'
    print(line)
    with open(LOG_FILE, 'a', encoding='utf-8') as f:
        f.write(line+'\n')

def save_record(rec):
    with open(OUT_FILE, 'a', encoding='utf-8') as f:
        for k in ['标准名称','标准编号','标准简介','标准状态','现行或作废','替代情况',
                  '中标分类','ICS分类','发布部门','发布日期','实施日期','detail_url','source_type']:
            f.write(f'{k} ：{rec.get(k,"")}\n')
        f.write('\n')

def load_done():
    if os.path.exists(DONE_FILE):
        return set(json.load(open(DONE_FILE,'r',encoding='utf-8')))
    return set()

def save_done(idset):
    json.dump(list(idset), open(DONE_FILE,'w',encoding='utf-8'))

def parse_list_page(html):
    """解析列表页 - 提取标准信息
    页面结构: <a href="/detail/413093.html" ...>GB/T 4706.118-2024 名称</a>
    然后: 中标分类: ... ICS分类: ... 发布日期:2024-07-24 实施日期:2024-07-24
    简介: ...
    """
    items = []
    # 按 detail 链接分段
    chunks = re.split(r'<a\s+href="(/detail/(\d+)\.html)"', html)
    # chunks: [prefix, href1, id1, content1, href2, id2, content2, ...]
    for i in range(1, len(chunks), 3):
        if i+2 >= len(chunks): break
        url = chunks[i]
        did = chunks[i+1]
        content = chunks[i+2]
        # 标准编号 + 名称
        m = re.search(r'>([^<]+)</a>', content)
        if not m: continue
        title = m.group(1).replace('&nbsp;',' ').strip()
        # 拆分: 标准编号 + 标准名称
        m2 = re.match(r'^((?:GB|JT|HA|HB|HG|HJ|JG|JY|NB|NY|QX|SH|SL|SN|TB|TD|WS|YD|YY|YZ|DB|T/[A-Z]+|JJ|JJF|JJG|T/[A-Z]{2,}/?)\s*(?:\d+(?:\.\d+)*(?:\.\d+)?)?[-\./]?\d{3,5}(?:\.\d+)?-\d{4})\s*(.*)$', title)
        if m2:
            code = m2.group(1).replace(' ','').replace('&nbsp;','')
            name = m2.group(2).strip()
        else:
            # fallback: 第一个空格切分
            parts = title.split(' ', 1)
            code = parts[0].replace('&nbsp;','')
            name = parts[1] if len(parts)>1 else ''
        
        # 提取状态
        status = ''
        if '(现行)' in content: status = '现行'
        elif '(即将实施)' in content: status = '即将实施'
        elif '(作废)' in content: status = '作废'
        elif '(废止)' in content: status = '废止'
        elif '(被代替)' in content: status = '被代替'
        
        # 中标分类
        ccs = ''
        m = re.search(r"中标分类:.*?class='a_hot_blue03'>([^<]+)</a>", content)
        if m: ccs = m.group(1).strip()
        
        # ICS分类
        ics = ''
        m = re.search(r"ICS\s*分类:.*?class='a_hot_blue03'>([^<]+)</a>", content)
        if m: ics = m.group(1).strip()
        
        # 发布日期 / 实施日期
        pub = imp = ''
        m = re.search(r'发布日期:(\d{4}-\d{2}-\d{2})', content)
        if m: pub = m.group(1)
        m = re.search(r'实施日期:(\d{4}-\d{2}-\d{2})', content)
        if m: imp = m.group(1)
        
        # 简介
        intro = ''
        m = re.search(r'简介:\s*([^<]+)', content)
        if m: intro = m.group(1).strip()[:200]
        
        rec = {
            '标准编号': code,
            '标准名称': name,
            '标准状态': status,
            '现行或作废': '现行' if status in ('现行','即将实施') else ('作废' if status in ('作废','废止') else ''),
            '发布日期': pub,
            '实施日期': imp,
            '中标分类': ccs,
            'ICS分类': ics,
            '标准简介': intro,
            'detail_url': f'{SITE}{url}',
            'source_type': 'csres_v4',
        }
        items.append((did, rec))
    return items

def fetch_page(session, url):
    """获取页面"""
    for retry in range(3):
        try:
            session.headers.update({'User-Agent': random.choice(UA_POOL)})
            r = session.get(url, timeout=20)
            r.encoding = 'gb18030'
            if r.status_code == 200 and len(r.text) > 3000:
                return r.text
        except Exception as e:
            log(f'  retry {retry}: {url} ERR {type(e).__name__}')
            time.sleep(random.uniform(1, 3))
    return None

def main():
    log('===== csres v4 启动(直连模式) =====')
    sess = requests.Session()
    sess.trust_env = False  # 关键: 禁用系统代理
    sess.headers.update({'Accept':'text/html,application/xhtml+xml', 
                         'Accept-Language':'zh-CN,zh',
                         'Referer':'http://www.csres.com/'})
    
    # 初始cookie
    sess.get(f'{SITE}/', timeout=20)
    log(f'cookies: {dict(sess.cookies)}')
    
    done_set = load_done()
    log(f'断点续采: 已记录 {len(done_set)} 个 id')
    
    pages_to_scrape = []
    for i in range(1, 251): pages_to_scrape.append(f'{SITE}/new/{i}.html')
    for i in range(1, 101): pages_to_scrape.append(f'{SITE}/new/update/{i}.html')
    for i in range(1, 101): pages_to_scrape.append(f'{SITE}/new/input/{i}.html')
    for c in 'ABCDEFGHIJKLMNOPRSTWXYZ':
        pages_to_scrape.append(f'{SITE}/sort/chsortdetail/{c}.html')
    log(f'待扫描列表页: {len(pages_to_scrape)}')
    
    total_new = 0
    batch_buf = []
    for i, u in enumerate(pages_to_scrape):
        html = fetch_page(sess, u)
        if not html:
            log(f'[{i+1}/{len(pages_to_scrape)}] FAIL {u}')
            continue
        items = parse_list_page(html)
        new_count = 0
        for did, rec in items:
            if did not in done_set:
                batch_buf.append(rec)
                done_set.add(did)
                total_new += 1
                new_count += 1
        log(f'[{i+1}/{len(pages_to_scrape)}] {u.replace(SITE,"")} 提取{len(items)} 新增{new_count} 累计{total_new}')
        # 每50条写一次
        if len(batch_buf) >= 50:
            for r in batch_buf:
                save_record(r)
            save_done(done_set)
            batch_buf = []
            log(f'>>> 已写入文件 累计{total_new} 条')
        time.sleep(random.uniform(0.5, 1.5))
    
    if batch_buf:
        for r in batch_buf: save_record(r)
        save_done(done_set)
    
    log(f'===== 完成：共新增 {total_new} 条, 总id数 {len(done_set)} =====')

if __name__ == '__main__':
    main()
