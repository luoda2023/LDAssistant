import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'})

# 抓 A00 子类列表页
r = s.get('http://www.csres.com/sort/Chtype/A00_1.html', timeout=60)
r.encoding = 'gb18030'
print('A00_1.html 长度:', len(r.text))

# 找标准详情链接模式
patterns = [
    r'href="(/stand/[^"]+\.html[^"]*)"',
    r'href="(/stand/[^"]+)"',
    r'href="(/standard/[^"]+)"',
    r'href="(/sort/stdinfo[^"]+)"',
    r'href="(/sort[^"]+\.html[^"]*)"',
    r'href="(/[^"]*GB[^"]*)"',
    r'href="(/[^"]*\.pdf)"',
    r'(GB[/T]*\s*\d+(?:\.\d+)?-\d{4})',
]
for i, p in enumerate(patterns):
    hits = re.findall(p, r.text)
    print(f'模式{i}: 命中{len(hits)}个, 前5: {hits[:5]}')

# 找标准号
std_codes = re.findall(r'[A-Z]{1,5}(?:/[A-Z])?\s*\d+(?:\.\d+)*(?:\.\d+)?-\d{4}', r.text)
print(f'识别到的标准号: {len(std_codes)}, 前10: {std_codes[:10]}')

# 找标准详情链接
detail_links = re.findall(r'href="(/[^"]+)"[^>]*>[^<]*[A-Z]{1,5}(?:/[A-Z])?\s*\d+(?:\.\d+)*-\d{4}', r.text)
print(f'详情链接: {len(detail_links)}, 前5: {detail_links[:5]}')

# 打印前3000字符
print('\n=== A00_1.html 前3000字符 ===')
print(r.text[:3000])
