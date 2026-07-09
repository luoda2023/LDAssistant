import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})

url = 'http://www.csres.com/sort/Chtype/A00_1.html'
r = s.get(url, timeout=60)
r.encoding='gb18030'

# 找包含 GB 的标准条目（逐行）
lines = r.text.split('\n')
for i, line in enumerate(lines):
    if 'GB' in line and 'html' in line and ('href' in line or 'HREF' in line):
        print(f'L{i}: {line.strip()[:200]}')
    if 'A00' in line and 'html' in line:
        print(f'L{i} A00: {line.strip()[:200]}')
