import sys, requests, re, traceback
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, r'C:\ZCODE\scripts')
from collect_biaozhun import parse_detail_page
from common import fetch

# 测试 guojia 列表前5条详情，测试为什么大量失败
import re as re2
html = fetch('https://www.biaozhun.org/guojia/list-1-1.html', timeout=30)
hits = re2.findall(r'<a\s+href="(/guojia/(\d+\.html))"[^>]*title="([^"]*)"', html)
print(f'列表 {len(hits)} 条')
for path, _, title in hits[:5]:
    url = 'https://www.biaozhun.org' + path
    try:
        dhtml = fetch(url, timeout=30)
        rec = parse_detail_page(dhtml)
        print(f'OK {title[:30]}: 编号={rec["标准编号"]!r} 名={rec["标准名称"][:20]!r}')
    except Exception as e:
        print(f'FAIL {url}: {type(e).__name__}: {e}')
        traceback.print_exc()
        print('--- HTML响应长度:', len(dhtml) if 'dhtml' in dir() else 'N/A')
        print('--- 响应前500字符:', dhtml[:500] if 'dhtml' in dir() else 'N/A')
