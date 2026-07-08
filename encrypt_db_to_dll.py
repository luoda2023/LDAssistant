import os, sys, base64
from cryptography.fernet import Fernet
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

def derive_key(seed, salt):
    kdf = PBKDF2HMAC(algorithm=hashes.SHA256(), length=32, salt=salt, iterations=100000)
    return base64.urlsafe_b64encode(kdf.derive(seed.encode()))

if __name__ == '__main__':
    db = sys.argv[1] if len(sys.argv) > 1 else 'standards.db'
    out = sys.argv[2] if len(sys.argv) > 2 else 'standards.dll'
    seed = sys.argv[3] if len(sys.argv) > 3 else 'LDAssistant2026Secret'
    salt = b'LDAssistant20260701!'
    key = derive_key(seed, salt)
    with open(db, 'rb') as f: data = f.read()
    encrypted = Fernet(key).encrypt(data)
    with open(out, 'wb') as f: f.write(encrypted)
    print(f'Encrypted {db} -> {out} ({len(encrypted)} bytes)')
