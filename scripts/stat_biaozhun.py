import os, re
total = 0
print('=== biaozhun 各分类成果 ===')
for f in sorted(os.listdir('C:/ZCODE/data')):
    if f.startswith('biaozhun_') and f.endswith('_standards.txt'):
        path = 'C:/ZCODE/data/' + f
        with open(path,'r',encoding='utf-8') as fp:
            content = fp.read()
        cnt = content.count('标准名称 ：')
        size = os.path.getsize(path)
        print(f'{f}: {cnt} 条, {size:,} bytes')
        total += cnt
print(f'--- 合计 {total} 条 ---')

# 检查最后5条标准的字段完整性
print('\n=== guojia 数据样本最后一条 ===')
with open('C:/ZCODE/data/biaozhun_guojia_standards.txt','r',encoding='utf-8') as fp:
    blocks = fp.read().split('\n\n')
print(blocks[-2] if len(blocks)>=2 else blocks[0])
