"""v3 最终合并 + 重建DB + 上传"""
import json, sqlite3, os, sys, hashlib, base64, shutil, requests
sys.stdout.reconfigure(encoding='utf-8')

# === 1. 重建 standards.db ===
DB_PATH = r'C:\ZCODE\github_repo\standards.db'
if os.path.exists(DB_PATH):
    os.remove(DB_PATH)

data = json.load(open(r'C:\ZCODE\data\all_standards_v3.json','r',encoding='utf-8'))
print(f'加载JSON: {len(data)} 条')

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()
cur.execute('''CREATE TABLE standards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT UNIQUE,
    name TEXT,
    publisher TEXT,
    implement_date TEXT,
    status TEXT,
    detail_url TEXT,
    replacement_raw TEXT,
    replacement_parsed TEXT
)''')
ok = 0; dup = 0
for r in data:
    code = (r.get('code','') or '').strip()
    if not code:
        continue
    try:
        cur.execute('''INSERT INTO standards(code,name,publisher,implement_date,status,detail_url,replacement_raw)
                       VALUES(?,?,?,?,?,?,?)''',
                    (code, r.get('name',''), r.get('publisher',''),
                     r.get('implement_date',''), r.get('status',''), r.get('detail_url',''),
                     r.get('replacement_raw','')))
        ok += 1
    except sqlite3.IntegrityError:
        dup += 1
conn.commit()

# FTS5
cur.execute('''CREATE VIRTUAL TABLE standards_fts USING fts5(
    code,name,publisher,replacement_raw,detail_url,content=standards,content_rowid=id
)''')
cur.execute('''INSERT OR REPLACE INTO standards_fts(rowid,code,name,publisher,replacement_raw,detail_url)
    SELECT id,code,name,publisher,replacement_raw,detail_url FROM standards''')
conn.commit()
total_db = cur.execute('SELECT COUNT(*) FROM standards').fetchone()[0]
print(f'DB写入: ok={ok} dup={dup} 总条数={total_db}')
conn.close()

# === 2. 同步 JSON 到仓库 ===
shutil.copy2(r'C:\ZCODE\data\all_standards_v3.json', r'C:\ZCODE\github_repo\all_standards_v3.json')
os.makedirs(r'C:\ZCODE\github_repo\data', exist_ok=True)
shutil.copy2(r'C:\ZCODE\data\all_standards_v3.json', r'C:\ZCODE\github_repo\data\all_standards_v3.json')

# 计算sha
def sha_file(path):
    with open(path,'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()[:10]
    return ''

db_sha = sha_file(DB_PATH); db_size = os.path.getsize(DB_PATH)
json_sha = sha_file(r'C:\ZCODE\github_repo\all_standards_v3.json'); json_size = os.path.getsize(r'C:\ZCODE\github_repo\all_standards_v3.json')
print(f'standards.db: {db_size} bytes sha={db_sha}')
print(f'all_standards_v3.json: {json_size} bytes sha={json_sha}')

# === 3. 上传GitHub ===
GH_TOKEN = open(r'C:\ZCODE\config\github_token.txt','r').read().strip()
REPO = 'spxrg/standard_checker'

# 创建无代理session
sess = requests.Session()
sess.trust_env = False  # 关键!强制不走系统代理
sess.headers.update({'Authorization': f'token {GH_TOKEN}', 'Accept': 'application/vnd.github.v3+json', 'User-Agent': 'standard-checker-bot'})

for local, remote in [
    (DB_PATH, 'standards.db'),
    (r'C:\ZCODE\github_repo\all_standards_v3.json', 'all_standards_v3.json'),
    (r'C:\ZCODE\github_repo\data\all_standards_v3.json', 'data/all_standards_v3.json'),
]:
    url = f'https://api.github.com/repos/{REPO}/contents/{remote}'
    r = sess.get(url, timeout=30)
    sha_file = r.json().get('sha','') if r.status_code == 200 else ''
    with open(local,'rb') as f:
        content_b64 = base64.b64encode(f.read()).decode()
    payload = {'message': f'v3 merge: {total_db} unique standards from csres+biaozhun+openstd', 'content': content_b64}
    if sha_file:
        payload['sha'] = sha_file
    resp = sess.put(url, json=payload, timeout=120)
    print(f'  upload {remote}: {resp.status_code} {resp.json().get("content",{}).get("html_url","") if resp.status_code in (200,201) else resp.text[:100]}')
