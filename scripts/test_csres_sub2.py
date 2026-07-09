"""直接测试 csres 子类提取，不通过 common"""
import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})
url = 'http://www.csres.com/sort/chsortdetail/A.html'
r = s.get(url, timeout=60)
r.encoding='gb18030'
print(f'获取A.html: 长度{len(r.text)}')
# 用 collect_csres 完整的 get_subcategories 逻辑
subs = []
for m in re.finditer(r'href="(/sort/Chtype/([A-Z]\w+?)_\d+\.html)"[^>]*>(.+?)</a>', r.text):
    path, code, name = m.group(1), m.group(2), m.group(3)
    name = re.sub(r'<[^>]+>', '', name).replace('&nbsp;', ' ').strip()
    subs.append((path, code, name))
print(f'子类: {len(subs)}')
for s in subs[:5]: print(f'  {s[1]}: {s[2]}')
