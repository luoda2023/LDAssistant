#!/usr/bin/env python3
import hashlib, os
_SEED = b'LDAssistant_Secret_Key_2026_v2'
_SALT = b'LDAssistant_Salt_2026!'
_KEY = hashlib.pbkdf2_hmac('sha256', _SEED, _SALT, 100000, dklen=32)
def encrypt_file(src, dst):
    with open(src, 'rb') as f: data = f.read()
    result = bytearray(len(data))
    for i in range(0, len(data), 32):
        for j, b in enumerate(data[i:i+32]):
            result[i+j] = b ^ _KEY[j % 32]
    os.makedirs(os.path.dirname(dst) or '.', exist_ok=True)
    with open(dst, 'wb') as f: f.write(bytes(result))
    print(f'Encrypted {src} -> {dst} ({len(data)} bytes)')
if __name__ == '__main__':
    encrypt_file('standards.db', 'standards.dll')