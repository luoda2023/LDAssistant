#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从csres.com详情页批量提取"替代情况"字段
高效版：使用requests + 多线程
"""
import json
import re
import sys
import time
import threading
from pathlib import Path
from urllib.parse import urlparse

import requests
from bs4 import BeautifulSoup
from html import unescape

# Config
DATA_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_20260629_092235.json")
OUTPUT_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_with_replacement.json")
WORKER_COUNT = 30          # 并发线程数
BATCH_SIZE = 5000          # 每批处理数（进度显示）
TIMEOUT = 15               # 请求超时

# Regex for replacement info
REPLACEMENT_RE = re.compile(
    r'替代情况[：:]\s*(.+?)(?=\s*发布部门|作废日期|起草单位|页数|书号)',
    re.DOTALL
)

# Global lock for print
print_lock = threading.Lock()

# Session with cookie persistence
session_pool = {}
pool_lock = threading.Lock()

def get_session(tid):
    """Get a thread-local requests session"""
    if tid not in session_pool:
        with pool_lock:
            if tid not in session_pool:
                session_pool[tid] = requests.Session()
    return session_pool[tid]

def extract_replacement(html: str, encoding='gbk') -> str:
    """Extract replacement info from HTML content"""
    try:
        # Decode if bytes
        if isinstance(html, bytes):
            html = html.decode(encoding, errors='replace')
        
        # Remove HTML tags for cleaner text matching
        clean = re.sub(r'<[^>]+>', ' ', html)
        clean = re.sub(r'\s+', ' ', clean)
        # Decode HTML entities
        from html import unescape
        clean = unescape(clean)
        
        # Try regex
        m = REPLACEMENT_RE.search(clean)
        if m:
            text = m.group(1).strip()
            # Further clean: remove any remaining tags
            text = re.sub(r'\[.*?\]', '', text)
            if text:
                return text
        
        return ""
    except Exception as e:
        return f"PARSE_ERROR"


def fetch_replacement(url: str, tid: int) -> str:
    """Fetch a single URL and extract replacement info"""
    try:
        sess = get_session(tid)
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'zh-CN,zh;q=0.9',
        }
        resp = sess.get(url, headers=headers, timeout=TIMEOUT)
        # csres uses GBK encoding
        resp.encoding = 'gbk'
        html = resp.text
        
        replacement = extract_replacement(html, 'gbk')
        return replacement
    except Exception as e:
        return f"ERROR:{str(e)[:60]}"


def worker(thread_id, url_queue, results, counter):
    """Worker thread that processes URLs from queue"""
    while True:
        try:
            url = url_queue.get(timeout=5)
        except:
            break
        
        replacement = fetch_replacement(url, thread_id)
        results[url] = replacement
        
        counter[0] += 1
        if counter[0] % 500 == 0:
            with print_lock:
                print(f"  Progress: {counter[0]} processed")
        
        url_queue.task_done()


def main():
    print(f"Loading data from {DATA_FILE}...")
    start_time = time.time()
    
    with open(DATA_FILE, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # Separate records: csres and non-csres
    csres_indices = []
    url_to_index = {}
    for i, r in enumerate(data):
        url = r.get('detail_url', '')
        if url.startswith('http://www.csres.com'):
            csres_indices.append(i)
            url_to_index[url] = i
    
    total = len(csres_indices)
    print(f"Total records: {len(data)}")
    print(f"CSRES records to process: {total}")
    print(f"Workers: {WORKER_COUNT}")
    
    if total == 0:
        print("No CSRES records found!")
        return
    
    # Initialize results dict
    results = {}
    
    # Create queue
    import queue
    url_queue = queue.Queue()
    for url in url_to_index:
        url_queue.put(url)
    
    counter = [0]  # thread-safe via GIL for simple int
    
    # Start workers
    threads = []
    for tid in range(WORKER_COUNT):
        t = threading.Thread(target=worker, args=(tid, url_queue, results, counter))
        t.daemon = True
        t.start()
        threads.append(t)
    
    print("Starting extraction...")
    batch_progress = 0
    
    while not url_queue.empty() or any(t.is_alive() for t in threads):
        time.sleep(1)
        batch_progress += 1
        if batch_progress % 30 == 0:
            elapsed = time.time() - start_time
            with print_lock:
                pct = counter[0] / total * 100
                print(f"\n[Batch Check] {counter[0]}/{total} ({pct:.1f}%) | "
                      f"Queue remaining: {url_queue.qsize()} | "
                      f"Elapsed: {elapsed:.0f}s")
    
    # Wait for all threads
    for t in threads:
        t.join(timeout=10)
    
    print(f"\nExtraction complete. Total fetched: {len(results)}")
    elapsed_fetch = time.time() - start_time
    
    # Apply results to data
    success = 0
    errors = 0
    for url, replacement in results.items():
        idx = url_to_index[url]
        if replacement and not replacement.startswith("ERROR"):
            data[idx]['replacement'] = replacement
            if replacement:
                success += 1
        else:
            data[idx]['replacement'] = ""
            if replacement.startswith("ERROR"):
                errors += 1
    
    # Save output
    print(f"\nSaving output to {OUTPUT_FILE}...")
    with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=None, separators=(',', ':'))
    
    total_time = time.time() - start_time
    print(f"\n=== Summary ===")
    print(f"Total records: {len(data)}")
    print(f"CSRES records: {total}")
    print(f"Replacement extracted: {success}")
    print(f"Errors: {errors}")
    print(f"Time: {total_time:.0f}s ({total_time/60:.1f} min)")
    print(f"Output: {OUTPUT_FILE}")


if __name__ == "__main__":
    main()
