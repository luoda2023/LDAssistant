import urllib.request, sys, os
url = sys.argv[1]
out = sys.argv[2]
print(f'Downloading {url}...')
req = urllib.request.Request(url, headers={'User-Agent': 'Python-urllib/3.12'})
with urllib.request.urlopen(req, timeout=120) as resp:
    data = resp.read()
    print(f'Downloaded {len(data)} bytes')
    with open(out, 'wb') as f:
        f.write(data)
print('Download complete')
