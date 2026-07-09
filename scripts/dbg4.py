import sys, requests, re
sys.stdout.reconfigure(encoding='utf-8')
for cat in ['hangye','difang','tuanti','jiliang','qiye']:
    r = requests.get(f'https://www.biaozhun.org/{cat}/', timeout=30, headers={'User-Agent':'Mozilla/5.0'})
    r.encoding='utf-8'
    m = re.search(r"href=['\"]([^'\"]*)['\"][^>]*>末页", r.text)
    print(f'{cat}: 末页链接 =', m.group(1) if m else 'None')
    links = re.findall(rf'/{cat}/\d+\.html', r.text)
    print(f'  标准链接数: {len(links)}, 首3: {links[:3]}')
    pages = re.findall(r"list-(\d+-\d+)\.html", r.text)
    print(f'  list-N-N 模式: {pages[:5]}')
    pages2 = re.findall(r"list-(\d+)\.html", r.text)
    print(f'  list-N 模式: {pages2[:5]}')
