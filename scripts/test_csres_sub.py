import sys
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, r'C:\ZCODE\scripts')
from collect_csres import get_subcategories
subs = get_subcategories('A')
print(f'A类子类数: {len(subs)}')
for s in subs[:5]: print(f'  {s[1]}: {s[2][:30]}')
