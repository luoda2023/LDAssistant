"""B团队 - biaozhun.org 深度诊断脚本 v2
多层诊断：DNS / TCP / TLS / 浏览器 / HTTP / 重定向 / IP归属 / Wayback
输出报告到 C:\\ZCODE\\logs\\diag_biaozhun_v2.log
"""
import os
import sys
import socket
import ssl
import time
import json
import re
import subprocess
import urllib.request
import urllib.error

sys.stdout.reconfigure(encoding='utf-8')

LOG = r"C:\ZCODE\logs\diag_biaozhun_v2.log"
os.makedirs(os.path.dirname(LOG), exist_ok=True)

HOST = "www.biaozhun.org"
IP_KNOWN = "47.86.107.108"


def log(msg):
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    line = f"{ts} | {msg}"
    print(line, flush=True)
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(line + "\n")


log("=" * 70)
log("B团队 biaozhun.org 深度诊断 v2 启动")
log("=" * 70)


# ============= 1. DNS =============
log("=== 1. DNS 解析 ===")
try:
    ip = socket.gethostbyname(HOST)
    log(f"DNS解析: OK -> {ip}")
    if ip == IP_KNOWN:
        log(f"  与已知IP匹配: {IP_KNOWN}")
    else:
        log(f"  WARNING: 与已知IP({IP_KNOWN}) 不一致")
except Exception as e:
    log(f"DNS解析: FAIL {type(e).__name__}: {e}")

# 8.8.8.8 socket level DNS lookup via getaddrinfo
try:
    infos = socket.getaddrinfo(HOST, 443, proto=socket.IPPROTO_TCP)
    addrs = set(i[4][0] for i in infos)
    log(f"getaddrinfo返回: {addrs}")
except Exception as e:
    log(f"getaddrinfo FAIL: {e}")


# ============= 2. ICMP / TCP 端口 =============
def test_port(host, port, timeout=15):
    try:
        s = socket.create_connection((host, port), timeout=timeout)
        s.close()
        return "OK"
    except socket.timeout:
        return "TIMEOUT"
    except ConnectionRefusedError:
        return "REFUSED"
    except Exception as e:
        return f"FAIL {type(e).__name__}: {str(e)[:80]}"


log("=== 2. TCP 端口连通性 ===")
log(f"TCP 80  : {test_port(HOST, 80, 15)}")
log(f"TCP 443 : {test_port(HOST, 443, 15)}")

# Try alternate IPs - maybe one of the addresses returned tries multiple
for test_ip in [IP_KNOWN]:
    for port in [80, 443, 8080, 8443, 22]:
        log(f"  ip={test_ip} port={port}: {test_port(test_ip, port, 10)}")


# ============= 3. TLS 握手 (尝试各版本) =============
log("=== 3. TLS 握手测试（按版本） ===")
for ver_name, minv, maxv in [
    ("TLSv1.0", ssl.TLSVersion.TLSv1, ssl.TLSVersion.TLSv1_2),
    ("TLSv1.1", ssl.TLSVersion.TLSv1_1, ssl.TLSVersion.TLSv1_2),
    ("TLSv1.2", ssl.TLSVersion.TLSv1_2, ssl.TLSVersion.TLSv1_2),
    ("TLSv1.3", ssl.TLSVersion.TLSv1_3, ssl.TLSVersion.TLSv1_3),
]:
    try:
        ctx = ssl.create_default_context()
        ctx.minimum_version = minv
        ctx.maximum_version = maxv
        ctx.check_hostname = False  # try direct IP
        ctx.verify_mode = ssl.CERT_NONE
        s = socket.create_connection((IP_KNOWN, 443), timeout=15)
        ss = ctx.wrap_socket(s, server_hostname=HOST)
        cert = ss.getpeercert(binary_form=False)
        log(f"{ver_name}: OK (但不应能成功 - check是否真握手) | cipher={ss.cipher()}")
        ss.close()
    except socket.timeout:
        log(f"{ver_name}: TIMEOUT (TCP层就不可达)")
    except Exception as e:
        log(f"{ver_name}: FAIL {type(e).__name__}: {str(e)[:100]}")


# ============= 4. curl HTTP/HTTPS 多种姿势 =============
log("=== 4. curl HTTP 多种姿势 ===")
curl_targets = [
    ("直连HTTPS", "https://www.biaozhun.org/"),
    ("直连HTTP", "http://www.biaozhun.org/"),
    ("忽略证书", "https://www.biaozhun.org/ --insecure"),
    ("强制HTTP/2", "https://www.biaozhun.org/ --http2"),
    ("HTTP/3", "https://www.biaozhun.org/ --http3-only"),
    ("直IP+host头", f"https://{IP_KNOWN}/ -H 'Host: www.biaozhun.org' --resolve www.biaozhun.org:443:{IP_KNOWN}"),
]
for name, url in curl_targets:
    try:
        out = subprocess.run(
            ['curl.exe', '--connect-timeout', '20', '-sS', '-o', 'NUL',
             '-w', 'code=%{http_code} time=%{time_total} size=%{size_download} url=%{url_effective}',
             *url.split()],
            capture_output=True, text=True, timeout=25,
        )
        log(f"  {name}: {out.stdout.strip()}")
        if out.returncode != 0:
            log(f"    stderr: {out.stderr.strip()[:150]}")
    except subprocess.TimeoutExpired:
        log(f"  {name}: TIMEOUT (curl 25s)")
    except Exception as e:
        log(f"  {name}: ERR {type(e).__name__}: {str(e)[:80]}")


