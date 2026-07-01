import urllib.request, urllib.parse, sqlite3, re, json, os, sys, concurrent.futures, time, random, signal
signal.signal(signal.SIGINT, lambda s,f: sys.exit(0))
TOTAL_PAGES = 15052; URL = 'http://www.csres.com/s.jsp?pageNum={}'; WORKERS = 10
DB = 'standards.db'; PROG = 'progress.json'
HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0'}
def parse(html):
    rows = re.findall(r'<tr[^>]*>(.*?)</tr>', html, re.DOTALL)
    res = []
    for row in rows:
        cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.DOTALL)
        if len(cells) < 6: continue
        code = re.sub(r'<[^>]+>', '', cells[0]).strip()
        name = re.sub(r'<[^>]+>', '', cells[1]).strip()
        pub = re.sub(r'<[^>]+>', '', cells[2]).strip()
        date = re.sub(r'<[^>]+>', '', cells[3]).strip()
        status = re.sub(r'<[^>]+>', '', cells[4]).strip()
        m = re.search(r'href="([^"]*)"', cells[5])
        url = m.group(1) if m else ''
        if url and not url.startswith('http'):
            url = 'http://www.csres.com' + url if url.startswith('/') else 'http://www.csres.com/' + url
        res.append([code, name, pub, date, status, url])
    return res
def fetch(pg):
    for at in range(3):
        try:
            req = urllib.request.Request(URL.format(pg), headers=HEADERS)
            with urllib.request.urlopen(req, timeout=30) as r:
                return parse(r.read().decode('gbk', 'replace')), None
        except Exception as e:
            if at < 2: time.sleep(random.uniform(1, 3))
    return [], 'Failed after 3 retries'
def main():
    conn = sqlite3.connect(DB, timeout=120)
    conn.execute('CREATE TABLE IF NOT EXISTS standards (id INTEGER PRIMARY KEY AUTOINCREMENT,code TEXT,name TEXT,publisher TEXT,implement_date TEXT,status TEXT,detail_url TEXT UNIQUE,replacement_raw TEXT,replacement_parsed TEXT)')
    conn.execute('CREATE INDEX IF NOT EXISTS idx_url ON standards(detail_url)'); conn.commit()
    done = set()
    if os.path.exists(PROG):
        try: done = set(json.load(open(PROG)))
        except: pass
    exist = set()
    for r in conn.execute('SELECT detail_url FROM standards WHERE detail_url IS NOT NULL'): exist.add(r[0])
    remain = [p for p in range(1, TOTAL_PAGES + 1) if p not in done]
    print('Total:{} Done:{} InDB:{} Remain:{}'.format(TOTAL_PAGES, len(done), len(exist), len(remain)), flush=True)
    if not remain: print('All done!', flush=True); conn.close(); return
    start = time.time(); total_new = 0; dc = 0
    with concurrent.futures.ThreadPoolExecutor(WORKERS) as ex:
        fm = {ex.submit(fetch, p): p for p in remain}
        for fut in concurrent.futures.as_completed(fm):
            p = fm[fut]; recs, err = fut.result(); dc += 1
            if err: print('FAIL p{}: {}'.format(p, err), flush=True); continue
            done.add(p); n = 0
            for r in recs:
                if r[5] and r[5] not in exist:
                    exist.add(r[5])
                    try:
                        conn.execute('INSERT OR IGNORE INTO standards(code,name,publisher,implement_date,status,detail_url) VALUES(?,?,?,?,?,?)', r)
                        if conn.total_changes: n += 1
                    except: pass
            total_new += n
            el = time.time() - start; rate = dc / el if el > 0 else 0; eta = (len(remain) - dc) / rate if rate > 0 else 0
            if dc % 50 == 0 or dc == len(remain):
                conn.commit(); json.dump(list(done), open(PROG, 'w'))
            if dc % 5 == 0 or dc == len(remain):
                print('{}/{} p{}:+{}/{} {:.1f}pg/s ETA:{:.0f}s'.format(dc, len(remain), p, n, len(recs), rate, eta), flush=True)
    conn.commit(); json.dump(list(done), open(PROG, 'w'))
    cnt = conn.execute('SELECT COUNT(*) FROM standards').fetchone()[0]
    print('OK! {} pages, {} records, {:.0f}s'.format(dc, cnt, time.time() - start), flush=True)
    conn.close()
if __name__ == '__main__': main()