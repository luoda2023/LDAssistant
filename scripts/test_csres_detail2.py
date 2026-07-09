"""
测试 csres parse_detail_page 单个详情
取出脚本执行结果
"""
import sys, os, requests, re
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, r'C:\ZCODE\scripts')
from collect_csres import fetch_gbk, parse_detail_page

url = 'http://www.csres.com/detail/445319.html'
html = fetch_gbk(url)
print(f'html长度={len(html)}')
rec = parse_detail_page(html)
print('解析结果：')
for k, v in rec.items():
    print(f'  {k}: {v}')