# ============= 5. Playwright 真浏览器 - 3种代理模式 =============
log("=== 5. Playwright Edge 浏览器测试 ===")
try:
    from playwright.sync_api import sync_playwright

    def browser_test(name, proxy=None, headless=True):
        try:
            with sync_playwright() as p:
                args_list = [
                    '--disable-blink-features=AutomationControlled',
                    '--disable-features=IsolateOrigins,site-per-process',
                ]
                launch_kwargs = dict(channel='msedge', headless=headless, args=args_list)
                if proxy:
                    launch_kwargs['proxy'] = proxy
                b = p.chromium.launch(**launch_kwargs)
                ctx = b.new_context(
                    user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36',
                    viewport={'width': 1920, 'height': 1080},
                    locale='zh-CN',
                    timezone_id='Asia/Shanghai',
                )
                page = ctx.new_page()
                page.add_init_script("""
                Object.defineProperty(navigator,'webdriver',{get:()=>undefined});
                Object.defineProperty(navigator,'languages',{get:()=>['zh-CN','zh']});
                Object.defineProperty(navigator,'plugins',{get:()=>[1,2,3,4,5]});
                """)
                try:
                    resp = page.goto('https://www.biaozhun.org/', timeout=60000, wait_until='domcontentloaded')
                    content = page.content()
                    title = page.title()
                    log(f"  [{name}] OK | title={title[:40]} len={len(content)} status={resp.status if resp else 'NA'} url={page.url}")
                    return True, len(content), title
                except Exception as e:
                    log(f"  [{name}] FAIL {type(e).__name__}: {str(e)[:200]}")
                    return False, 0, ""
                finally:
                    b.close()
        except Exception as e:
            log(f"  [{name}] 浏览器启动失败: {type(e).__name__}: {str(e)[:120]}")
            return False, 0, ""

    browser_test("直连", proxy=None, headless=True)
    browser_test("系统代理", proxy={"server": "http://127.0.0.1:10808"}, headless=True)

    # 重复10次看是否偶发可用 - 直连
    log("  重复10次直连测试...")
    ok_count = 0
    for i in range(10):
        ok, ln, title = browser_test(f"直连#{i}", proxy=None, headless=True)
        if ok:
            ok_count += 1
            if ok_count >= 1:
                break  # 一次成功就停
    log(f"  10次重试成功率: {ok_count}/10 (但已中断首次成功)")

except Exception as e:
    log(f"Playwright 加载失败: {e}")


# ============= 6. HTTP 30x 重定向检查（用 curl -I 短超时） =============
log("=== 6. HTTP HEADER 检查（重定向、备用域名） ===")
alt_domains = [
    "www.biaozhun.org",
    "biaozhun.org",
    "m.biaozhun.org",
    "www.biaozhun.com",
    "biaozhun.com",
    "www.biaozhun.com.cn",
    "biaozhun.com.cn",
    "www.biaozhun.net",
]
for d in alt_domains:
    # try resolving+connecting first
    try:
        ip = socket.gethostbyname(d)
    except Exception as e:
        log(f"  {d}: DNS FAIL {e}")
        continue
    # just see status header
    out = subprocess.run(
        ['curl.exe', '--connect-timeout', '8', '-sS', '-o', 'NUL',
         '-w', 'code=%{http_code} ip=%{remote_ip}',
         '-I', f'https://{d}/'],
        capture_output=True, text=True, timeout=12,
    )
    log(f"  {d}: {out.stdout.strip() or out.stderr.strip()[:100]}")


# ============= 7. IP 归属 =============
log("=== 7. IP 归属查询 (47.86.107.108) ===")
ipinfo_url = f"https://ipinfo.io/{IP_KNOWN}/json"
opener = urllib.request.build_opener()
try:
    req = urllib.request.Request(ipinfo_url, headers={'User-Agent': 'curl/8.0'})
    resp = urllib.request.urlopen(req, timeout=20)
    body = resp.read().decode('utf-8')
    log(f"  ipinfo.io:\n  {body}")
except Exception as e:
    log(f"  ipinfo.io FAIL: {type(e).__name__}: {str(e)[:150]}")


# ============= 8. Wayback Machine 历史 =============
log("=== 8. Wayback Machine 历史快照检查 ===")
wb_urls = [
    f"https://archive.org/wayback/available?url=www.biaozhun.org",
    f"https://archive.org/wayback/available?url=www.biaozhun.org/guojia/",
    f"https://archive.org/wayback/available?url=www.biaozhun.org&timestamp=20240101000000",
    f"https://archive.org/wayback/available?url=www.biaozhun.org&timestamp=20250101000000",
    f"https://archive.org/wayback/available?url=biaozhun.org&timestamp=20250101000000",
]
for wb in wb_urls:
    try:
        req = urllib.request.Request(wb, headers={'User-Agent': 'Mozilla/5.0'})
        resp = urllib.request.urlopen(req, timeout=20)
        log(f"  [WB] {wb}\n    {resp.read().decode('utf-8')[:500]}")
    except Exception as e:
        log(f"  [WB] {wb} FAIL {type(e).__name__}: {str(e)[:100]}")

# Wayback CDX API - 列出所有历史快照
log("  CDX 列出所有抓取历史快照:")
cdx_url = ("http://web.archive.org/cdx/search/cdx?url=www.biaozhun.org/*"
           "&output=json&limit=10&from=20230101&to=20250101")
try:
    req = urllib.request.Request(cdx_url, headers={'User-Agent': 'Mozilla/5.0'})
    resp = urllib.request.urlopen(req, timeout=30)
    body = resp.read().decode('utf-8')
    log(f"  {body[:1500]}")
except Exception as e:
    log(f"  CDX FAIL: {type(e).__name__}: {str(e)[:200]}")


# ============= 9. 总体结论 =============
log("=" * 70)
log("诊断结束。")
log("=" * 70)
