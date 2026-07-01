# -*- mode: python ; coding: utf-8 -*-
import sys
from pathlib import Path

a = Analysis(
    ['standard_checker.py'],
    pathex=[str(Path.cwd())],
    binaries=[],
    datas=[
        ('standards.dll', '.'),
        ('LDA.png', '.'),
        ('decrypt_module.py', '.'),
    ],
    hiddenimports=[
        'PIL',
        'PIL._tkinter_finder',
        'docx',
        'docx.shared',
        'docx.enum.text',
        'docx.oxml',
        'requests',
        'xml',
        'xml.etree',
        'xml.etree.ElementTree',
        'lxml',
        'cssselect',
        'rapidocr_onnxruntime',
        'PyQt5',
        'PyQt5.QtCore',
        'PyQt5.QtGui',
        'PyQt5.QtWidgets',
        'sqlite3',
        'hashlib',
        'json',
        'csv',
        're',
    ],
    hookspath=[],
    hooksconfig={},
    excludes=[
        'tkinter',
        'matplotlib',
        'scipy',
        'notebook',
        'IPython',
        'pandas',
        'numpy',
        'PIL.ImageShow',
        'PIL.ImageGrab',
    ],
    runtime_hooks=[],
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='standard_checker',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='LDA.png',
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='standard_checker',
)