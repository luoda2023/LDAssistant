import sys; sys.path.insert(0, r'C:\ZCODE\scripts')
from collect_biaozhun import parse_detail_page
from common import fetch

urls = [
    'https://www.biaozhun.org/hangye/375743.html',
    'https://www.biaozhun.org/hangye/375744.html',
    'https://www.biaozhun.org/hangye/375742.html',
    'https://www.biaozhun.org/hangye/375741.html',
]
for url in urls:
    try:
        html = fetch(url, timeout=30)
        rec = parse_detail_page(html)
        print(f'OK: 编号={rec["标准编号"]!r} 名称={rec["标准名称"][:30]!r}')
    except Exception as e:
        import traceback
        print(f'FAIL {url}: {e}')
        traceback.print_exc()
