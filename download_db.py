"""下载并解压标准数据库"""
import urllib.request, gzip, os, sys

url = "https://github.com/luoda2023/LDAssistant/releases/download/data-v1/standards_gov_full.db.gz"
out = "all_standards_merged_20260629_092235.json"

print(f"Downloading {url}...")
req = urllib.request.Request(url, headers={"User-Agent": "Python-urllib/3.12"})
try:
    with urllib.request.urlopen(req, timeout=120) as resp:
        compressed = resp.read()
    print(f"Downloaded {len(compressed)} bytes")
    data = gzip.decompress(compressed)
    print(f"Decompressed {len(data)} bytes")
    with open(out, "wb") as f:
        f.write(data)
    print("Done")
except Exception as e:
    print(f"Error: {e}")
    sys.exit(1)