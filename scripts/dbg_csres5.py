"""调试 csres 子类提取问题 - 抓实际页面分析结构"""
import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')

s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})

# 大类详情页
for letter in ['A','B','L']:
    r = s.get(f'http://www.csres.com/sort/chsortdetail/{letter}.html', timeout=60)
    r.encoding = 'gb18030'
    print(f'\n=== {letter}.html 长度={len(r.text)} ===')
    # 试多种正则
    pats = [
        r'/sort/Chtype/([A-Z]\d+)_\d+\.html',
        r'<a\s+href="/sort/Chtype/([A-Z]\d+)[^"]+\.html"',
        r'Chtype/([A-Z]\d+)',
        r'href="([^"]*Chtype[^"]*)"',
        r'src=Chtype',
    ]
    for p in pats:
        hits = re.findall(p, r.text)
        print(f'  {p[:40]}: {len(hits)} hits, 首5: {hits[:5]}')
    # 找 A00 附近
    m = re.search(r'.{0,200}A00.{0,200}', r.text)
    if m:
        print(f'  A00附近: {m.group()[:300]}')
    break
