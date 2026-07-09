"""批处理采集 openstd 所有 ICS 分类（p.p2=5~49, p.p1=2 推荐性国标 + p.p1=3 指导性文件）"""
import sys, os, subprocess, argparse, time
sys.stdout.reconfigure(encoding='utf-8')

PYTHON = r'C:\Python312\python.exe'
HERE = os.path.dirname(os.path.abspath(__file__))

# 推荐性国标 p.p1=2
RECOMMEND_P2 = list(range(5, 50))  # 5~49

def run_batch(p1, p2_values, label):
    for i, p2 in enumerate(p2_values):
        cmd = f'{PYTHON} "{os.path.join(HERE, "collect_openstd.py")}" batch --p1 {p1} --p2 {p2}'
        print(f'[{label}] {i+1}/{len(p2_values)} p.p2={p2}  start')
        rc = subprocess.call(cmd, shell=True, cwd=os.path.dirname(HERE))
        print(f'[{label}] p.p2={p2} done rc={rc}')
        time.sleep(0.5)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--p1', type=int, default=2, choices=[2,3])
    ap.add_argument('--p2-from', type=int, default=5)
    ap.add_argument('--p2-to', type=int, default=49)
    ap.add_argument('mode', nargs='?', default='all',
                    choices=['recommend', 'guide', 'all'])
    args = ap.parse_args()
    if args.mode in ('recommend', 'all'):
        run_batch(2, range(5, 50), '推荐性国标')
    if args.mode in ('guide', 'all'):
        run_batch(3, range(5, 50), '指导性文件')

if __name__ == '__main__':
    main()
