"""测试其他可用标准数据源"""
import requests, sys, re
sys.stdout.reconfigure(encoding='utf-8')
s = requests.Session()
s.headers.update({
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9',
    'Accept-Language': 'zh-CN,zh;q=0.9',
})

# 候选数据源
sources = [
    # 国标委官方 - 已知可用
    ('openstd.samr.gov.cn 强制性', 'https://openstd.samr.gov.cn/bzgk/gb/std_list_type?p.p1=1&p.p2=5'),
    # 国家标准信息公共服务平台
    ('std.samr.gov.cn', 'http://std.samr.gov.cn/'),
    # 全国标准信息公共服务平台
    ('std.samr.gov.cn search', 'http://std.samr.gov.cn/gb/search/gbDetailed?id=GB'),
    # 标准网
    ('biaozhun8.com', 'http://www.biaozhun8.com/'),
    ('biaozhun8 GB search', 'http://www.biaozhun8.com/search?keyword=GB'),
    # 学兔鸭
    ('xuetu.co', 'https://www.xuetu.co/'),
    # 标准免费下载
    ('biaozhun.info', 'https://biaozhun.info/'),
    # 中国标准在线服务网
    ('biaozhun.org中国标准在线', 'http://www.spc.org.cn/'),
    ('spc.org.cn', 'http://www.spc.org.cn/'),
    # 中国标准出版社
    ('spc.stddevelop.com', 'https://www.spc.org.cn/online/'),
    # 国家标准全文公开系统ipv6
    ('gb6.samr.gov.cn', 'https://gb6.samr.gov.cn/'),
    # 工标网 IPv6 / 镜像
    ('csres ipv6', 'http://www.csres.com/'),  # 主页总可用
    # 移动版
    ('m.csres.com', 'http://m.csres.com/'),
    # standard.net.cn 中国行业标准网
    ('standard.net.cn', 'http://www.standard.net.cn/'),
    # hugaowu plants
    ('bzmxx.com', 'http://www.bzmxx.com/'),
    # 国标网库
    ('biaozhunku.com', 'https://www.biaozhunku.com/'),
]

results = []
for name, url in sources:
    try:
        r = s.get(url, timeout=10, allow_redirects=False)
        ok = r.status_code == 200 and len(r.text) > 1000
        marker = '✅' if ok else '⚠️' if r.status_code == 200 else '❌'
        print(f'{marker} {name}: {r.status_code} len={len(r.text)}')
        results.append((name, ok, r.status_code, len(r.text)))
    except Exception as e:
        print(f'❌ {name}: ERROR {type(e).__name__}')
        results.append((name, False, 0, 0))

print('\n--- 可用源汇总 ---')
ok_sources = [(n,s,l) for n,ok,s,l in results if ok]
for n,s,l in ok_sources:
    print(f'  ✅ {n} ({s} {l}字节)')
