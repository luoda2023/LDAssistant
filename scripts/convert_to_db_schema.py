"""把 standards_merged.json 转换为 init_sqlite_fts.py 期望的 schema:
   all_standards_merged_with_replacement.json
字段映射:
  标准编号 -> code
  标准名称 -> name
  发布部门 -> publisher
  实施日期 -> implement_date
  标准状态 -> status
  (URL)    -> detail_url
  替代情况 -> replacement_raw
  (空)     -> replacement_parsed
"""
import json, os, sys, re
sys.stdout.reconfigure(encoding='utf-8')

DATA_DIR = 'C:/ZCODE/data'
SRC = os.path.join(DATA_DIR, 'standards_merged.json')
DST = os.path.join(DATA_DIR, 'all_standards_merged_with_replacement.json')

# 读取合并的 biaozhun + csres 数据
with open(SRC, 'r', encoding='utf-8') as f:
    biaozhun_records = json.load(f)

print(f'biaozhun+csres合并: {len(biaozhun_records)} 条')

# 同时收集 csres 已生成的文件
all_records = {}

# 转 biaozhun
for rec in biaozhun_records:
    code = rec.get('标准编号', '').strip()
    name = rec.get('标准名称', '').strip()
    if not code and not name:
        continue
    item = {
        'code': code,
        'name': name,
        'publisher': rec.get('发布部门', '').strip(),
        'implement_date': rec.get('实施日期', '').strip(),
        'status': rec.get('标准状态', '').strip() or rec.get('现行或作废', '').strip(),
        'detail_url': rec.get('来源URL', '').strip() if rec.get('来源URL') else '',
        'replacement_raw': rec.get('替代情况', '').strip(),
        'replacement_parsed': '',
        'ccs': rec.get('中标分类', '').strip(),
        'ics': rec.get('ICS分类', '').strip(),
        'publish_date': rec.get('发布日期', '').strip(),
        'source_type': 'biaozhun',
    }
    key = (code, name)
    all_records[key] = item

# 收集 csres 各字母文件
import os
for f in sorted(os.listdir(DATA_DIR)):
    if f.startswith('csres_') and f.endswith('_standards.txt'):
        path = os.path.join(DATA_DIR, f)
        with open(path, 'r', encoding='utf-8') as fp:
            content = fp.read()
        # 检测字段分隔符
        # csres 文件格式: 标准 ：值\n标准编号 ：XXX ...
        # 简单按空行分割
        blocks = content.strip().split('\n\n')
        for blk in blocks:
            item = {k: '' for k in ['标准名称','标准编号','标准状态','现行或作废','替代情况',
                                  '中标分类','ICS分类','发布部门','发布日期','实施日期','来源URL']}
            for line in blk.strip().split('\n'):
                m = re.match(r'^(\S+\s*?)\s*[：:]\s*(.*)$', line)
                if m:
                    k = m.group(1).strip()
                    v = m.group(2).strip()
                    if k in item: item[k] = v
            code = item.get('标准编号', '').strip()
            name = item.get('标准名称', '').strip()
            if not code and not name:
                continue
            key = (code, name)
            if key in all_records:
                # 合并字段
                rec = all_records[key]
                for k, v in item.items():
                    if v and not rec.get(_map_field(k)):
                        rec[_map_field(k)] = v
            else:
                new_item = {
                    'code': code,
                    'name': name,
                    'publisher': item.get('发布部门', '').strip(),
                    'implement_date': item.get('实施日期', '').strip(),
                    'status': item.get('标准状态', '').strip() or item.get('现行或作废', '').strip(),
                    'detail_url': item.get('来源URL', '').strip(),
                    'replacement_raw': item.get('替代情况', '').strip(),
                    'replacement_parsed': '',
                    'ccs': item.get('中标分类', '').strip(),
                    'ics': item.get('ICS分类', '').strip(),
                    'publish_date': item.get('发布日期', '').strip(),
                    'source_type': 'csres',
                }
                all_records[key] = new_item

# 输出
out = list(all_records.values())
with open(DST, 'w', encoding='utf-8') as f:
    json.dump(out, f, ensure_ascii=False, indent=2)
print(f'输出 {len(out)} 条到: {DST}')

def _map_field(k):
    return {
        '标准名称':'name','标准编号':'code','发布部门':'publisher','实施日期':'implement_date',
        '标准状态':'status','来源URL':'detail_url','替代情况':'replacement_raw',
        '中标分类':'ccs','ICS分类':'ics','发布日期':'publish_date',
    }.get(k, k)
