import sys, os, sqlite3
sys.stdout.reconfigure(encoding='utf-8')
db = r'C:\ZCODE\github_repo\standards.db'
conn = sqlite3.connect(db)
cur = conn.execute('SELECT COUNT(*) FROM standards')
print(f'standards表: {cur.fetchone()[0]} 条')
cur = conn.execute('SELECT COUNT(*) FROM standards_fts')
print(f'FTS5表: {cur.fetchone()[0]} 条')
cur = conn.execute('SELECT code, name, status FROM standards LIMIT 3')
for r in cur: print(f'  [{r[0]}] {r[1][:30]} | {r[2]}')
cur = conn.execute('SELECT code, name FROM standards_fts WHERE standards_fts MATCH ? LIMIT 3', ('标准',))
rows = cur.fetchall()
print(f'FTS5搜索"标准": {len(rows)} 条')
conn.close()
