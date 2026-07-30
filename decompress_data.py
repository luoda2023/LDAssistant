"""解压标准数据库压缩包"""
import gzip, os

src = 'data/standards_data.json.gz'
dst = 'data/all_standards_merged_20260629_092235.json'

if not os.path.exists(src):
    print(f'ERROR: {src} not found')
    exit(1)

with gzip.open(src, 'rb') as f:
    data = f.read()

with open(dst, 'wb') as f:
    f.write(data)

size = os.path.getsize(dst)
print(f'Decompressed: {size/1024/1024:.1f} MB ({len(data)/1024/1024:.1f} MB raw, {len(data)-size} bytes overhead)')