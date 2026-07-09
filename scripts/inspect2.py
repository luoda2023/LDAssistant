import json, sys, os, re
sys.stdout.reconfigure(encoding='utf-8')
data = json.load(open('C:/ZCODE/data/biaozhun_difang_list.json','r',encoding='utf-8'))
print('前5:', [(d['code'],d['name'][:20]) for d in data[:5]])
done = set()
f = 'C:/ZCODE/data/biaozhun_difang_standards.txt'
if os.path.exists(f):
    for m in re.finditer(r'^标准编号\s*[:：]\s*(.+?)\s*$', open(f,'r',encoding='utf-8').read(), re.MULTILINE):
        done.add(m.group(1).strip())
print('done_ids:', list(done)[:5])
print('done总数:', len(done))
print('code在done中:', sum(1 for d in data if d['code'] and d['code'] in done))
