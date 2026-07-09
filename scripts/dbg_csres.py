import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'})

# 1) 大类详情页 A
r = s.get('http://www.csres.com/sort/chsortdetail/A.html', timeout=60)
r.encoding = 'gb18030'
print('A.html 长度:', len(r.text))
# 找子类链接模式
patterns = [
    r'href="(/sort/Chtype/[^"]+)"',
    r'href="(/sort[^"]*A\d+[^"]*)"',
    r'href="(Chtype/[^"]+)"',
    r'href="([^"]*A\d+[^"]*\.html)"',
    r'href="([^"]+)"\s*[^>]*>([^<]*(?:标准化|管理|技术)[^<]*)',
]
for i, p in enumerate(patterns):
    hits = re.findall(p, r.text)
    print(f'模式{i}: 命中{len(hits)}个, 前5: {hits[:5]}')

# 打印前2000字符
print('\n=== A.html 前2000字符 ===')
print(r.text[:2000])
