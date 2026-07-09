"""openstd 数据合并入 standards_merged.json 并重建 standards.db → 上传 GitHub

输入文件：
  - data/openstd_mandatory_standards.txt (58条)
  - data/openstd_recommend_p2_*_standards.txt (各 p.p2 5~49)
  - data/biaozhun_*_standards.txt (1660条已有)
  - data/standards_merged.json (现有 biaozhun 合并)

合并策略：
  - 优先使用 openstd 数据（更新更准）
  - biaozhun 数据保留作为补充（地方/团体/计量/行业标准 openstd 没覆盖）
  - 用标准编号去重
"""
import sys, os, json, re, glob
from datetime import datetime
sys.path.insert(0, r'C:\ZCODE\scripts')
sys.stdout.reconfigure(encoding='utf-8')

from common import FIELDS

DATA_DIR = r'C:\ZCODE\data'
REPO_DIR = r'C:\ZCODE\github_repo'

def parse_file(path):
    """解析旧 biaozhun 格式（标准名称 ：xxx\n标准编号 ：xxx\n...）"""
    if not os.path.exists(path):
        return []
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    records = []
    for block in content.split('\n\n'):
        if not block.strip(): continue
        rec = {}
        for line in block.split('\n'):
            m = re.match(r'^(\S+(?:\s\S+)*?)\s*[：:]\s*(.*)$', line)
            if m:
                key = m.group(1).strip()
                val = m.group(2).strip()
                rec[key] = val
        if '标准编号' in rec and rec['标准编号']:
            r = {f: '' for f in FIELDS}
            for k in FIELDS:
                r[k] = rec.get(k, '')
            records.append(r)
    return records

def main():
    # 1. 读 biaozhun 已合并 JSON
    src_merged = os.path.join(DATA_DIR, 'standards_merged.json')
    biaozhun = json.load(open(src_merged, 'r', encoding='utf-8'))
    print(f'biaozhun 旧合并: {len(biaozhun)} 条')

    # 2. 解析 openstd 文件
    openstd_recs = []
    files = []
    files.append(('mandatory', os.path.join(DATA_DIR, 'openstd_mandatory_standards.txt')))
    for p2 in range(5, 50):
        files.append((f'recommend_p2_{p2}', os.path.join(DATA_DIR, f'openstd_recommend_p2_{p2}_standards.txt')))
    files.append(('guide', os.path.join(DATA_DIR, 'openstd_guide_standards.txt')))

    for tag, fpath in files:
        if os.path.exists(fpath):
            recs = parse_file(fpath)
            if recs:
                openstd_recs.extend(recs)
                print(f'  {tag}: {len(recs)} 条')
    print(f'openstd 总记录: {len(openstd_recs)} 条')

    # 3. 合并：去重（code 优先）
    all_dict = {}
    # biaozhun 先入
    for r in biaozhun:
        code = r.get('标准编号','').strip()
        if not code: continue
        all_dict[code] = r
        all_dict[code]['_source'] = 'biaozhun'
    print(f'biaozhun 入库: {len(all_dict)} 条')

    # openstd 再覆盖（更新）
    openstd_added = 0; openstd_updated = 0
    for r in openstd_recs:
        code = r.get('标准编号','').strip()
        if not code: continue
        if code in all_dict:
            # 用 openstd 数据覆盖
            for k in FIELDS:
                if r.get(k):
                    all_dict[code][k] = r[k]
            all_dict[code]['_source'] = 'openstd_merged'
            openstd_updated += 1
        else:
            all_dict[code] = r
            all_dict[code]['_source'] = 'openstd'
            openstd_added += 1
    print(f'openstd 新增: {openstd_added} 条; 字段更新: {openstd_updated} 条')

    result = list(all_dict.values())
    print(f'合并后总条数: {len(result)}')

    # 4. 转换为仓库 schema (12字段)
    schema_recs = []
    for r in result:
        code = r.get('标准编号', '').strip()
        name = r.get('标准名称', '').strip()
        # 修复 difang 拆分
        if name and re.match(r'^\d', name):
            m = re.match(r'^(\d+(?:\.\d+)?-\d{4})\s+(.*)', name)
            if m:
                code = (code + ' ' + m.group(1)).strip()
                name = m.group(2).strip()
            else:
                m = re.match(r'^([\d\-/]+)\s+(.*)', name)
                if m:
                    code = (code + ' ' + m.group(1)).strip()
                    name = m.group(2).strip()
        item = {
            'code': code,
            'name': name,
            'publisher': r.get('发布部门', '').strip(),
            'implement_date': r.get('实施日期', '').strip(),
            'status': r.get('标准状态', '').strip() or r.get('现行或作废', '').strip(),
            'detail_url': '',
            'replacement_raw': r.get('替代情况', '').strip(),
            'replacement_parsed': '',
            'ccs': r.get('中标分类', '').strip(),
            'ics': r.get('ICS分类', '').strip(),
            'publish_date': r.get('发布日期', '').strip(),
            'source_type': r.get('_source', 'biaozhun'),
        }
        schema_recs.append(item)

    # 5. 输出 JSON
    out_json = os.path.join(DATA_DIR, 'all_standards_merged_with_replacement.json')
    with open(out_json, 'w', encoding='utf-8') as f:
        json.dump(schema_recs, f, ensure_ascii=False, indent=2)
    print(f'JSON 已生成: {out_json}')

    # 6. 同步到仓库目录（多命名兼容）
    import shutil
    dst1 = os.path.join(REPO_DIR, 'all_standards_merged_with_replacement.json')
    dst2 = os.path.join(REPO_DIR, 'data', 'all_standards_merged_20260629_092235.json')
    shutil.copy(out_json, dst1)
    shutil.copy(out_json, dst2)
    print(f'JSON 已复制到: {dst1}, {dst2}')

    # 7. 重建 standards.db
    rc = os.system(f'C:\\Python312\\python.exe "{os.path.join(REPO_DIR, "init_sqlite_fts.py")}"')
    print(f'standards.db 重建 rc={rc}')

    # 8. 复制 db 到主目录
    shutil.copy(os.path.join(REPO_DIR, 'standards.db'),
                os.path.join(os.path.dirname(REPO_DIR), 'standards.db'))

    # 9. 上传 GitHub
    api_path = os.path.join(r'C:\ZCODE\scripts', 'gh_push.py')
    for f, gh_path in [
        ('standards.db', 'standards.db'),
        ('all_standards_merged_with_replacement.json', 'all_standards_merged_with_replacement.json'),
    ]:
        full_local = os.path.join(REPO_DIR, f)
        rc = os.system(f'C:\\Python312\\python.exe "{api_path}" "{full_local}" {gh_path}')
        print(f'  上传 {f}: rc={rc}')

    print(f'\n===== 合并+重建+上传完成 =====')
    print(f'最终: {len(schema_recs)} 条标准 (biaozhun {len(biaozhun)} + openstd 新增 {openstd_added})')

if __name__ == '__main__':
    main()
