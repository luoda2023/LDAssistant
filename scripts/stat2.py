import os, sys
sys.stdout.reconfigure(encoding='utf-8')
total = 0
for f in sorted(os.listdir('C:/ZCODE/data')):
    if f.endswith('_list.json') or f.endswith('_standards.txt'):
        path = 'C:/ZCODE/data/'+f
        with open(path,'r',encoding='utf-8') as fp:
            content = fp.read()
        if '_list.json' in f:
            cnt = content.count('"url":')
            print(f'{f}: {cnt} 条(列表)')
        elif '_standards.txt' in f:
            cnt = content.count('标准名称 ：')
            print(f'{f}: {cnt} 条(详情)')
            total += cnt
print(f'--- 合计 {total} 条详情 ---')
