#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从csres.com详情页提取"替代情况"字段
格式: "代替95S516;被23S516代替"
"""
import asyncio
import json
import re
import sys
import time
from pathlib import Path

from playwright.async_api import async_playwright

# Config
DATA_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_20260629_092235.json")
OUTPUT_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_with_replacement.json")
BATCH_SIZE = 500        # 每批处理的URL数
CONCURRENCY = 10        # 同时打开的页面数（Playwright单实例）
DELAY_MIN = 0.1         # 页面间最小间隔
DELAY_MAX = 0.5         # 页面间最大随机间隔

# Regex patterns for replacement info
REPLACEMENT_PATTERN = re.compile(
    r'替代情况[：:]\s*(.+?)(?=\s*发布部门|作废日期|起草单位|页数|书号|$)',
    re.DOTALL
)


def extract_replacement(html: str) -> str:
    """从HTML中提取替代情况字段"""
    # Try 1: Direct text pattern (most common)
    m = REPLACEMENT_PATTERN.search(html)
    if m:
        text = m.group(1).strip()
        # Clean up: remove HTML tags, extra whitespace
        text = re.sub(r'<[^>]+>', '', text)
        text = re.sub(r'\s+', '', text)
        if text:
            return text
    
    # Try 2: looser pattern - look for "代替" and "被" keywords near each other
    m2 = re.search(r'替代情况.{0,5}[：:]\s*(.+?)(?=发布部门)', html, re.DOTALL)
    if m2:
        text = m2.group(1)
        text = re.sub(r'<[^>]+>', '', text)
        text = re.sub(r'\s+', '', text)
        if text:
            return text
    
    return ""


async def fetch_and_extract(page, url: str) -> str:
    """Navigate to URL and extract replacement info"""
    try:
        await page.goto(url, wait_until="domcontentloaded", timeout=15000)
        html = await page.content()
        return extract_replacement(html)
    except Exception as e:
        return f"ERROR:{str(e)[:50]}"


async def process_batch(page, urls: list) -> dict:
    """Process a batch of URLs"""
    results = {}
    for url in urls:
        replacement = await fetch_and_extract(page, url)
        results[url] = replacement
        await asyncio.sleep(DELAY_MIN + (DELAY_MAX - DELAY_MIN) * (0.5 + 0.5 * (hash(url) % 100) / 100))
    return results


async def main():
    print(f"Loading data from {DATA_FILE}")
    start_time = time.time()
    
    with open(DATA_FILE, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    # Filter records with csres.com URLs
    csres_records = [(i, r) for i, r in enumerate(data) 
                     if r.get('detail_url', '').startswith('http://www.csres.com')]
    print(f"Total records: {len(data)}")
    print(f"CSRES records to process: {len(csres_records)}")
    
    # Collect all URLs
    url_to_index = {}
    for i, r in csres_records:
        url_to_index[r['detail_url']] = i
    
    urls = list(url_to_index.keys())
    total = len(urls)
    processed = 0
    
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        page = await browser.new_page()
        
        # Process in batches
        batch_count = (total + BATCH_SIZE - 1) // BATCH_SIZE
        
        for batch_idx in range(batch_count):
            batch_start = batch_idx * BATCH_SIZE
            batch_end = min(batch_start + BATCH_SIZE, total)
            batch_urls = urls[batch_start:batch_end]
            
            batch_results = await process_batch(page, batch_urls)
            
            # Update data
            for url, replacement in batch_results.items():
                idx = url_to_index[url]
                if replacement and not replacement.startswith("ERROR"):
                    data[idx]['replacement'] = replacement
                else:
                    data[idx]['replacement'] = ""
            
            processed = batch_end
            elapsed = time.time() - start_time
            remaining = (total - processed) * elapsed / processed if processed > 0 else 0
            
            print(f"Batch {batch_idx+1}/{batch_count}: "
                  f"{processed}/{total} ({100*processed/total:.1f}%) "
                  f"| Elapsed: {elapsed:.0f}s | ETA: {remaining:.0f}s")
            
            # Save checkpoint every 10 batches
            if (batch_idx + 1) % 10 == 0:
                print(f"  Saving checkpoint...")
                with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
                    json.dump(data, f, ensure_ascii=False, indent=None, separators=(',', ':'))
        
        await browser.close()
    
    # Final save
    print(f"\nSaving final result...")
    with open(OUTPUT_FILE, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=None, separators=(',', ':'))
    
    elapsed = time.time() - start_time
    print(f"\nDone! Total time: {elapsed:.0f}s ({elapsed/60:.1f} min)")
    print(f"Output: {OUTPUT_FILE}")


if __name__ == "__main__":
    asyncio.run(main())
