import requests, re, sys
sys.stdout.reconfigure(encoding='utf-8')
r = requests.get('https://www.biaozhun.org/guojia/list-1-1.html', timeout=30, headers={'User-Agent':'Mozilla/5.0'})
r.encoding='utf-8'
print('LEN:', len(r.text))
hits = re.findall(r'/guojia/\d+\.html', r.text)
print('hits:', len(hits))
# 提取标题
titles = re.findall(r'<a href="(/guojia/\d+\.html)"[^>]*title="([^"]+)"', r.text)
print('titles:', len(titles))
for h in titles[:5]:
    print(h)
