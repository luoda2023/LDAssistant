# -*- mode: python ; coding: utf-8 -*-
import sys
from pathlib import Path
BASE_DIR = Path(".")
datas = [
    (str(BASE_DIR / "standards.dll"), "."),
    (str(BASE_DIR / "LDA.png"), "."),
    (str(BASE_DIR / "decrypt_module.py"), "."),
]
a = Analysis(
    ["standard_checker.py"],
    pathex=["."],
    binaries=[],
    datas=datas,
    hiddenimports=["PIL","PIL._tkinter_finder","docx","docx.shared","docx.enum.text","docx.oxml","fitz","pymupdf"],
    hookspath=[],hooksconfig={},runtime_hooks=[],excludes=["tkinter","test","distutils","setuptools","pip","numpy"],noarchive=False)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, a.binaries, a.datas, [], name="LDAssistant", debug=False, bootloader_ignore_signals=False, strip=False, upx=True, console=False, disable_windowed_traceback=False, target_arch=None, target_platform=None)