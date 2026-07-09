"""测试通过系统代理 + 非系统代理方式访问 csres 搜索接口"""
import requests, sys
sys.stdout.reconfigure(encoding='utf-8')

# 已知系统配置了代理 127.0.0.1:10808，这正好是科学上网
PROXY = 'http://127.0.0.1:10808'

# 1. 不用代理（直连）
print('=== 1. 直连 csres 搜索 ===')
s1 = requests.Session()
s1.headers.update({'User-Agent':'Mozilla/5.0', 'Cookie': 'JSESSIONID=test'})
try:
    r = s1.get('http://www.csres.com/s.jsp?keyword=GB&pageNum=1', timeout=15)
    print(f'  状态={r.status_code} 长度={len(r.text)} {"无权" if "无权访问" in r.text else "可能可用"}')
except Exception as e:
    print(f'  ❌ {type(e).__name__}')

# 2. 用系统代理（科学上网，可能换IP/换地区）
print('\n=== 2. 过代理(科学上网) csres 搜索 ===')
s2 = requests.Session()
s2.proxies = {'http': PROXY, 'https': PROXY}
s2.headers.update({'User-Agent':'Mozilla/5.0'})
try:
    r = s2.get('http://www.csres.com/', timeout=15)
    print(f'  主页: {r.status_code} len={len(r.text)}')
    r = s2.get('http://www.csres.com/s.jsp?keyword=GB&pageNum=1', timeout=30)
    print(f'  搜索: {r.status_code} len={len(r.text)} {"无权" if "无权访问" in r.text else "✅可用!"}')
except Exception as e:
    print(f'  ❌ {type(e).__name__}')

# 3. 测试代理对 openstd 的兼容性
print('\n=== 3. 过代理 openstd 搜索 ===')
try:
    r = s2.get('https://openstd.samr.gov.cn/bzgk/std/std_list_type?r=0.5&page=1&pageSize=10&p.p1=2&p.p2=5&p.p90=circulation_date&p.p91=desc', timeout=30)
    print(f'  openstd: {r.status_code} len={len(r.text)}')
except Exception as e:
    print(f'  ❌ {type(e).__name__}')
