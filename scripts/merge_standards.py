"""去重合并 biaozhun 6个分类 + csres 数据为标准数据库"""
import os, sys, re, json
sys.stdout.reconfigure(encoding='utf-8')

DATA_DIR = 'C:/ZCODE/data'
FIELDS = ['标准名称', '标准编号', '标准简介', '标准状态', '现行或作废',
          '替代情况', '中标分类', 'ICS分类', '发布部门', '发布日期', '实施日期']

def parse_standards_file(filepath):
    """从 standard_s.txt 文件解析记录列表"""
    records = []
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    # 块分割（每块由11个字段组成）
    blocks = content.strip().split('\n\n')
    for block in blocks:
        rec = {}
        for line in block.strip().split('\n'):
            for field in FIELDS:
                m = re.match(rf'^{re.escape(field)}\s*[：:]\s*(.*)$', line)
                if m:
                    rec[field] = m.group(1).strip()
                    break
        if rec.get('标准编号'):
            records.append(rec)
    return records

def main():
    all_stats = {}
    all_records = []
    seen = set()  # 标准编号去重
    
    for f in sorted(os.listdir(DATA_DIR)):
        if f.endswith('_standards.txt') and f.startswith('biaozhun'):
            path = os.path.join(DATA_DIR, f)
            records = parse_standards_file(path)
            cat = f.replace('biaozhun_','').replace('_standards.txt','')
            all_stats[cat] = len(records)
            new = 0
            for rec in records:
                code = rec.get('标准编号', '')
                if code and code not in seen:
                    seen.add(code)
                    all_records.append(rec)
                    new += 1
            print(f'{cat}: {len(records)}条 -> 新增{new}条')
    
    # 输出合并结果
    total = len(all_records)
    print(f'\n--- 去重后合计 {total} 条 ---')
    
    # 保存合并JSON
    outpath = os.path.join(DATA_DIR, 'standards_merged.json')
    with open(outpath, 'w', encoding='utf-8') as f:
        json.dump(all_records, f, ensure_ascii=False, indent=2)
    print(f'合并JSON已保存: {outpath}')
    
    # 保存合并文本
    outpath2 = os.path.join(DATA_DIR, 'standards_merged.txt')
    with open(outpath2, 'w', encoding='utf-8') as f:
        for rec in all_records:
            for field in FIELDS:
                f.write(f'{field} ：{rec.get(field, "")}\n')
            f.write('\n')
    print(f'合并文本已保存: {outpath2}')

if __name__ == '__main__':
    main()
