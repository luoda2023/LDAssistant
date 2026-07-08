import hashlib
import os

_SEED = b'LDAssistant_Secret_Key_2026_v2'
_SALT = b'LDAssistant_Salt_2026!'
_KEY = None

def _get_key():
    global _KEY
    if _KEY is None:
        _KEY = hashlib.pbkdf2_hmac('sha256', _SEED, _SALT, 100000, dklen=32)
    return _KEY

def decrypt_dll(dll_path):
    if not os.path.exists(dll_path):
        raise FileNotFoundError(f"DLL not found: {dll_path}")
    key = _get_key()
    with open(dll_path, 'rb') as f:
        data = f.read()
    result = bytearray(len(data))
    for i in range(0, len(data), 32):
        chunk = data[i:i+32]
        for j, b in enumerate(chunk):
            result[i+j] = b ^ key[j % 32]
    return bytes(result)