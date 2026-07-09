import os, re, sys, requests
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})
r = s.get('http://www.csres.com/sort/chsortdetail/A.html', timeout=60)
r.encoding='gb18030'
subs = re.findall(r'href=\"(/sort/Chtype/([A-Z]\w+?)_\d+\.html)\"[^>]*>(.+?)</a>', r.text)
print(f'子类: {len(subs)}')
for path, code, name in subs[:5]:
    name_clean = re.sub(r'<[^>]+>', '', name).replace('&nbsp;',' ').strip()
    print(f'  {code}: {name_clean} -> {path}')
