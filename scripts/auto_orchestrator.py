"""全自动调度器
- 每60秒检测目标站点可达性
- 任一站点恢复 → 自动启动对应采集任务
- 所有任务完成后 → 自动合并 → 重建 standards.db → 上传 GitHub
- 状态机持久化到 state/auto_orchestrator.json，可重启继续

用法：
  python auto_orchestrator.py                 # 前台运行
  python auto_orchestrator.py --once          # 单次巡检（用于测试）
  python auto_orchestrator.py --interval 60   # 自定义巡检间隔
"""
import sys, os, json, time, subprocess, threading, argparse
from datetime import datetime
sys.stdout.reconfigure(encoding='utf-8')
import requests

HERE = os.path.dirname(os.path.abspath(__file__))
PYTHON = r'C:\Python312\python.exe'
STATE_DIR = os.path.join(HERE, '..', 'state')
STATE_FILE = os.path.join(STATE_DIR, 'auto_orchestrator.json')
LOG_DIR = os.path.join(HERE, '..', 'logs')
DATA_DIR = os.path.join(HERE, '..', 'data')
REPO_DIR = os.path.join(HERE, '..', 'github_repo')

os.makedirs(STATE_DIR, exist_ok=True)
os.makedirs(LOG_DIR, exist_ok=True)

# 探测URL
BIAOZHUN_PROBE = 'https://www.biaozhun.org/guojia/'

# 站点任务配置
SITES = {
    'biaozhun_difang': {
        'probe_url': BIAOZHUN_PROBE,
        'probe_status': 200,
        'probe_min_len': 5000,
        'collect_cmd': f'{PYTHON} "{os.path.join(HERE, "collect_biaozhun_detail_only.py")}" all difang',
        'output_files': [os.path.join(DATA_DIR, 'biaozhun_difang_standards.txt')],
        'log_prefix': 'biaozhun_difang',
    },
    'biaozhun_qiye': {
        'probe_url': BIAOZHUN_PROBE,
        'probe_status': 200,
        'probe_min_len': 5000,
        'collect_cmd': f'{PYTHON} "{os.path.join(HERE, "collect_biaozhun_detail_only.py")}" all qiye',
        'output_files': [os.path.join(DATA_DIR, 'biaozhun_qiye_standards.txt')],
        'log_prefix': 'biaozhun_qiye',
    },
}

def log(msg):
    ts = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    line = f'{ts} | {msg}'
    print(line, flush=True)
    with open(os.path.join(LOG_DIR, 'auto_orchestrator.log'), 'a', encoding='utf-8') as f:
        f.write(line + '\n')

def load_state():
    if os.path.exists(STATE_FILE):
        with open(STATE_FILE, 'r', encoding='utf-8') as f:
            return json.load(f)
    return {
        'started_at': datetime.now().isoformat(),
        'last_probe': {},
        'site_status': {k: 'pending' for k in SITES},  # pending|reachable|collecting|done|failed
        'site_started_at': {},
        'site_completed_at': {},
        'final_merge_done': False,
    }

def save_state(state):
    with open(STATE_FILE, 'w', encoding='utf-8') as f:
        json.dump(state, f, indent=2, ensure_ascii=False)

def probe(url):
    """探测一次URL，返回 (ok, status_code, length)"""
    try:
        r = requests.get(url, timeout=20, headers={'User-Agent': 'Mozilla/5.0'})
        return (True, r.status_code, len(r.text))
    except Exception as e:
        return (False, 0, str(e)[:80])

def run_background(name, cmd):
    """后台运行命令，返回进程对象"""
    log_file = os.path.join(LOG_DIR, f'auto_{name}.log')
    log(f'启动后台任务 {name}: {cmd}')
    f = open(log_file, 'a', encoding='utf-8')
    proc = subprocess.Popen(
        cmd, shell=True, cwd=HERE,
        stdout=f, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if sys.platform=='win32' else 0,
    )
    return proc

def wait_proc(proc, name, state, timeout_check=30):
    """等待进程完成，定期更新状态（独立线程）"""
    while proc.poll() is None:
        time.sleep(timeout_check)
    rc = proc.returncode
    state['site_status'][name] = 'done' if rc == 0 else 'failed'
    state['site_completed_at'][name] = datetime.now().isoformat()
    save_state(state)
    log(f'任务 {name} 完成 rc={rc}')

def all_done(state):
    """是否所有采集任务完成"""
    return all(state['site_status'][k] in ('done', 'failed') for k in SITES)

