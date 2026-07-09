"""LDAssistant 数据采集总调度器
- 一键采集 biaozhun 6分类 + csres 23大类+行业+ICS
- 自动去重合并
- 自动生成 standards.db (含FTS5)
- 自动上传 GitHub 备份

用法：
  python run_all.py collect_biaozhun     # 仅采 biaozhun 6 分类
  python run_all.py collect_csres        # 仅采 csres 23 大类+行业+ICS
  python run_all.py merge                # 合并所有 txt 到一个 JSON
  python run_all.py build_db             # 由 JSON 生成 standards.db
  python run_all.py push                 # 上传到 GitHub
  python run_all.py all                  # 顺序执行 collect_biaozhun → collect_csres → merge → build_db → push
"""
import sys, os, subprocess, time, argparse
sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
PYTHON = r'C:\Python312\python.exe'
REPO_DIR = r'C:\ZCODE\github_repo'

def run(cmd, **kw):
    """同步执行子进程，实时打印stdout"""
    print(f'\n>>> {cmd}', flush=True)
    proc = subprocess.Popen(
        cmd, shell=True, cwd=HERE,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        bufsize=1, text=True, encoding='utf-8', errors='ignore',
    )
    for line in proc.stdout:
        sys.stdout.write(line); sys.stdout.flush()
    proc.wait()
    return proc.returncode

def collect_biaozhun():
    """双阶段采集 biaozhun 6 个分类"""
    cats = ['guojia', 'hangye', 'difang', 'tuanti', 'jiliang', 'qiye']
    print('=== biaozhun 列表采集 ===')
    for c in cats:
        rc = run(f'{PYTHON} collect_biaozhun_detail_only.py list {c}')
        if rc != 0: print(f'!! {c} 列表采集失败 rc={rc}')
    print('\n=== biaozhun 详情采集 ===')
    for c in cats:
        rc = run(f'{PYTHON} collect_biaozhun_detail_only.py detail {c}')
        if rc != 0: print(f'!! {c} 详情采集失败 rc={rc}')

def collect_csres():
    """csres 23 大类 + 行业 + ICS"""
    print('=== csres 中标23大类采集 ===')
    rc = run(f'{PYTHON} collect_csres.py zhongbiao ALL')
    if rc != 0: print(f'!! zhongbiao 失败 rc={rc}')
    print('\n=== csres 行业分类采集 ===')
    rc = run(f'{PYTHON} collect_csres.py industry')
    if rc != 0: print(f'!! industry 失败 rc={rc}')
    print('\n=== csres ICS 采集 ===')
    rc = run(f'{PYTHON} collect_csres.py ics')
    if rc != 0: print(f'!! ics 失败 rc={rc}')

def merge():
    """去重合并 biaozhun+csres 所有 txt 到 JSON"""
    print('=== 合并去重 ===')
    rc = run(f'{PYTHON} merge_standards.py')
    if rc != 0: return rc
    print('\n=== 转为仓库 schema ===')
    rc = run(f'{PYTHON} convert_to_db_schema.py')
    return rc

def build_db():
    """生成 standards.db (FTS5)"""
    print('=== 生成 standards.db ===')
    # 复制 JSON 到仓库目录
    src = r'C:\ZCODE\data\all_standards_merged_with_replacement.json'
    dst = os.path.join(REPO_DIR, 'all_standards_merged_with_replacement.json')
    import shutil
    shutil.copy(src, dst)
    print(f'已复制 {src} -> {dst}')
    # 跑仓库的 init_sqlite_fts.py
    return run(f'{PYTHON} {os.path.join(REPO_DIR, "init_sqlite_fts.py")}', cwd=REPO_DIR)

def push():
    """上传 standards.db + JSON 到 GitHub"""
    print('=== 上传到 GitHub ===')
    rc1 = run(f'{PYTHON} gh_push.py {os.path.join(REPO_DIR, "standards.db")} standards.db')
    rc2 = run(f'{PYTHON} gh_push.py {os.path.join(REPO_DIR, "all_standards_merged_with_replacement.json")} all_standards_merged_with_replacement.json')
    return rc1 or rc2

def all_steps():
    collect_biaozhun()
    collect_csres()
    merge()
    build_db()
    push()

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['collect_biaozhun','collect_csres','merge','build_db','push','all'])
    args = ap.parse_args()
    {
        'collect_biaozhun': collect_biaozhun,
        'collect_csres': collect_csres,
        'merge': merge,
        'build_db': build_db,
        'push': push,
        'all': all_steps,
    }[args.cmd]()

if __name__ == '__main__':
    main()
