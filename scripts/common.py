"""公共采集模块 - 工程师共用工具"""
import os
import re
import time
import random
import requests
from bs4 import BeautifulSoup

# 工作目录
BASE_DIR = r"C:\ZCODE"
DATA_DIR = os.path.join(BASE_DIR, "data")
LOG_DIR = os.path.join(BASE_DIR, "logs")
os.makedirs(DATA_DIR, exist_ok=True)
os.makedirs(LOG_DIR, exist_ok=True)

# User-Agent 池
UA_LIST = [
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36 Edg/119.0.0.0",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36",
]

# 字段顺序（严格按用户要求）
FIELDS = [
    "标准名称",
    "标准编号",
    "标准简介",
    "标准状态",
    "现行或作废",
    "替代情况",
    "中标分类",
    "ICS分类",
    "发布部门",
    "发布日期",
    "实施日期",
]

def get_ua():
    return random.choice(UA_LIST)

def fetch(url, timeout=30, retries=3, encoding=None):
    """带重试的请求"""
    last_err = None
    for i in range(retries):
        try:
            r = requests.get(url, timeout=timeout, headers={
                "User-Agent": get_ua(),
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            })
            if encoding:
                r.encoding = encoding
            else:
                if not r.encoding or r.encoding.lower() == 'iso-8859-1':
                    # 通过meta或content推断
                    ct = r.headers.get('Content-Type', '')
                    if 'utf-8' in ct.lower():
                        r.encoding = 'utf-8'
                    elif 'gbk' in ct.lower() or 'gb2312' in ct.lower():
                        r.encoding = 'gb18030'
                    else:
                        # 嗅探
                        head = r.content[:500].decode('ascii', errors='ignore').lower()
                        if 'gbk' in head or 'gb2312' in head:
                            r.encoding = 'gb18030'
                        else:
                            r.encoding = 'utf-8'
            return r.text
        except Exception as e:
            last_err = e
            wait = 2 * (i + 1) + random.uniform(0, 1)
            time.sleep(wait)
    raise last_err

def parse_html(html, parser='lxml'):
    return BeautifulSoup(html, parser)

def save_record(filepath, record):
    """以键值对格式追加一条标准到文件
    record: dict {字段名: 值}
    """
    lines = []
    for f in FIELDS:
        v = record.get(f, "") or ""
        v = str(v).strip()
        v = re.sub(r'[\r\n\t]+', ' ', v)
        lines.append(f"{f} ：{v}")
    lines.append("")  # 空行分隔
    with open(filepath, 'a', encoding='utf-8') as f:
        f.write("\n".join(lines) + "\n")

def load_done_ids(filepath):
    """加载已采集的标准编号集合（断点续传）"""
    done = set()
    if not os.path.exists(filepath):
        return done
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    # 匹配 标准编号 ：XXX
    for m in re.finditer(r'^标准编号\s*[：:]\s*(.+?)\s*$', content, re.MULTILINE):
        code = m.group(1).strip()
        if code:
            done.add(code)
    return done

def load_done_urls(filepath):
    """加载已采集的标准URL集合（断点续传）——从日志中提取URL更可靠。
    这里用文件里的"标准编号 ：" 做key，而采集时用 url 做 done。
    既然详情数据没记录URL，我们采用：从已有文件中读取 标准编号 的集合，匹配时用 标准编号 排除。
    """
    return load_done_ids(filepath)

def log_line(logfile, msg):
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    line = f"{ts} | {msg}"
    with open(logfile, 'a', encoding='utf-8') as f:
        f.write(line + "\n")
    print(line, flush=True)

def polite_sleep(min_s=0.8, max_s=1.5):
    time.sleep(random.uniform(min_s, max_s))