def final_pipeline():
    """采集全部完成后 → 合并 → 重建数据库 → 上传"""
    log('='*60)
    log('全部采集完成，启动最终流水线: merge → build_db → push')
    log('='*60)

    # 合并
    rc = subprocess.call(f'{PYTHON} "{os.path.join(HERE, "merge_standards.py")}"', shell=True, cwd=HERE)
    log(f'merge_standards 完成 rc={rc}')

    rc = subprocess.call(f'{PYTHON} "{os.path.join(HERE, "convert_to_db_schema.py")}"', shell=True, cwd=HERE)
    log(f'convert_to_db_schema 完成 rc={rc}')

    # 复制 JSON 到仓库目录
    import shutil
    src = os.path.join(DATA_DIR, 'all_standards_merged_with_replacement.json')
    dst = os.path.join(REPO_DIR, 'all_standards_merged_with_replacement.json')
    shutil.copy(src, dst)
    shutil.copy(src, os.path.join(REPO_DIR, 'data', 'all_standards_merged_20260629_092235.json'))
    log('JSON 已复制到仓库')

    # 重建 standards.db
    rc = subprocess.call(f'{PYTHON} "{os.path.join(REPO_DIR, "init_sqlite_fts.py")}"', shell=True, cwd=REPO_DIR)
    log(f'init_sqlite_fts 完成 rc={rc}')

    # 上传 GitHub
    rc1 = subprocess.call(f'{PYTHON} "{os.path.join(HERE, "gh_push.py")}" "{os.path.join(REPO_DIR, "standards.db")}" standards.db', shell=True, cwd=HERE)
    rc2 = subprocess.call(f'{PYTHON} "{os.path.join(HERE, "gh_push.py")}" "{os.path.join(REPO_DIR, "all_standards_merged_with_replacement.json")}" all_standards_merged_with_replacement.json', shell=True, cwd=HERE)
    log(f'push GitHub 完成 db={rc1} json={rc2}')

    log('='*60)
    log('全自动化流水线完成 ✅')
    log('='*60)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--once', action='store_true')
    ap.add_argument('--interval', type=int, default=60)
    args = ap.parse_args()

    state = load_state()
    log(f'启动自动调度器 interval={args.interval}s  state={state["site_status"]}')

    procs = {}  # name -> (proc, thread)

    while True:
        now = datetime.now().isoformat(timespec='seconds')
        log(f'---- 巡检 {now} ----')

        # 探测每个站点
        for name, cfg in SITES.items():
            status = state['site_status'][name]
            if status in ('collecting', 'done', 'failed'):
                log(f'  {name}: 状态={status}，跳过')
                continue

            ok, sc, length = probe(cfg['probe_url'])
            state['last_probe'][name] = {
                'time': now,
                'ok': ok, 'status': sc, 'length': str(length)[:60],
            }
            log(f'  {name}: probe ok={ok} status={sc} len={str(length)[:60]}')

            if ok and sc == cfg['probe_status'] and (
                    isinstance(length, int) and length >= cfg['probe_min_len']):
                log(f'  ✅ {name} 恢复！开始采集')
                # 根据name选择命令
                cmds = [cfg['collect_cmd']]
                # 串行子进程
                def runner(nm, cmdlist):
                    for c in cmdlist:
                        log(f'  [{nm}] 执行: {c}')
                        rc = subprocess.call(c, shell=True, cwd=HERE)
                        log(f'  [{nm}] 子流程结束 rc={rc}')
                    state['site_status'][nm] = 'done'
                    state['site_completed_at'][nm] = datetime.now().isoformat()
                    save_state(state)
                    log(f'  ✅ [{nm}] 全部完成')
                t = threading.Thread(target=runner, args=(name, cmds), daemon=True)
                state['site_status'][name] = 'collecting'
                state['site_started_at'][name] = now
                save_state(state)
                t.start()
                procs[name] = t
            else:
                log(f'  ❌ {name} 仍不可达')

        save_state(state)

        # 如果所有采集完成且最终流程没跑 → 跑最终流程
        if all_done(state) and not state['final_merge_done']:
            log('所有站点完成 → 启动最终流水线')
            try:
                final_pipeline()
            except Exception as e:
                log(f'最终流水线异常: {e}')
            state['final_merge_done'] = True
            save_state(state)

        if args.once:
            log('单次模式结束')
            break

        # 等待
        log(f'休眠 {args.interval}s ...\n')
        time.sleep(args.interval)

    log('调度器退出')

if __name__ == '__main__':
    main()
