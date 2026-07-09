"""biaozhun8.com 采集器 - 通过 sitemap.xml 直接获取所有标准 URL
- 直连（不走代理，速度更快）
- sitemap.xml 含 501 条 /biaozhun-{id}/ 和 /xinxi-{id}/
- 详情页含标准全文，提取：标准号、名称、发布日期、实施日期、状态、代替、ICS、CCS
"""
import requests, sys, re, os, time, json
from datetime import datetime
sys.stdout.reconfigure(encoding='utf-8')

# 不用代理 - 直连快，必须禁用系统代理
SESSION = requests.Session()
SESSION.trust_env = False
SESSION.headers.update({
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9',
    'Accept-Language': 'zh-CN,zh;q=0.9',
    'Referer': 'http://www.biaozhun8.com/',
})

DATA_DIR = r'C:\ZCODE\data'
LOG_DIR = r'C:\ZCODE\logs'
os.makedirs(DATA_DIR, exist_ok=True)
os.makedirs(LOG_DIR, exist_ok=True)

OUT_FILE = os.path.join(DATA_DIR, 'biaozhun8_standards.txt')
LOG_FILE = os.path.join(LOG_DIR, 'biaozhun8.log')

def log(msg):
    line = f'{datetime.now().strftime("%Y-%m-%d %H:%M:%S")} | {msg}'
    print(line)
    with open(LOG_FILE, 'a', encoding='utf-8') as f:
        f.write(line + '\n')

def get_sitemap_urls():
    """从 sitemap.xml 获取所有 URL"""
    r = SESSION.get('http://www.biaozhun8.com/sitemap.xml', timeout=120)
    urls = re.findall(r'<loc>([^<]+)</loc>', r.text)
    # 排除首页
    urls = [u for u in urls if u != 'http://www.biaozhun8.com/']
    return urls

def parse_detail(url):
    """解析详情页，提取结构化字段"""
    r = SESSION.get(url, timeout=30)
    r.encoding = 'utf-8'
    html = r.text
    if r.status_code != 200 or len(html) < 1000:
        return None

    # 清理HTML
    clean = re.sub(r'<script[\s\S]*?</script>', '', html)
    clean = re.sub(r'<style[\s\S]*?</style>', '', clean)
    text = re.sub(r'<[^>]+>', '\n', clean)
    text = re.sub(r'\n+', '\n', text)
    text = re.sub(r'[ \t]+', ' ', text)

    rec = {
        '标准编号': '',
        '标准名称': '',
        '标准状态': '现行',
        '现行或作废': '现行',
        '发布日期': '',
        '实施日期': '',
        '替代情况': '',
        '中标分类': '',
        'ICS分类': '',
        '发布部门': '',
        '标准简介': url,
    }

    # 从文本提取标准号
    m = re.search(r'(GB[/T]*\s*\d+[\.\-]?\d*(?:\.\d+)?-\d{4})', text)
    if m: rec['标准编号'] = m.group(1).replace(' ', '')
    m = re.search(r'(GB[/T]*\s*\d+[\.\-]?\d*(?:\.\d+)?-\d{4})', html)
    if m and not rec['标准编号']: rec['标准编号'] = m.group(1).replace(' ', '')

    # 标题作为标准名称
    m = re.search(r'<title>([^<]+)</title>', html)
    if m:
        title = m.group(1).strip()
        title = re.sub(r'[-_|].*$', '', title).strip()
        if title:
            rec['标准名称'] = title[:200]

    # 找日期 YYYY-MM-DD
    dates = re.findall(r'(\d{4}-\d{2}-\d{2})', text)
    if dates:
        rec['发布日期'] = dates[0]
        if len(dates) > 1:
            rec['实施日期'] = dates[1]

    # 找代替标准
    m = re.search(r'代替[^:：]*[:：]\s*([^\n]+)', text)
    if m:
        rec['替代情况'] = m.group(1).strip()[:200]

    # 找 ICS
    m = re.search(r'ICS?\s*[:：]?\s*([\d\.]+)', text)
    if m:
        rec['ICS分类'] = m.group(1)
    # CCS
    m = re.search(r'CCS?\s*[:：]?\s*([A-Z]\d+)', text)
    if m:
        rec['中标分类'] = m.group(1)

    return rec if rec['标准编号'] else None

def save_record(rec):
    with open(OUT_FILE, 'a', encoding='utf-8') as f:
        for k, v in rec.items():
            f.write(f'{k} ：{v}\n')
        f.write('\n')

def main():
    log('===== 开始 biaozhun8 采集 =====')
    urls = get_sitemap_urls()
    log(f'sitemap 总 URL: {len(urls)}')

    # 已采
    done = set()
    if os.path.exists(OUT_FILE):
        with open(OUT_FILE, 'r', encoding='utf-8') as f:
            for line in f:
                m = re.match(r'标准编号 ：(\S+)', line)
                if m: done.add(m.group(1))
    log(f'已采 {len(done)} 条')

    ok = 0; fail = 0
    for i, u in enumerate(urls):
        try:
            rec = parse_detail(u)
            if rec:
                code = rec['标准编号']
                if code not in done:
                    save_record(rec)
                    done.add(code)
                    ok += 1
                    if ok % 20 == 0:
                        log(f'[{i+1}/{len(urls)}] ok={ok} fail={fail}  当前: {code} {rec["标准名称"][:30]}')
            else:
                fail += 1
            time.sleep(0.5)
        except Exception as e:
            fail += 1
            log(f'[{i+1}/{len(urls)}] ERR {u}: {type(e).__name__}')
            time.sleep(1)
    log(f'===== biaozhun8 完成：成功 {ok} 失败 {fail} =====')

if __name__ == '__main__':
    main()
