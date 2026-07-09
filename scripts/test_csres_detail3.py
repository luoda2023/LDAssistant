"""探查csres详情页 meta description 和 title 的实际结构"""
import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})
url = 'http://www.csres.com/detail/445319.html'
r = s.get(url, timeout=60)
r.encoding='gb18030'
# meta description
for m in re.finditer(r'<meta[^>]+>', r.text):
    if 'description' in m.group().lower() or 'keywords' in m.group().lower():
        print(m.group()[:300])
# title
for m in re.finditer(r'<title>(.*?)</title>', r.text, re.DOTALL):
    print('TITLE:', repr(m.group(1)))
# 找 table 中字段
print('\n=== 字段查找 ===')
for m in re.finditer(r'(标准编号|标准名称|标准状态|中标分类|ICS分类|发布部门|发布日期|实施日期|代替标准|替代情况|归口单位|起草单位|范围)[：:]\s*([^<\n]+)', r.text):
    print(f'  {m.group(1)}: {m.group(2)[:80]}')
# 看正文里标准号附近内容
print('\n=== /h1 ====')
for m in re.finditer(r'<h1[^>]*>([\s\S]+?)</h1>', r.text, re.IGNORECASE):
    print(repr(m.group(1)[:200]))
print('\n=== /detail/详情区 ===')
m = re.search(r'<title>.*?</title>', r.text, re.DOTALL)
# 找 tb4 内容
for m in re.finditer(r'class="tb4"[^>]*>([\s\S]{200,1000}?)</table>', r.text):
    print(repr(m.group(1)[:400]))
    break
