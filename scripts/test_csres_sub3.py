import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})
url = 'http://www.csres.com/sort/chsortdetail/A.html'
r = s.get(url, timeout=60)
r.encoding='gb18030'
print(f'获取A.html: 长度{len(r.text)}')
# 找一个真实子类链接上下文
m = re.search(r'.{0,100}A00_1\.html.{0,150}', r.text)
if m:
    print('A00_1上下文(原):', repr(m.group()))
# 修复版正则（DOTALL支持跨行）
print('\n=== 修复版正则 ===')
pats = [
    r'href="(/sort/Chtype/([A-Z]\w+?)_\d+\.html)"[^>]*>(.+?)</a>',
    r'href="(/sort/Chtype/([A-Z]\w+?)_\d+\.html)"[^>]*>([\s\S]+?)</a>',  # DOTALL
    r'href="(/sort/Chtype/([A-Z]\w+?)_\d+\.html)"',
]
for i, p in enumerate(pats):
    if i < 2:
        hits = re.findall(p, r.text, re.DOTALL)
    else:
        hits = re.findall(p, r.text)
    print(f'模式{i}: {len(hits)} hits')
    if hits and isinstance(hits[0], tuple):
        for h in hits[:3]: print(f'  {h}')
    else:
        for h in hits[:3]: print(f'  {h}')
