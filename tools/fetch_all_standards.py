import sqlite3
import requests
import gzip
import json
from pathlib import Path
BASE_DIR=Path(__file__).parent.parent
DB_FILE=BASE_DIR/'standards.db'

def main():
    existing=set()
    if DB_FILE.exists():
        conn=sqlite3.connect(str(DB_FILE))
        for row in conn.execute('SELECT DISTINCT code FROM standards'):
            existing.add(row[0])
        conn.close()
        print(f'Existing: {len(existing)} records')
    else:
        print('No existing database')
    print(f'Script ready. DB at {DB_FILE}')
if __name__=='__main__':
    main()
