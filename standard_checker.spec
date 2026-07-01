# -*- mode: python ; coding: utf-8 -*-

import sys
from pathlib import Path

# Correct paths
MAIN_SCRIPT = Path(r"J:/WorkBuddy-work/csres-standards/standard_checker.py")
DATA_FILE = Path(r"J:/WorkBuddy-work/csres-standards/all_standards_merged_20260629_092235.json")
OCR_DIR = Path(r"D:/Program Files/图片文字识别/UmiOCR-data/plugins/win7_x64_PaddleOCR-json")

print(f"Main script: {MAIN_SCRIPT}")
print(f"Data file: {DATA_FILE}")
print(f"OCR dir: {OCR_DIR}")

# Collect data files
datas = []
binaries = []

if OCR_DIR.exists():
    # Include entire OCR directory (exe + models + configs) into ocr/
    for item in OCR_DIR.rglob("*"):
        if item.is_file():
            rel = item.relative_to(OCR_DIR)
            # Preserve directory structure under ocr/
            dest = f"ocr/{rel.parent}" if rel.parent != Path('.') else "ocr"
            binaries.append((str(item), dest))
    print(f"Included OCR directory: {OCR_DIR}")
else:
    print(f"WARNING: OCR directory not found: {OCR_DIR}")

if DATA_FILE.exists():
    datas.append((str(DATA_FILE), "data"))
    print(f"Included data file: {DATA_FILE}")
else:
    print(f"WARNING: Data file not found: {DATA_FILE}")

a = Analysis(
    [str(MAIN_SCRIPT)],
    pathex=[str(MAIN_SCRIPT.parent)],
    binaries=binaries,
    datas=datas,
    hiddenimports=[
        "PIL",
        "PIL._tkinter_finder",
        "docx",
        "docx.shared",
        "docx.enum.text",
        "docx.oxml",
        "fitz",
        "pymupdf",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="工程助手",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    target_platform=None,
)
