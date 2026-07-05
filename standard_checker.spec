# -*- mode: python ; coding: utf-8 -*-
"""
工程助手 LDAssistant v10 — PyInstaller spec
使用 onedir 模式：主 EXE 仅 ~4MB，其余依赖放在 _internal/ 子目录。
"""
import sys
from pathlib import Path

# Paths — 使用 cwd() 以支持 GitHub Actions CI 环境
SCRIPT_DIR = Path.cwd().resolve()
MAIN_SCRIPT = SCRIPT_DIR / "standard_checker.py"
ICON_FILE = SCRIPT_DIR / "app_icon.ico"
LOGO_FILE = SCRIPT_DIR / "LDA.png"

# 数据库文件 — 依次尝试多个候选路径
DB_CANDIDATES = [
    SCRIPT_DIR / "standards_new.db",
    SCRIPT_DIR / "standards.db",
    SCRIPT_DIR / "data" / "standards_new.db",
    SCRIPT_DIR / "data" / "standards.db",
]
DB_FILE = None
for _p in DB_CANDIDATES:
    if _p.exists():
        DB_FILE = _p
        break

# OCR 引擎路径 — 优先同目录 ocr/，不存在则跳过（CI 环境无 OCR）
OCR_DIR = SCRIPT_DIR / "ocr"
PADDLE_OCR_EXE = OCR_DIR / "PaddleOCR-json.exe"
if PADDLE_OCR_EXE.exists():
    OCR_ROOT = OCR_DIR
    print(f"Using portable OCR: {OCR_ROOT}")
else:
    OCR_ROOT = None
    print("OCR engine not found — build will exclude OCR (CI should download it via setup step)")

# ODA File Converter 引擎路径（DWG → PDF 转换，可选）
ODA_DIR = SCRIPT_DIR / "oda_converter"
ODA_EXE = ODA_DIR / "ODAFileConverter.exe"
if ODA_EXE.exists():
    ODA_ROOT = ODA_DIR
    print(f"Using bundled ODA converter: {ODA_ROOT} ({ODA_EXE.stat().st_size / 1024 / 1024:.0f} MB)")
else:
    ODA_ROOT = None
    print("ODA converter not bundled — DWG preview fallback to DXF rendering + AutoCAD COM")

print(f"Main script: {MAIN_SCRIPT}")
print(f"Database: {DB_FILE} (exists: {DB_FILE is not None})")
print(f"Icon: {ICON_FILE} (exists: {ICON_FILE.exists()})")

# Collect data files
datas = []
binaries = []

# Include matplotlib mpl-data (fonts, colormaps — needed for DXF rendering)
try:
    import matplotlib as _mpl
    _mpl_data = Path(_mpl.__file__).parent / "mpl-data"
    if _mpl_data.exists():
        datas.append((str(_mpl_data), "matplotlib/mpl-data"))
        print(f"Included matplotlib/mpl-data: {sum(f.stat().st_size for f in _mpl_data.rglob('*') if f.is_file()) / 1048576:.1f} MB")
except ImportError:
    print("matplotlib not available — DXF rendering may fail")

# Include ezdxf data files (DXF templates, fonts, etc.)
try:
    import ezdxf as _ezdxf
    _ezdxf_data = Path(_ezdxf.__file__).parent
    # Collect .pyd files (compiled C extensions)
    for _pyd in _ezdxf_data.rglob("*.pyd"):
        _rel = _pyd.relative_to(_ezdxf_data.parent)
        binaries.append((str(_pyd), str(_rel.parent)))
    print(f"Included ezdxf extensions")
except ImportError:
    print("ezdxf not available — DXF support disabled")

# Include OCR directory structure (if available)
if OCR_ROOT and OCR_ROOT.exists():
    for item in OCR_ROOT.rglob("*"):
        if item.is_file():
            rel = item.relative_to(OCR_ROOT)
            dest = f"ocr/{rel.parent}" if rel.parent != Path('.') else "ocr"
            binaries.append((str(item), dest))
    print(f"Included OCR directory: {OCR_ROOT}")
else:
    print("OCR excluded — not found at build time")

# Include ODA File Converter directory (DWG → PDF conversion engine)
if ODA_ROOT and ODA_ROOT.exists():
    for item in ODA_ROOT.rglob("*"):
        if item.is_file():
            rel = item.relative_to(ODA_ROOT)
            dest = f"oda_converter/{rel.parent}" if rel.parent != Path('.') else "oda_converter"
            binaries.append((str(item), dest))
    print(f"Included ODA converter: {ODA_ROOT}")
else:
    print("ODA converter excluded — not bundled")

# Include database
if DB_FILE is not None:
    datas.append((str(DB_FILE), "data"))
    print(f"Included database: {DB_FILE} (size: {DB_FILE.stat().st_size / 1024 / 1024:.1f} MB)")
else:
    print("WARNING: No database file found!")

# Include logo for About dialog
if LOGO_FILE.exists():
    datas.append((str(LOGO_FILE), "."))
    print(f"Included logo: {LOGO_FILE}")

# Include icon file (sometimes needed at runtime)
if ICON_FILE.exists():
    datas.append((str(ICON_FILE), "."))
    print(f"Included icon: {ICON_FILE}")

a = Analysis(
    [str(MAIN_SCRIPT)],
    pathex=[str(SCRIPT_DIR)],
    binaries=binaries,
    datas=datas,
    hiddenimports=[
        # Heavy third-party libs loaded lazily at runtime
        "PIL",
        "PIL._tkinter_finder",
        "PIL.Image",
        "PIL.ImageDraw",
        "PIL.ImageFilter",
        "PIL.ImageCms",
        "PIL.ImageTk",
        "docx",
        "docx.shared",
        "docx.enum.text",
        "docx.oxml",
        "fitz",
        "pymupdf",
        # Database module
        "standard_db",
        # CAD / COM automation
        "win32com",
        "win32com.client",
        "pythoncom",
        # DXF rendering + DOC reading
        "ezdxf",
        "ezdxf.addons.drawing",
        "ezdxf.addons.drawing.matplotlib",
        "matplotlib",
        "matplotlib.backends",
        "matplotlib.backends.backend_agg",
        "olefile",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "tkinter.test",
        "unittest",
        "email",
        "http",
        "xml",
        "pdb",
        "py_compile",
        "doctest",
    ],
    noarchive=False,
)

pyz = PYZ(a.pure)

# ── onedir mode: EXE is small (~4 MB) — binaries/datas go to _internal/ ──
exe = EXE(
    pyz,
    a.scripts,
    exclude_binaries=True,
    name="工程助手",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    target_platform=None,
    icon=str(ICON_FILE) if ICON_FILE.exists() else None,
)

# ── COLLECT for the one-folder output (EXE + _internal/ with DLLs & data) ──
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    name="工程助手_v10",
)
