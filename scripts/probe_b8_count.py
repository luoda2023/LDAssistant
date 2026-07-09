import requests, sys, re
sys.stdout.reconfigure(encoding='utf-8')
from collections import Counter
r = requests.get('http://www.biaozhun8.com/sitemap.xml', headers={'User-Agent':'Mozilla/5.0'}, timeout=120)
urls = re.findall(r'<loc>([^<]+)</loc>', r.text)
print(f'sitemap URL总数: {len(urls)}')
prefixes = Counter()
for u in urls:
    m = re.match(r'http://www\.biaozhun8\.com/([a-z]+)(?:-?(\d+))?/', u)
    if m:
        prefixes[m.group(1)] += 1
for p,c in prefixes.most_common():
    print(f'  /{p}/: {c}')

ids = []
for u in urls:
    m = re.match(r'http://www\.biaozhun8\.com/biaozhun-(\d+)/', u)
    if m: ids.append(int(m.group(1)))
if ids:
    print(f'\nbiaozhun-XXX 范围: {min(ids)} ~ {max(ids)} 共 {len(ids)} 个')

ids2 = []
for u in urls:
    m = re.match(r'http://www\.biaozhun8\.com/xinxi-(\d+)/', u)
    if m: ids2.append(int(m.group(1)))
if ids2:
    print(f'xinxi-XXX 范围: {min(ids2)} ~ {max(ids2)} 共 {len(ids2)} 个')
