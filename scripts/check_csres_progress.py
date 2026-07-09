import os, sys, glob
sys.stdout.reconfigure(encoding='utf-8')
logs_dir = 'C:/ZCODE/logs'
for f in sorted(glob.glob(os.path.join(logs_dir, 'csres_*.log'))):
    with open(f, 'r', encoding='utf-8') as fp:
        lines = fp.readlines()
    if not lines: continue
    last = lines[-1].strip()
    ok_count = sum(1 for l in lines if '成功' in l or '已采' in l)
    print(f'{os.path.basename(f)}: 行数={len(lines)}, 末行={last[:60]}')
