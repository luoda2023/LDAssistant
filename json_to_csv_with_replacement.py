#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从JSON生成带replacement列的CSV
运行:
  python json_to_csv_with_replacement.py
"""
import json
import csv
from pathlib import Path

JSON_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_with_replacement.json")
CSV_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_with_replacement.csv")

with open(JSON_FILE, 'r', encoding='utf-8') as f:
    data = json.load(f)

fieldnames = ['code', 'name', 'publisher', 'implement_date', 'status', 'detail_url',
              'replacement_raw', 'replacement_parsed']

with open(CSV_FILE, 'w', encoding='utf-8-sig', newline='') as f:
    writer = csv.DictWriter(f, fieldnames=fieldnames)
    writer.writeheader()
    for row in data:
        writer.writerow({k: row.get(k, '') for k in fieldnames})

print(f"CSV written: {CSV_FILE}")
print(f"Total rows: {len(data)}")
