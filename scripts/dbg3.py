import sys
sys.path.insert(0, r'C:\ZCODE\scripts')
from collect_biaozhun import parse_detail_page
from common import fetch

for url in ['https://www.biaozhun.org/guojia/384102.html', 'https://www.biaozhun.org/guojia/384101.html']:
    html = fetch(url, timeout=30)
    rec = parse_detail_page(html)
    print('URL:', url)
    for k, v in rec.items():
        print(f'  {k}: {v}')
    print()
