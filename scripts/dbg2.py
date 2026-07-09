import sys
sys.path.insert(0, r'C:\ZCODE\scripts')
from common import fetch, FIELDS
from collect_biaozhun import parse_detail_page
import re

# 列表页
html = fetch('https://www.biaozhun.org/guojia/list-1-1.html', timeout=30)
hits = re.findall(r'<a\s+href="(/guojia/(\d+\.html))"[^>]*title="([^"]*)"', html)
print('total list:', len(hits))

# 抓前3个详情页看解析结果
for url_path, _, title in hits[:3]:
    url = 'https://www.biaozhun.org' + url_path
    try:
        dhtml = fetch(url, timeout=30)
        rec = parse_detail_page(dhtml)
        print('URL:', url)
        print('  输入 title:', title)
        print('  解析 编号:', repr(rec['标准编号']))
        print('  解析 名称:', repr(rec['标准名称']))
        print('  发布部门:', repr(rec['发布部门']))
        print('  替代情况:', repr(rec['替代情况']))
        print('  简介:', repr(rec['标准简介'])[:80])
        print()
    except Exception as e:
        print('URL:', url, 'ERROR:', e)
