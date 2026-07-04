#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从csres.com详情页批量提取"替代情况"字段，增加2列:
  replacement_raw   - 原始文本，如 "代替95S516;被23S516代替"
  replacement_parsed - 结构化文本，如 "代替95S516;被23S516代替"

运行:
  python extract_replacement_v2.py
"""
import json
import re
import time
import threading
from pathlib import Path

import requests
from html import unescape

# Config
DATA_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_20260629_092235.json")
OUTPUT_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_with_replacement.json")
WORKER_COUNT = 30
TIMEOUT = 20
CHECKPOINT_INTERVAL = 2000  # save checkpoint every N records
PRINT_INTERVAL = 500        # print progress every N records

REPLACEMENT_RE = re.compile(
    r'替代情况[：:]\s*(.+?)(?=\s*发布部门|作废日期|起草单位|页数|书号)',
    re.DOTALL
)

print_lock = threading.Lock()


def extract_replacement(html: str) -> str:
    if isinstance(html, bytes):
        html = html.decode('gbk', errors='replace')
    clean = re.sub(r'<[^>]+>', ' ', html)
    clean = re.sub(r'\s+', ' ', clean)
    clean = unescape(clean)
    m = REPLACEMENT_RE.search(clean)
    if m:
        text = m.group(1).strip()
        text = re.sub(r'\[.*?\]', '', text)
        return text if text else ""
    return ""


def fetch_replacement(url: str, sess: requests.Session) -> str:
    try:
        resp = sess.get(url, timeout=TIMEOUT)
        resp.encoding = 'gbk'
        return extract_replacement(resp.text)
    except Exception as e:
        return f"ERROR:{str(e)[:50]}"


def worker(tid, url_queue, results, counter):
    sess = requests.Session()
    sess.headers.update({
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        'Accept': 'text/html,application/xhtml+xml',
        'Accept-Language': 'zh-CN,zh;q=0.9',
    })
    while True:
        try:
            url = url_queue.get(timeout=5)
        except Exception:
            break
        results[url] = fetch_replacement(url, sess)
        with print_lock:
            counter[0] += 1
            if counter[0] % PRINT_INTERVAL == 0:
                print(f"  Progress: {counter[0]} processed")
        url_queue.task_done()


def main():
    print(f"[1/4] Loading existing output from {OUTPUT_FILE}...")
    t0 = time.time()
    with open(OUTPUT_FILE, 'r', encoding='utf-8') as f:
        data = json.load(f)

    url_to_index = {}
    missing_urls = []
    for i, r in enumerate(data):
        url = r.get('detail_url', '')
        if url.startswith('http://www.csres.com'):
            url_to_index[url] = i
            # Skip if already has replacement data
            if not r.get('replacement_raw'):
                missing_urls.append(url)

    total = len(url_to_index)
    missing = len(missing_urls)
    print(f"Total records: {len(data)}, CSRES records: {total}")
    print(f"Already extracted: {total - missing}, Missing: {missing}")

    if missing == 0:
        print("All records already have replacement data!")
        return

    print(f"[2/4] Starting extraction of {missing} missing URLs with {WORKER_COUNT} workers...")
    import queue
    url_queue = queue.Queue()
    for url in missing_urls:
        url_queue.put(url)

    results = {}
    counter = [0]
    threads = []
    for tid in range(WORKER_COUNT):
        t = threading.Thread(target=worker, args=(tid, url_queue, results, counter), daemon=True)
        t.start()
        threads.append(t)

    last_save = 0
    while not url_queue.empty() or any(t.is_alive() for t in threads):
        time.sleep(1)
        if counter[0] - last_save >= CHECKPOINT_INTERVAL:
            last_save = counter[0]
            _apply_results(data, url_to_index, results)
            with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=None, separators=(',', ':'))
            with print_lock:
                print(f"  [CHECKPOINT] Saved at {counter[0]} records")

    for t in threads:
        t.join(timeout=10)

    print(f"[3/4] Applying results and saving...")
    _apply_results(data, url_to_index, results)
    with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=None, separators=(',', ':'))

    success = sum(1 for v in results.values() if v and not str(v).startswith("ERROR") and str(v).strip())
    errors = sum(1 for v in results.values() if str(v).startswith("ERROR"))
    elapsed = time.time() - t0
    print(f"\n=== Summary ===")
    print(f"Total records: {len(data)}")
    print(f"CSRES records: {total}")
    print(f"Missing URLs: {missing}")
    print(f"Successfully extracted: {success}")
    print(f"Errors: {errors}")
    print(f"Time: {elapsed:.0f}s ({elapsed/60:.1f} min)")
    print(f"Output: {OUTPUT_FILE}")


def _apply_results(data, url_to_index, results):
    """Apply fetched results to data records"""
    for url, replacement in results.items():
        idx = url_to_index[url]
        raw = replacement if replacement and not str(replacement).startswith("ERROR") else ""
        data[idx]['replacement_raw'] = raw
        # Parse into structured format
        data[idx]['replacement_parsed'] = _parse_replacement(raw)


def _parse_replacement(raw: str) -> str:
    """Parse raw replacement text into structured format"""
    if not raw:
        return ""
    # Normalize
    text = raw.strip()
    parts = [p.strip() for p in text.split(';') if p.strip()]
    return ';'.join(parts)


if __name__ == "__main__":
    main()
