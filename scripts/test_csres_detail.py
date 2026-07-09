"""测试 csres 详情页解析"""
import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({'User-Agent':'Mozilla/5.0'})

url = 'http://www.csres.com/detail/445319.html'
r = s.get(url, timeout=60)
r.encoding='gb18030'
print(f'详情页长度={len(r.text)}')
# 输出关键部分
print('=== 标准编号 ===')
for m in re.finditer(r'标准编号[：:](.*?)<', r.text):
    print(m.group(1))
print('=== 标准名称(前100) ===')
for m in re.finditer(r'标准名称[：:](.*?)<', r.text):
    print(m.group(1)[:100])
print('=== 发布部门/替代/总页 ---')
for m in re.finditer(r'(发布部门|代替标准|ICS|中标分类|发布日期|实施日期|标准状态)[：:](.*?)<', r.text):
    print(f'{m.group(1)}:{m.group(2)[:50]}')
print('\n=== 前500字符 ===')
print(r.text[:500])
