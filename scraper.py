import urllib.request, urllib.parse, sqlite3, re, json, os, sys, concurrent.futures, time

TOTAL_PAGES = 15052
URL = "http://www.csres.com/s.jsp?pageNum={}"
WORKERS = 20
DB = "standards.db"
PROG = "progress.json"
HEADERS = {"User-Agent": "Mozilla/5.0"}

def parse(html):
    rc = []
    rows = re.findall(r'<tr[^>]*>(.*?)</tr>', html, re.DOTALL)
    for row in rows:
        cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.DOTALL)
        if len(cells) < 6: continue
        code = re.sub(r'<[^>]+>', '', cells[0]).strip()
        name = re.sub(r'<[^>]+>', '', cells[1]).strip()
        pub = re.sub(r'<[^>]+>', '', cells[2]).strip()
        date = re.sub(r'<[^>]+>', '', cells[3]).strip()
        status = re.sub(r'<[^>]+>', '', cells[4]).strip()
        href = ""
        m = re.search(r'href="([^"]*)"', cells[5])
        if m: href = m.group(1)
        if href and not href.startswith("http"):
            href = "http://www.csres.com" + href if href.startswith("/") else "http://www.csres.com/" + href
        rc.append([code, name, pub, date, status, href])
    return rc

def fetch(pg):
    try:
        req = urllib.request.Request(URL.format(pg), headers=HEADERS)
        with urllib.request.urlopen(req, timeout=30) as r:
            return parse(r.read().decode("gbk", "replace")), None
    except Exception as e:
        return [], str(e)

def main():
    conn = sqlite3.connect(DB, timeout=60)
    conn.execute("""CREATE TABLE IF NOT EXISTS standards (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        code TEXT, name TEXT, publisher TEXT, implement_date TEXT, status TEXT,
        detail_url TEXT UNIQUE, replacement_raw TEXT, replacement_parsed TEXT
    )""")
    conn.execute("CREATE INDEX IF NOT EXISTS idx_url ON standards(detail_url)")
    conn.commit()
    
    prog = set()
    if os.path.exists(PROG):
        try: prog = set(json.load(open(PROG)))
        except: pass
    
    existing = set()
    for row in conn.execute("SELECT detail_url FROM standards WHERE detail_url != ''"):
        existing.add(row[0])
    
    todo = [p for p in range(1, TOTAL_PAGES+1) if p not in prog]
    print(f"Already done: {len(prog)} pages, need: {len(todo)} pages, existing records: {len(existing)}", flush=True)
    
    batch = []
    failed = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=WORKERS) as ex:
        fs = {ex.submit(fetch, p): p for p in todo}
        for f in concurrent.futures.as_completed(fs):
            p = fs[f]
            rows, err = f.result()
            if err:
                failed.append(p)
                print(f"Failed page {p}: {err}", flush=True)
            else:
                batch.extend(rows)
                prog.add(p)
                if len(batch) >= 200:
                    _flush(conn, batch, existing)
                    batch.clear()
                if len(prog) % 100 == 0:
                    json.dump(list(prog), open(PROG, "w"))
                    print(f"Progress: {len(prog)}/{TOTAL_PAGES}, total records: {len(existing)}+{len(batch)}", flush=True)
    
    if batch:
        _flush(conn, batch, existing)
    
    json.dump(list(prog), open(PROG, "w"))
    
    total = conn.execute("SELECT COUNT(*) FROM standards").fetchone()[0]
    print(f"Done! Total pages: {len(prog)}, Total standards: {total}", flush=True)
    
    if failed:
        print(f"Failed pages: {len(failed)}: {failed[:20]}", flush=True)
    
    conn.close()

def _flush(conn, batch, existing):
    new = 0
    for row in batch:
        if row[5] and row[5] not in existing:
            try:
                conn.execute("INSERT INTO standards(code,name,publisher,implement_date,status,detail_url) VALUES(?,?,?,?,?,?)", row)
                existing.add(row[5])
                new += 1
            except: pass
    conn.commit()
    if new:
        print(f"Inserted {new} new records", flush=True)

if __name__ == "__main__":
    main()