#!/usr/bin/env python3
"""Fix literal newlines in string literals that should be \\n escape sequences."""
import re

with open('standard_checker_v2.py', 'rb') as f:
    data = f.read()

# Fix 1: text.count(' followed by actual newline
# Should be text.count('\\n')
data = re.sub(b"text.count\\(\\s*'\\r?\\n'\\s*\\)", b"text.count('\\\\n')", data)

# Fix 2: replace(' followed by actual newline
data = re.sub(b"replace\\(\\s*'\\r?\\n'\\s*,\\s*", b"replace('\\\\n', ", data)

# Fix 3: replace(" followed by actual newline
data = re.sub(b'replace\\(\\s*"\\r?\\n"\\s*,\\s*', b'replace("\\\\n", ', data)

with open('standard_checker_v2.py', 'wb') as f:
    f.write(data)
print("Fixed escape sequences")