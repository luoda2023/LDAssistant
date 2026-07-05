#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
	工程助手 LDAssistant（新数据库版）
功能：
1. 上传PDF/WORD/TXT文件（支持多文件）
2. 选择识别区域（拖拽矩形，后续页面按同一区域识别）
3. OCR识别文字（自动排除公章等圆形印章）
4. 检查规范是否最新版/作废（支持30万+条数据，工程标准标记）
5. 生成DOC报告
"""
import sqlite3
import os
import sys
import json
import re
import subprocess
import tempfile
import threading
import glob
import shutil
from pathlib import Path
from datetime import datetime

from VERSION import VERSION_STR, VERSION_DISPLAY, VERSION_APP

import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# ── Heavy modules loaded lazily (see _get_* functions below) ──

try:
    from standard_db import StandardChecker as SQLiteStandardChecker
    USE_SQLITE = True
except Exception:
    USE_SQLITE = False

# Fix blurry text on high-DPI Windows displays
try:
    import ctypes
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

# ──────────────────────────────────────────────────────
# Resource path resolution — supports both:
#   1. Development mode (source tree via __file__)
#   2. PyInstaller one-folder mode (files in _internal/)
#   3. PyInstaller one-file mode (files in sys._MEIPASS temp)
# ──────────────────────────────────────────────────────
def _resource_path(relative: str) -> Path:
    """Return absolute path for a resource bundled with the app."""
    if getattr(sys, 'frozen', False):
        base = Path(sys._MEIPASS)
    else:
        base = Path(__file__).parent.resolve()
    return base / relative

# ── OCR engine path ──
OCR_DIR = _resource_path("ocr")
PADDLE_OCR_EXE = OCR_DIR / "PaddleOCR-json.exe"
if not PADDLE_OCR_EXE.exists():
    # Fallback: check system-installed UmiOCR
    _umi = Path(r"D:/Program Files/图片文字识别/UmiOCR-data/plugins/win7_x64_PaddleOCR-json")
    if _umi.exists():
        OCR_DIR = _umi
        PADDLE_OCR_EXE = _umi / "PaddleOCR-json.exe"

# ── JSON data file (optional fallback, SQLite is primary) ──
_DATA_FILE = _resource_path("data") / "all_standards_merged_20260629_092235.json"
DATA_FILE = _DATA_FILE if _DATA_FILE.exists() else None

# Standard code pattern
# Supports GB/T, DB14/T, DB3206/T, CJJ, GB51038, T/CCAA, etc.
CODE_PATTERN = re.compile(r'\b(?:[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?)\s*\d+(?:\.\d+)?-\d{4}\b', re.IGNORECASE)
# Standard name pattern: Chinese text after code
NAME_PATTERN = re.compile(r'(?:[A-Z]{1,5}(?:/[A-Z]{1,2})?)\s*\d+(?:\.\d+)?-\d{4}\s+([\u4e00-\u9fff]{2,60})')

# Status keywords
OBsolete_KEYWORDS = ['废止', '作废', '代替', '被代替', '被...代替']


def fullwidth_to_halfwidth(text):
    """全角转半角"""
    result = []
    for ch in text:
        code = ord(ch)
        if 0xFF01 <= code <= 0xFF5E:
            result.append(chr(code - 0xFEE0))
        elif code == 0x3000:
            result.append(' ')
        else:
            result.append(ch)
    return ''.join(result)


def normalize_for_matching(text):
    """统一格式用于匹配：全角转半角、中文符号转英文符号、去除空格、修正OCR错误"""
    if not text:
        return ''
    result = fullwidth_to_halfwidth(text)
    # 常见中文符号转英文
    punct_map = {
        '\u3002': '.',   # 。
        '\u3001': ',',   # 、
        '\u301C': '~',   # ～
        '\u2014': '-',   # —
        '\u2013': '-',   # –
        '\u2026': '...', # …
        '\u201C': '"',   # "
        '\u201D': '"',   # "
        '\u2018': "'",   # '
        '\u2019': "'",   # '
        '\u00D7': 'x',   # ×
    }
    for cn, en in punct_map.items():
        result = result.replace(cn, en)
    # 去除空格
    result = re.sub(r'\s+', '', result)
    # OCR常见错误修正
    # CJJJ -> CJJ (OCR多识别了一个J)
    result = re.sub(r'CJJJ', 'CJJ', result, flags=re.IGNORECASE)
    # DGJ -> DG/TJ (OCR漏识别了斜杠和T)
    result = re.sub(r'DGJ(?=\d)', 'DG/TJ', result, flags=re.IGNORECASE)
    return result


def preprocess_ocr_text(text):
    """OCR 文本预处理：全角字母数字→半角、常见OCR误识修正、符号统一
    此函数用于显示前预处理，不影响 normalize_for_matching 的独立逻辑。"""
    if not text:
        return text

    # 1. 全角英文字母/数字 → 半角（已有的函数能处理全角符号，但全角字母也需要）
    # fullwidth_to_halfwidth 已经处理了 FF01-FF5E（含全角字母数字）

    # 2. 常见 OCR 数字/字母混淆修正（只在明显语境下修正）
    # 注意：这些修正只针对明显错误的上下文，避免过度修正
    # 字母 O → 数字 0 （在编号语境中）
    result = re.sub(r'(?<=[A-Z]{1,3})O(?=\d)', '0', text, flags=re.IGNORECASE)
    # 字母 I → 数字 1 （在编号语境中）
    result = re.sub(r'(?<=[A-Z]{1,3})I(?=\d)', '1', result, flags=re.IGNORECASE)
    # 数字 0 → 字母 O （在字母后、数字前 且 0 后跟字母）
    result = re.sub(r'(?<=[A-Z])0(?=[A-Z])', 'O', result, flags=re.IGNORECASE)

    # 3. 中文逗号/句号/分号混用修正（显示时保留中文字符可读性，不影响匹配）
    # normalize_for_matching 会处理符号转换，这里不做额外处理

    return result


# ──────────────────────────────────────────────────────
# Lazy module loaders — heavy packages imported on demand
# so the app window appears instantly and shows a progress bar.
# ──────────────────────────────────────────────────────
_FITZ = None
_DOCX = None
_PIL = None
_HAS_PIL = False
HAS_PIL = False  # Module-level flag for code that checks HAS_PIL


def _get_fitz():
    """Lazy import of PyMuPDF (≈53 MB loaded on first use)."""
    global _FITZ
    if _FITZ is None:
        import fitz
        _FITZ = fitz
    return _FITZ


def _get_docx():
    """Lazy import of python-docx (≈12 MB loaded on first use)."""
    global _DOCX
    if _DOCX is None:
        from docx import Document as _Document
        from docx.shared import Pt, Inches, RGBColor
        from docx.enum.text import WD_ALIGN_PARAGRAPH
        _DOCX = {
            'Document': _Document,
            'Pt': Pt,
            'Inches': Inches,
            'RGBColor': RGBColor,
            'WD_ALIGN_PARAGRAPH': WD_ALIGN_PARAGRAPH,
        }
    return _DOCX


def _get_pil():
    """Lazy import of Pillow (≈15 MB loaded on first use).

    Returns (pil_dict, has_pil).
    Call this before any PIL usage.
    """
    global _PIL, _HAS_PIL, HAS_PIL
    if _PIL is None:
        try:
            from PIL import Image, ImageDraw, ImageFilter, ImageCms, ImageTk
            _PIL = {
                'Image': Image,
                'ImageDraw': ImageDraw,
                'ImageFilter': ImageFilter,
                'ImageCms': ImageCms,
                'ImageTk': ImageTk,
            }
            _HAS_PIL = True
            HAS_PIL = True
        except Exception:
            _HAS_PIL = False
            HAS_PIL = False
    return _PIL, _HAS_PIL


class StandardChecker:
    """标准规范检查器：优先使用 SQLite + FTS5，回退到 JSON 内存索引。"""
    def __init__(self, progress_callback=None):
        self.data = []
        self.code_index = {}
        self.name_index = {}
        self._sqlite_checker = None
        if USE_SQLITE:
            try:
                self._sqlite_checker = SQLiteStandardChecker(progress_callback=progress_callback)
                print("[StandardChecker] 已启用 SQLite + FTS5 加速")
            except Exception as e:
                print(f"[StandardChecker] SQLite 初始化失败，回退到 JSON: {e}")
                self._sqlite_checker = None
        if self._sqlite_checker is None:
            self.load_data()

    def load_data(self):
        if DATA_FILE is None or not DATA_FILE.exists():
            print(f"Data file not found: {DATA_FILE}")
            return
        print(f"Loading data from {DATA_FILE}...")
        with open(DATA_FILE, 'r', encoding='utf-8') as f:
            self.data = json.load(f)
        for r in self.data:
            code = normalize_for_matching(r.get('code', ''))
            if code:
                self.code_index[code] = r
            name = r.get('name', '').strip()
            if name:
                norm_name = normalize_for_matching(name)
                self.name_index[norm_name] = r
        print(f"Loaded {len(self.data)} records, indexed {len(self.code_index)} codes, {len(self.name_index)} names")

    def check_code(self, code, name=''):
        if self._sqlite_checker is not None:
            return self._sqlite_checker.check_code(code, name=name)

        normalized = normalize_for_matching(code)
        # 保持与 SQLite 版本结果格式一致（UI 代码依赖这些字段）
        result = {
            'found': False, 'status': '未找到', 'std_type': '', 'is_eng': False,
            'publish_date': '', 'act_date': '', 'source': '',
            'matched_code': '', 'matched_name': '', 'dual_match': False,
        }

        # 1. Exact code match
        if normalized in self.code_index:
            r = self.code_index[normalized]
            result.update({
                'found': True,
                'status': r.get('status', ''),
                'matched_code': r.get('code', ''),
                'matched_name': r.get('name', ''),
            })
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(r.get('name', '')).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
            return result

        # 2. Name match (combined name+code checking)
        if name:
            norm_name = normalize_for_matching(name).strip()
            if norm_name and norm_name in self.name_index:
                r = self.name_index[norm_name]
                result.update({
                    'found': True,
                    'status': r.get('status', ''),
                    'matched_code': r.get('code', ''),
                    'matched_name': r.get('name', ''),
                })
                if code:
                    norm_code = normalize_for_matching(code).strip()
                    db_code = normalize_for_matching(r.get('code', '')).strip()
                    if norm_code and db_code and (norm_code in db_code or db_code in norm_code):
                        result['dual_match'] = True
                return result
            # Partial name match: search for names containing the query
            if norm_name and len(norm_name) >= 4:
                for k, v in self.name_index.items():
                    if norm_name in k or k in norm_name:
                        result.update({
                            'found': True,
                            'status': v.get('status', ''),
                            'matched_code': v.get('code', ''),
                            'matched_name': v.get('name', ''),
                        })
                        if code:
                            norm_code = normalize_for_matching(code).strip()
                            db_code = normalize_for_matching(v.get('code', '')).strip()
                            if norm_code and db_code and (norm_code in db_code or db_code in norm_code):
                                result['dual_match'] = True
                        return result

        # 3. Partial/fuzzy code match
        best_match = None
        best_score = 0
        for k, v in self.code_index.items():
            # Exact substring match
            if normalized in k or k in normalized:
                score = len(normalized) / max(len(k), len(normalized))
                if score > best_score:
                    best_score = score
                    best_match = v
            # Fuzzy match: character-by-character similarity
            elif len(normalized) > 3 and len(k) > 3:
                matches = 0
                n_idx = 0
                k_idx = 0
                while n_idx < len(normalized) and k_idx < len(k):
                    if normalized[n_idx] == k[k_idx]:
                        matches += 1
                        n_idx += 1
                        k_idx += 1
                    else:
                        k_idx += 1
                similarity = matches / max(len(normalized), len(k))
                if similarity > 0.8 and similarity > best_score:
                    best_score = similarity
                    best_match = v

        if best_match:
            result.update({
                'found': True,
                'status': best_match.get('status', ''),
                'matched_code': best_match.get('code', ''),
                'matched_name': best_match.get('name', ''),
            })
            if name:
                norm_name = normalize_for_matching(name).strip()
                db_name = normalize_for_matching(best_match.get('name', '')).strip()
                if norm_name and db_name and (norm_name in db_name or db_name in norm_name):
                    result['dual_match'] = True
            return result

        return result

    def find_similar_codes(self, code, limit=5):
        """Find similar codes in database for debugging/recommendation."""
        if self._sqlite_checker is not None:
            # 委托给新的 SQLite StandardChecker
            return self._sqlite_checker.find_similar_codes(code, limit=limit)

        # JSON 回退模式
        normalized = normalize_for_matching(code)
        results = []
        for k, v in self.code_index.items():
            if normalized in k or k in normalized:
                results.append((k, v.get('code', ''), v.get('name', ''), v.get('status', ''), 'substring'))
            elif len(normalized) > 3 and len(k) > 3:
                matches = 0
                n_idx = 0
                k_idx = 0
                while n_idx < len(normalized) and k_idx < len(k):
                    if normalized[n_idx] == k[k_idx]:
                        matches += 1
                        n_idx += 1
                        k_idx += 1
                    else:
                        k_idx += 1
                similarity = matches / max(len(normalized), len(k))
                if similarity > 0.6:
                    results.append((k, v.get('code', ''), v.get('name', ''), v.get('status', ''), f'similar:{similarity:.2f}'))
        results.sort(key=lambda x: (0 if x[4] == 'substring' else 1, float(x[4].split(':')[1]) if ':' in x[4] else 0), reverse=True)
        return results[:limit]


    def ocr_image(self, image_path):
        cmd = [
            str(PADDLE_OCR_EXE),
            f"-image_path={image_path}",
        ]
        try:
            result = subprocess.run(cmd, capture_output=True, text=True, timeout=30, cwd=str(OCR_DIR),
                                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            # Strip ANSI escape codes
            ansi_escape = re.compile(r'\x1b\[[0-9;]*m')
            # Parse JSON from output
            for line in result.stdout.split('\n'):
                line = ansi_escape.sub('', line).strip()
                if line.startswith('{'):
                    try:
                        ocr_result = json.loads(line)
                        blocks = []
                        for item in ocr_result.get('data', []):
                            text = item.get('text', '')
                            box = item.get('box', [])
                            if box and len(box) == 4:
                                xs = [p[0] for p in box]
                                ys = [p[1] for p in box]
                                bbox = (min(xs), min(ys), max(xs), max(ys))
                            else:
                                bbox = (0, 0, 0, 0)
                            blocks.append((text, bbox))
                        text = ' '.join([b[0] for b in blocks])
                        return text, blocks
                    except Exception as e:
                        print(f"OCR parse error: {e}, line: {line[:200]}")
                        pass
            # If no JSON found, return raw stdout for debugging
            cleaned = ansi_escape.sub('', result.stdout).strip()
            return cleaned, []
        except Exception as e:
            return f"OCR_ERROR: {e}", []

    def close(self):
        if getattr(self, '_sqlite_checker', None) is not None:
            self._sqlite_checker.close()


class RegionSelector:
    """拖拽选择识别区域的辅助类"""
    def __init__(self, canvas, image_item_id, on_selected):
        self.canvas = canvas
        self.image_item_id = image_item_id
        self.on_selected = on_selected
        self.start_x = None
        self.start_y = None
        self.rect_id = None
        self.active = False

    def enable(self):
        self.active = True
        self.canvas.config(cursor="cross")
        self.canvas.bind('<ButtonPress-1>', self.on_press)
        self.canvas.bind('<B1-Motion>', self.on_drag)
        self.canvas.bind('<ButtonRelease-1>', self.on_release)

    def disable(self):
        self.active = False
        self.canvas.config(cursor="")
        self.canvas.unbind('<ButtonPress-1>')
        self.canvas.unbind('<B1-Motion>')
        self.canvas.unbind('<ButtonRelease-1>')
        if self.rect_id:
            self.canvas.delete(self.rect_id)
            self.rect_id = None

    def on_press(self, event):
        if not self.active:
            return
        self.start_x = self.canvas.canvasx(event.x)
        self.start_y = self.canvas.canvasy(event.y)
        if self.rect_id:
            self.canvas.delete(self.rect_id)
        self.rect_id = self.canvas.create_rectangle(
            self.start_x, self.start_y, self.start_x, self.start_y,
            outline='red', width=2, dash=(4, 2)
        )

    def on_drag(self, event):
        if not self.active or self.rect_id is None:
            return
        cur_x = self.canvas.canvasx(event.x)
        cur_y = self.canvas.canvasy(event.y)
        self.canvas.coords(self.rect_id, self.start_x, self.start_y, cur_x, cur_y)

    def on_release(self, event):
        if not self.active or self.rect_id is None:
            return
        end_x = self.canvas.canvasx(event.x)
        end_y = self.canvas.canvasy(event.y)
        x1 = min(self.start_x, end_x)
        y1 = min(self.start_y, end_y)
        x2 = max(self.start_x, end_x)
        y2 = max(self.start_y, end_y)
        if abs(x2 - x1) < 10 or abs(y2 - y1) < 10:
            self.canvas.delete(self.rect_id)
            self.rect_id = None
            return
        if self.on_selected:
            self.on_selected((x1, y1, x2, y2))
        self.disable()


def mask_seals_pil(image_path, out_path=None):
    """简单公章过滤：检测图像中的红色圆形区域并遮盖，减少 OCR 误识别"""
    pil, has_pil = _get_pil()
    if not has_pil:
        return image_path
    try:
        Image = pil['Image']
        img = Image.open(image_path).convert("RGB")
        w, h = img.size
        # 若图片过大先缩放，加快检测
        max_side = 1600
        if max(w, h) > max_side:
            scale = max_side / max(w, h)
            img_small = img.resize((int(w * scale), int(h * scale)), Image.Resampling.BOX)
        else:
            img_small = img
            scale = 1.0

        # 转 HSV，检测红色区域
        hsv = img_small.convert("HSV")
        pixels = hsv.load()
        mask = Image.new("L", img_small.size, 0)
        mask_pixels = mask.load()
        for y in range(img_small.size[1]):
            for x in range(img_small.size[0]):
                h_val, s_val, v_val = pixels[x, y]
                # 红色大致在 Hue 0-10 或 170-180
                if (h_val < 12 or h_val > 160) and s_val > 60 and v_val > 60:
                    mask_pixels[x, y] = 255

        # 简单膨胀，覆盖印章边缘
        mask = mask.filter(pil['ImageFilter'].MaxFilter(3))

        # 计算红色区域占比，只有占比超过 8% 才遮盖，避免误伤文档中的红线、红字
        red_count = sum(1 for p in mask.getdata() if p > 128)
        total = mask.width * mask.height
        should_mask = red_count > total * 0.08

        if should_mask:
            # 将小图 mask 放大回原图尺寸后再遮盖
            if scale != 1.0:
                mask = mask.resize((w, h), Image.Resampling.NEAREST)
            # 用 mask 把红色区域替换为白色
            white = Image.new("RGB", (w, h), (255, 255, 255))
            img = Image.composite(white, img, mask)

        if out_path:
            img.save(out_path)
            return out_path
        tmp_path = tempfile.mktemp(suffix='.png')
        img.save(tmp_path)
        return tmp_path
    except Exception as e:
        print(f"mask_seals error: {e}")


class App:
    def __init__(self):
        # ── Lightweight state variables (no heavy imports) ──
        self.checker = None
        self.pdf_paths = []
        self.current_path = None
        self.file_type = None  # 'pdf', 'docx', 'doc', 'txt', 'dwg', 'dxf'
        self.pdf_images = []
        self.pdf_images_meta = []  # 文件元信息（用于 DXF 等非 PDF 文件）
        self.ocr_results = []
        self.extracted_codes = []
        self.extracted_code_info = {}  # code -> {name, original}
        self.code_locations = []  # list of dicts: page_index, bbox, code
        self._active_highlight_loc = None  # 当前激活的高亮位置（供缩放/重绘后重建）
        self._highlight_rect_id = None
        self._highlight_fill_id = None
        self._highlight_label_id = None
        self._highlight_flash_count = 0
        self._zoom_level = 1.0
        self.check_results = []

        # Region selection state
        self.ocr_region = None
        self.selector = None
        self.selection_mode = False
        self.current_display_index = 0
        self._left_text_input = None
        self._file_text_widget = None
        self._file_text_scroll = None
        self._file_preview_frame = None
        self._text_input_frame = None
        self._left_mode_var = None

        # Pan state for preview canvas
        self._pan_start_x = 0
        self._pan_start_y = 0
        self._pan_image_x = 0
        self._pan_image_y = 0
        self._panning = False
        self._preview_name_text_id = None

        # ── Create root window (hidden initially) ──
        self.root = tk.Tk()
        self._name_index = {}
        self._left_mode_var = tk.StringVar(value='text')
        self.root.title("工程助手 LDAssistant")
        self.root.geometry("1280x820")
        self.root.minsize(1024, 640)
        self.root.withdraw()

        # ── Show splash window ──
        self._splash = None
        self._splash_status = None
        self._splash_progress = None
        self._create_splash()
        self.root.update()  # Force splash to paint

        # ── Deferred initialization via after() ──
        self.root.after(50, self._init_step1)

        # ── Watchdog guard: 25 秒后强制显示主窗口 ──
        self._watchdog_id = self.root.after(25000, self._init_watchdog)

    # ──────────────────────────────
    #  Splash screen
    # ──────────────────────────────
    def _create_splash(self):
        splash = tk.Toplevel(self.root)
        splash.title("加载中")
        splash.overrideredirect(True)
        splash.configure(bg="#1E3A5F")

        w, h = 420, 200
        sw = splash.winfo_screenwidth()
        sh = splash.winfo_screenheight()
        x = (sw - w) // 2
        y = (sh - h) // 2
        splash.geometry(f"{w}x{h}+{x}+{y}")
        splash.resizable(False, False)

        # Prevent splash from being closed
        splash.protocol("WM_DELETE_WINDOW", lambda: None)

        # Get available font — 直接测试候选字体，避免 font.families() 全量枚举（可卡死）
        from tkinter import font as _tkfont
        _splash_ff = "TkDefaultFont"
        for _candidate in ('Microsoft YaHei', '微软雅黑', 'Microsoft YaHei UI', 'SimSun'):
            try:
                _tf = _tkfont.Font(family=_candidate, size=10)
                _actual = _tf.actual()
                _tf.destroy()
                if _candidate.lower() in _actual['family'].lower():
                    _splash_ff = _candidate
                    break
            except Exception:
                continue

        # Title
        title_lbl = tk.Label(splash, text="工程助手 LDAssistant",
                             fg="#FFFFFF", bg="#1E3A5F",
                             font=(_splash_ff, 18, "bold"))
        title_lbl.pack(pady=(30, 10))

        # Status message
        status_lbl = tk.Label(splash, text="正在初始化...",
                              fg="#B0C4DE", bg="#1E3A5F",
                              font=(_splash_ff, 10))
        status_lbl.pack(pady=(0, 10))

        # Progress bar
        progress = ttk.Progressbar(splash, mode='indeterminate', length=320)
        progress.pack(pady=(0, 10))
        progress.start(15)

        # Version
        ver_lbl = tk.Label(splash, text=f"{VERSION_DISPLAY} · 新数据库版",
                           fg="#708090", bg="#1E3A5F",
                           font=(_splash_ff, 8))
        ver_lbl.pack()

        self._splash = splash
        self._splash_status = status_lbl
        self._splash_progress = progress
        self._splash_progress_det = False  # track mode

    def _update_splash(self, text, pct=None):
        self._splash_status.config(text=text)
        if pct is not None and not self._splash_progress_det:
            # Switch from indeterminate to determinate once
            self._splash_progress.stop()
            self._splash_progress.config(mode='determinate', maximum=100, value=min(pct, 100))
            self._splash_progress_det = True
        elif pct is not None and self._splash_progress_det:
            self._splash_progress['value'] = min(pct, 100)
        self.root.update()

    # ──────────────────────────────
    #  Deferred init steps (called via after())
    # ──────────────────────────────
    def _init_step1(self):
        """Phase 1: Load StandardChecker (heavy — indexes 300K+ records)"""
        self._update_splash("正在加载规范数据库...", 0)

        # Progress callback for database loading
        def progress_cb(curr, total, msg):
            pct = int(curr / max(total, 1) * 100)
            self._update_splash(msg, pct)

        try:
            self.checker = StandardChecker(progress_callback=progress_cb)
        except Exception as e:
            import traceback
            traceback.print_exc()
            self._update_splash(f"⚠️ 数据库加载失败: {e}", 100)
            self.checker = None  # 标记为不可用

        self.root.after(20, self._init_step2)

    def _init_step2(self):
        """Phase 2: Build UI"""
        try:
            self._update_splash("正在初始化界面...", 100)
            self._setup_style()
            self.setup_ui()
            self.root.after(20, self._init_done)
        except Exception as e:
            import traceback
            traceback.print_exc()
            self._update_splash(f"初始化界面失败: {e}. 正在尝试继续...", 100)
            self.root.after(20, self._init_done)

    def _init_done(self):
        """Final: close splash, show main window"""
        # 取消看门狗
        if hasattr(self, '_watchdog_id') and self._watchdog_id:
            try:
                self.root.after_cancel(self._watchdog_id)
            except Exception:
                pass
            self._watchdog_id = None
        if self._splash:
            try:
                self._splash.destroy()
            except Exception:
                pass
        self._splash = None
        self.root.deiconify()
        # 只在 UI 完全构建后才启动周期性重绘
        if hasattr(self, 'pdf_canvas') and self.pdf_canvas:
            self._start_periodic_redraw()

    def _init_watchdog(self):
        """看门狗：_init_done 超时时强制关闭 splash 显示主窗口"""
        self._watchdog_id = None
        if self._splash:
            try:
                self._splash.destroy()
            except Exception:
                pass
            self._splash = None
        self.root.deiconify()
        if hasattr(self, 'pdf_canvas') and self.pdf_canvas:
            self._start_periodic_redraw()

    def _get_font_family(self):
        """智能获取系统字体：直接测试候选字体，避免枚举全部字体（可导致卡死）"""
        try:
            from tkinter import font
            # 直接测试各候选字体，不枚举 font.families()（某些系统枚举全部字体会卡死）
            for candidate in ('Microsoft YaHei', '微软雅黑', 'Microsoft YaHei UI', 'SimSun'):
                try:
                    f = font.Font(family=candidate, size=10)
                    actual = f.actual()
                    f.destroy()
                    if candidate.lower() in actual['family'].lower():
                        return candidate
                except Exception:
                    continue
        except Exception:
            pass
        return 'SimSun'

    def _setup_style(self):
        style = ttk.Style()
        try:
            style.theme_use("vista")
        except Exception:
            try:
                style.theme_use("xpnative")
            except Exception:
                style.theme_use("default")

        self._font_family = self._get_font_family()

        # 全局字体
        default_font = (self._font_family, 10)
        header_font = (self._font_family, 11, "bold")
        title_font = (self._font_family, 18, "bold")
        small_font = (self._font_family, 9)

        # 配色方案 — 工程蓝主题
        PRIMARY = "#1E3A5F"       # 深蓝（主色）
        PRIMARY_LIGHT = "#2B6CB0" # 亮蓝（强调）
        BG_LIGHT = "#F5F6FA"      # 浅灰背景
        CARD_BG = "#FFFFFF"       # 卡片白
        TEXT_DARK = "#1A1A2E"     # 深色文字
        TEXT_MUTED = "#6B7280"    # 中灰色
        BORDER = "#E5E7EB"       # 边框色
        SUCCESS = "#2E7D32"       # 绿色（现行）
        DANGER = "#C62828"        # 红色（废止）
        WARNING = "#E65100"       # 橙色（更新）
        INFO = "#1565C0"         # 蓝色（即将实施）

        # 全局默认
        style.configure(".", font=default_font, background=BG_LIGHT)
        style.configure("TLabel", font=default_font, padding=4, background=BG_LIGHT, foreground=TEXT_DARK)
        style.configure("TButton", font=default_font, padding=(8, 6), background=CARD_BG)
        style.configure("TFrame", padding=8, background=BG_LIGHT)

        # 头部样式
        style.configure("Header.TLabel", font=header_font, background=CARD_BG, foreground=PRIMARY)

        # 标题样式 — CARD_BG 背景用于 header
        style.configure("Title.TLabel", font=title_font, foreground=PRIMARY, background=CARD_BG)

        # 按钮层级
        style.configure("Primary.TButton", font=(self._font_family, 10, "bold"),
                        foreground=CARD_BG, background=PRIMARY_LIGHT)
        style.map("Primary.TButton",
                  background=[('active', '#1A509A'), ('pressed', '#153D7A')],
                  foreground=[('active', CARD_BG)])

        style.configure("Action.TButton", font=default_font, padding=(10, 6),
                        foreground=TEXT_DARK, background=CARD_BG, relief="solid", borderwidth=1)
        style.map("Action.TButton",
                  background=[('active', '#EBF5FF'), ('pressed', '#D6E9FF')])

        style.configure("Danger.TButton", font=(self._font_family, 10, "bold"),
                        foreground=CARD_BG, background=DANGER)
        style.map("Danger.TButton",
                  background=[('active', '#A51D1D'), ('pressed', '#871515')])

        # Treeview 美化
        style.configure("Treeview",
                        rowheight=30,
                        font=default_font,
                        foreground=TEXT_DARK,
                        background=CARD_BG,
                        fieldbackground=CARD_BG,
                        borderwidth=0,
                        relief="flat")
        style.map("Treeview",
                  background=[('selected', '#BFDBFE')],
                  foreground=[('selected', TEXT_DARK)])

        style.configure("Treeview.Heading",
                        font=header_font,
                        foreground=CARD_BG,
                        background=PRIMARY,
                        relief="flat",
                        borderwidth=0,
                        padding=(6, 6))
        style.map("Treeview.Heading",
                  background=[('active', '#2B6CB0')])

        # 状态栏
        style.configure("Status.TLabel",
                        font=default_font,
                        background=PRIMARY,
                        foreground=CARD_BG,
                        anchor="w",
                        padding=(12, 6))

        # 进度条
        style.configure("Horizontal.TProgressbar",
                        background=PRIMARY_LIGHT,
                        troughcolor="#E5E7EB",
                        bordercolor=PRIMARY,
                        lightcolor=PRIMARY_LIGHT,
                        darkcolor=PRIMARY)

        # 标签框架（卡片效果）
        style.configure("Card.TFrame",
                        background=CARD_BG,
                        relief="solid",
                        borderwidth=1,
                        bordercolor=BORDER)

        # 辅助标签
        style.configure("Muted.TLabel", font=small_font, foreground=TEXT_MUTED, background=BG_LIGHT)

        # Radiobutton 样式
        style.configure("TRadiobutton", font=default_font, background=BG_LIGHT, foreground=TEXT_DARK)
        style.map("TRadiobutton",
                  background=[('active', '#EBF5FF')])

        # Notebook 样式
        style.configure("TNotebook", background=BG_LIGHT, borderwidth=0)
        style.configure("TNotebook.Tab", font=(self._font_family, 9), padding=(14, 5), background="#E5E7EB")
        style.map("TNotebook.Tab",
            background=[("selected", "#FFFFFF"), ("active", "#F3F4F6")],
            foreground=[("selected", "#1E3A5F")])

    def setup_ui(self):
        # ==================== 品牌头部 ====================
        header_bg_frame = tk.Frame(self.root, bg="#FFFFFF", highlightbackground="#E0E0E0", highlightthickness=0, highlightcolor="#E0E0E0")
        header_bg_frame.pack(side=tk.TOP, fill=tk.X)
        # 底部1px分隔线（用Frame模拟）
        sep = tk.Frame(header_bg_frame, bg="#E0E0E0", height=1)
        sep.pack(side=tk.BOTTOM, fill=tk.X)

        header_inner = tk.Frame(header_bg_frame, bg="#FFFFFF", padx=16, pady=8)
        header_inner.pack(side=tk.TOP, fill=tk.X)

        # Logo + 软件名
        logotext_frame = tk.Frame(header_inner, bg="#FFFFFF")
        logotext_frame.pack(side=tk.LEFT)

        # Logo 图片
        self._logo_label = None
        pil, has_pil = _get_pil()
        if has_pil:
            try:
                Image = pil['Image']
                ImageTk = pil['ImageTk']
                logo_path = _resource_path("LDA.png")
                if logo_path.exists():
                    logo_img = Image.open(logo_path)
                    header_img_height = 36
                    ratio = header_img_height / max(logo_img.height, 1)
                    new_size = (max(1, int(logo_img.width * ratio)), max(1, int(logo_img.height * ratio)))
                    logo_img = logo_img.resize(new_size, Image.Resampling.BOX)
                    self._logo_photo = ImageTk.PhotoImage(logo_img)
                    logo_lbl = tk.Label(logotext_frame, image=self._logo_photo, bg="#FFFFFF", cursor="hand2")
                    logo_lbl.pack(side=tk.LEFT, padx=(0, 10))
                    logo_lbl.bind('<Button-1>', lambda e: self._show_about())
            except Exception as e:
                print(f"Failed to load logo: {e}")

        # 软件名称 + 版本号
        title_lbl = tk.Label(logotext_frame, text="规范标准助手", font=(self._font_family, 18, "bold"),
                             fg="#1E3A5F", bg="#FFFFFF")
        title_lbl.pack(side=tk.LEFT)
        ver_badge = tk.Label(logotext_frame, text=VERSION_DISPLAY, font=(self._font_family, 9, "bold"),
                             fg="#FFFFFF", bg="#2B6CB0", padx=5, pady=1)
        ver_badge.pack(side=tk.LEFT, padx=(6, 0))

        # 标语行
        tagline = tk.Label(header_inner, text="📄 OCR识别  →  📋 规范提取  →  ✅ 状态检查  →  📝 报告导出",
                          font=(self._font_family, 9), fg="#6B7280", bg="#FFFFFF")
        tagline.pack(side=tk.LEFT, padx=(20, 0))

        # 右侧：关于按钮
        about_btn = tk.Button(header_inner, text="关于", font=(self._font_family, 9),
                             bg="#FFFFFF", fg="#2B6CB0", relief="flat",
                             padx=8, pady=2, cursor="hand2",
                             command=self._show_about)
        about_btn.pack(side=tk.RIGHT)
        about_btn.bind('<Enter>', lambda e: about_btn.config(bg='#EBF4FF'))
        about_btn.bind('<Leave>', lambda e: about_btn.config(bg='#FFFFFF'))

        # ==================== 主容器 ====================
        main_container = tk.Frame(self.root, bg="#F5F6FA")
        main_container.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=0, pady=0)

        # ---- 左侧面板：文件预览 / 文本输入 ----
        left_pane = tk.Frame(main_container, bg="#FFFFFF", highlightbackground="#E5E7EB",
                             highlightthickness=1, highlightcolor="#E5E7EB")
        left_pane.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(8, 4), pady=(8, 8))

        preview_header = tk.Frame(left_pane, bg="#FFFFFF")
        preview_header.pack(side=tk.TOP, fill=tk.X, padx=8, pady=(8, 0))
        tk.Label(preview_header, text="📂 文件预览", font=(self._font_family, 11, "bold"),
                 fg="#1E3A5F", bg="#FFFFFF").pack(side=tk.LEFT)

        # 模式切换
        mode_frame = tk.Frame(left_pane, bg="#FFFFFF")
        mode_frame.pack(side=tk.TOP, fill=tk.X, padx=8, pady=(6, 0))
        self._left_mode_var = tk.StringVar(value='text')
        self._radio_text = ttk.Radiobutton(mode_frame, text="📝 粘贴文本", variable=self._left_mode_var,
                                           value='text', command=self._on_left_mode_changed)
        self._radio_text.pack(side=tk.LEFT, padx=(0, 12))
        self._radio_file = ttk.Radiobutton(mode_frame, text="📁 文件预览", variable=self._left_mode_var,
                                           value='file', command=self._on_left_mode_changed)
        self._radio_file.pack(side=tk.LEFT)

        # 左右内容容器
        left_content = ttk.Frame(left_pane, padding=(0, 0))
        left_content.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=8, pady=(8, 8))

        # 文本输入模式
        self._text_input_frame = tk.Frame(left_content, bg="#FFFFFF")
        self._left_text_input = tk.Text(self._text_input_frame, wrap=tk.WORD,
                                        font=(self._font_family, 10),
                                        relief="solid", borderwidth=1,
                                        highlightbackground="#D1D5DB",
                                        highlightcolor="#2B6CB0",
                                        highlightthickness=1)
        text_scroll = ttk.Scrollbar(self._text_input_frame, orient=tk.VERTICAL,
                                    command=self._left_text_input.yview)
        self._left_text_input.configure(yscrollcommand=text_scroll.set)
        text_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._left_text_input.pack(fill=tk.BOTH, expand=True)

        # 文件预览模式
        self._file_preview_frame = tk.Frame(left_content, bg="#FFFFFF")
        self.pdf_canvas = tk.Canvas(self._file_preview_frame,
                                    bg="#F3F4F6",
                                    highlightbackground="#D1D5DB",
                                    highlightthickness=1)
        self.pdf_canvas.pack(side=tk.TOP, fill=tk.BOTH, expand=True)
        self.pdf_canvas.bind('<Configure>', self._on_canvas_resize)
        self.pdf_canvas.bind('<MouseWheel>', self._on_mouse_wheel)
        # 鼠标左键拖拽平移预览图
        self.pdf_canvas.bind('<ButtonPress-1>', self._on_pan_start)
        self.pdf_canvas.bind('<B1-Motion>', self._on_pan_drag)
        self.pdf_canvas.bind('<ButtonRelease-1>', self._on_pan_end)
        # 原中键拖拽保留备用
        self.pdf_canvas.bind('<ButtonPress-2>', self._on_pan_start)
        self.pdf_canvas.bind('<B2-Motion>', self._on_pan_drag)
        self.pdf_canvas.bind('<ButtonRelease-2>', self._on_pan_end)
        self._resize_after_id = None

        # 键盘快捷键
        self.root.bind('<Left>', lambda e: self._prev_page())
        self.root.bind('<Right>', lambda e: self._next_page())
        self.root.bind('<Prior>', lambda e: self._prev_page())  # Page Up
        self.root.bind('<Next>', lambda e: self._next_page())   # Page Down
        self.root.bind('<Home>', lambda e: self.show_page(0))   # Home
        self.root.bind('<End>', lambda e: self.show_page(len(self.pdf_images) - 1) if self.pdf_images else None)
        self.root.bind('<Escape>', lambda e: self._reset_zoom())  # Esc重置缩放

        # 预览底部工具栏
        preview_footer = tk.Frame(self._file_preview_frame, bg="#FFFFFF")
        preview_footer.pack(side=tk.TOP, fill=tk.X, padx=0, pady=(6, 0))
        self.page_var = tk.StringVar(value="第 0 / 0 页")
        tk.Label(preview_footer, textvariable=self.page_var, font=(self._font_family, 9),
                 fg="#6B7280", bg="#FFFFFF").pack(side=tk.LEFT)
        self._preview_name_var = tk.StringVar(value="")
        tk.Label(preview_footer, textvariable=self._preview_name_var, font=(self._font_family, 9, "bold"),
                 fg="#C62828", bg="#FFFFFF").pack(side=tk.LEFT, padx=(12, 0))

        # 预览按钮组
        btn_frame = tk.Frame(preview_footer, bg="#FFFFFF")
        btn_frame.pack(side=tk.RIGHT)
        for txt, cmd, w in [("◀ 上页", self._prev_page, 6), ("下页 ▶", self._next_page, 6),
                            ("放大 +", self._zoom_in, 6), ("缩小 -", self._zoom_out, 6),
                            ("重置", self._reset_zoom, 5)]:
            b = tk.Button(btn_frame, text=txt, command=cmd, font=(self._font_family, 9),
                         bg="#FFFFFF", fg="#374151", relief="solid", borderwidth=1,
                         padx=4, pady=2, width=w, cursor="hand2", height=1)
            b.pack(side=tk.RIGHT, padx=(2, 0))
            b.bind('<Enter>', lambda e, btn=b: btn.config(bg='#E5E7EB'))
            b.bind('<Leave>', lambda e, btn=b: btn.config(bg='#FFFFFF'))

        # ---- 右侧面板：操作 + 标签页 ----
        right_pane = tk.Frame(main_container, bg="#F5F6FA")
        right_pane.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True, padx=(4, 8), pady=(8, 8))

        # 操作按钮条
        action_card = tk.Frame(right_pane, bg="#FFFFFF", highlightbackground="#E5E7EB",
                               highlightthickness=1, highlightcolor="#E5E7EB")
        action_card.pack(side=tk.TOP, fill=tk.X, pady=(0, 8))

        action_inner = tk.Frame(action_card, bg="#FFFFFF", padx=8, pady=8)
        action_inner.pack(side=tk.TOP, fill=tk.X)

        btn_data = [
            ("📂 打开文件", self.open_file, "Action"),
            ("🎯 选区", self.start_selection, "Action"),
            ("🗑️ 清除", self.clear_region, "Danger"),
            ("✨ 开始OCR", self.start_ocr, "Primary"),
            ("🔍 检查规范", self.check_standards, "Primary"),
            ("📄 导出报告", self.export_doc, "Action"),
        ]
        for txt, cmd, style in btn_data:
            if style == "Primary":
                b = tk.Button(action_inner, text=txt, command=cmd, cursor="hand2",
                             font=(self._font_family, 10, "bold"),
                             bg="#2B6CB0", fg="#FFFFFF", relief="flat",
                             padx=12, pady=4, height=1)
            elif style == "Danger":
                b = tk.Button(action_inner, text=txt, command=cmd, cursor="hand2",
                             font=(self._font_family, 10),
                             bg="#FFFFFF", fg="#C62828", relief="solid", borderwidth=1,
                             padx=10, pady=4, height=1)
            else:
                b = tk.Button(action_inner, text=txt, command=cmd, cursor="hand2",
                             font=(self._font_family, 10),
                             bg="#FFFFFF", fg="#374151", relief="solid", borderwidth=1,
                             padx=10, pady=4, height=1)
            b.pack(side=tk.LEFT, padx=(0, 6))
            # hover效果
            if style == "Primary":
                b.bind('<Enter>', lambda e, btn=b: btn.config(bg='#3178C6'))
                b.bind('<Leave>', lambda e, btn=b: btn.config(bg='#2B6CB0'))
            elif style == "Danger":
                b.bind('<Enter>', lambda e, btn=b: btn.config(bg='#FEF2F2'))
                b.bind('<Leave>', lambda e, btn=b: btn.config(bg='#FFFFFF'))

        self.region_var = tk.StringVar(value="识别区域：未设置（全页识别）")
        tk.Label(action_card, textvariable=self.region_var, font=(self._font_family, 9),
                 fg="#6B7280", bg="#FFFFFF", padx=8, pady=0).pack(side=tk.TOP, anchor=tk.W)

        # Progress bar
        self.progress_var = tk.DoubleVar(value=0.0)
        self.progress_bar = ttk.Progressbar(right_pane, variable=self.progress_var, maximum=100)
        self.progress_bar.pack(side=tk.TOP, fill=tk.X, pady=(4, 8))

        # Notebook
        self.notebook = ttk.Notebook(right_pane)
        self.notebook.pack(side=tk.TOP, fill=tk.BOTH, expand=True)

        # OCR text tab
        ocr_frame = ttk.Frame(self.notebook)
        self.notebook.add(ocr_frame, text="OCR 识别文本")
        self.ocr_text = tk.Text(ocr_frame, wrap=tk.WORD, font=(self._font_family, 10))
        ocr_scroll = ttk.Scrollbar(ocr_frame, orient=tk.VERTICAL, command=self.ocr_text.yview)
        self.ocr_text.configure(yscrollcommand=ocr_scroll.set)
        ocr_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.ocr_text.pack(fill=tk.BOTH, expand=True)

        # Extracted standards list tab
        list_frame = ttk.Frame(self.notebook)
        self.notebook.add(list_frame, text="识别到的规范列表")
        list_toolbar = ttk.Frame(list_frame)
        list_toolbar.pack(side=tk.TOP, fill=tk.X)
        ttk.Label(list_toolbar, text="双击可移除误识别项，单击可定位到 PDF").pack(side=tk.LEFT)
        list_columns = ('no', 'code', 'name')
        self.list_tree = ttk.Treeview(list_frame, columns=list_columns, show='headings', selectmode='extended')
        self.list_tree.heading('no', text='序号')
        self.list_tree.heading('code', text='规范编号')
        self.list_tree.heading('name', text='规范名称')
        self.list_tree.column('no', width=60, anchor=tk.CENTER)
        self.list_tree.column('code', width=160, anchor=tk.W)
        self.list_tree.column('name', width=260, anchor=tk.W)
        list_scroll = ttk.Scrollbar(list_frame, orient=tk.VERTICAL, command=self.list_tree.yview)
        self.list_tree.configure(yscrollcommand=list_scroll.set)
        list_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.list_tree.pack(fill=tk.BOTH, expand=True)
        self.list_tree.bind('<Double-Button-1>', self.on_list_item_double_click)
        self.list_tree.bind('<<TreeviewSelect>>', self.on_code_selected)

        # Check results tab
        check_frame = ttk.Frame(self.notebook)
        self.notebook.add(check_frame, text="规范检查结果")
        columns = ('code', 'name', 'status', 'std_type', 'action_date')
        self.check_tree = ttk.Treeview(check_frame, columns=columns, show='tree headings', selectmode='extended')
        self.check_tree.heading('#0', text='序号')
        self.check_tree.heading('code', text='规范编号')
        self.check_tree.heading('name', text='规范名称')
        self.check_tree.heading('status', text='状态')
        self.check_tree.heading('std_type', text='标准类型')
        self.check_tree.heading('action_date', text='操作/日期')
        self.check_tree.column('#0', width=50, anchor=tk.CENTER)
        self.check_tree.column('code', width=180)
        self.check_tree.column('name', width=280)
        self.check_tree.column('status', width=80, anchor=tk.CENTER)
        self.check_tree.column('std_type', width=70, anchor=tk.CENTER)
        self.check_tree.column('action_date', width=120, anchor=tk.CENTER)
        check_scroll = ttk.Scrollbar(check_frame, orient=tk.VERTICAL, command=self.check_tree.yview)
        self.check_tree.configure(yscrollcommand=check_scroll.set)
        check_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.check_tree.pack(fill=tk.BOTH, expand=True)
        self.check_tree.bind('<Double-Button-1>', self.on_check_item_double_click)
        self.check_tree.bind('<<TreeviewSelect>>', self.on_check_item_selected)

        # Status bar
        self.status_var = tk.StringVar(value="就绪")
        statusbar = ttk.Label(self.root, textvariable=self.status_var, style="Status.TLabel")
        statusbar.pack(side=tk.BOTTOM, fill=tk.X)

        # Selection helper
        self.selector = RegionSelector(self.pdf_canvas, None, self._on_region_selected)

        # 初始化左侧面板显示（默认显示文本输入模式）
        self._on_left_mode_changed()

    def _on_left_mode_changed(self):
        mode = self._left_mode_var.get()
        if mode == 'text':
            self._text_input_frame.pack(fill=tk.BOTH, expand=True)
            self._file_preview_frame.forget()
        else:
            self._file_preview_frame.pack(fill=tk.BOTH, expand=True)
            self._text_input_frame.forget()

    def _get_active_text(self):
        """Get text from whichever mode is active."""
        mode = self._left_mode_var.get()
        if mode == 'text':
            return self._left_text_input.get('1.0', tk.END).strip()
        else:
            # File mode: use combined OCR results
            return '\n'.join(self.ocr_results).strip()

    def open_file(self):
        paths = filedialog.askopenfilenames(
            title="选择文件（PDF/Word/DWG/DXF/Excel/PPT/TXT）",
            filetypes=[
                ("PDF files", "*.pdf"),
                ("Word files", "*.doc;*.docx"),
                ("Excel files", "*.xls;*.xlsx"),
                ("PPT files", "*.ppt;*.pptx"),
                ("DWG files", "*.dwg"),
                ("DXF files", "*.dxf"),
                ("Text files", "*.txt"),
                ("All files", "*.*")
            ]
        )
        if not paths:
            return
        self.pdf_paths = list(paths)
        self.current_path = self.pdf_paths[0]
        ext = Path(self.current_path).suffix.lower()
        if ext == '.pdf':
            self.file_type = 'pdf'
        elif ext in ('.docx', '.doc'):
            self.file_type = ext.lstrip('.')  # 'docx' or 'doc'
        elif ext in ('.xlsx', '.xls'):
            self.file_type = 'xlsx' if ext == '.xlsx' else 'xls'
        elif ext in ('.pptx', '.ppt'):
            self.file_type = 'pptx' if ext == '.pptx' else 'ppt'
        elif ext == '.txt':
            self.file_type = 'txt'
        elif ext == '.dwg':
            self.file_type = 'dwg'
        elif ext == '.dxf':
            self.file_type = 'dxf'
        else:
            self.file_type = 'unknown'

        self.status_var.set(f"已打开 {len(self.pdf_paths)} 个文件，当前: {Path(self.current_path).name}")
        self._left_mode_var.set('file')
        self._on_left_mode_changed()
        # 强制更新布局，确保 canvas 拿到真实尺寸
        self.root.update_idletasks()
        if self.file_type == 'pdf':
            self.convert_pdf_to_images()
        elif self.file_type == 'dwg':
            self._render_dwg_to_image()
        elif self.file_type == 'dxf':
            self._render_dxf_to_image()
        elif self.file_type in ('xlsx', 'xls'):
            self.extract_text_file()
        elif self.file_type in ('pptx', 'ppt'):
            self.extract_text_file()
        else:
            self.extract_text_file()

    def convert_pdf_to_images(self):
        if not self.current_path or self.file_type != 'pdf':
            return
        self.status_var.set("正在转换 PDF...")
        self.progress_var.set(0)
        self.pdf_images = []

        doc = None
        try:
            doc = _get_fitz().open(self.current_path)
            total = len(doc)
            for page_num in range(total):
                page = doc.load_page(page_num)
                pix = page.get_pixmap(dpi=200)
                img_path = tempfile.mktemp(suffix='.png')
                pix.save(img_path)
                self.pdf_images.append(img_path)
                self.progress_var.set((page_num + 1) / total * 100)
                self.root.update_idletasks()
        except Exception as e:
            import traceback
            traceback.print_exc()
            self.pdf_images = []
            self.status_var.set(f"❌ PDF 打开失败: {e}")
            messagebox.showerror("PDF 错误", f"无法打开 PDF 文件:\n{e}\n\n请确认文件不是加密或损坏的。")
            return
        finally:
            if doc is not None:
                doc.close()

        self.status_var.set(f"PDF 已转换: {len(self.pdf_images)} 页")
        self.page_var.set(f"第 1 / {len(self.pdf_images)} 页")
        self.progress_var.set(0)

        if self.pdf_images:
            self.show_page(0)

    def _read_doc_via_com(self):
        """使用 Word COM 自动化读取 .doc 文件（兼容所有 Word 格式）"""
        try:
            import win32com.client
            import pythoncom
            pythoncom.CoInitialize()
            word = win32com.client.Dispatch('Word.Application')
            word.Visible = False
            word.DisplayAlerts = False
            doc = word.Documents.Open(self.current_path)
            full_text = doc.Content.Text
            doc.Close(False)
            word.Quit()
            pythoncom.CoUninitialize()
            return full_text
        except Exception as e:
            print(f"Word COM 读取失败: {e}")
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass
            messagebox.showerror("Word 读取失败",
                f"无法读取 Word 文件。\n请确认已安装 Microsoft Word。\n错误: {e}")
            return None

    def _read_doc_via_olefile(self):
        """使用 olefile 从旧版 .doc 文件中提取文本（无需 Word）"""
        try:
            import olefile
            ole = olefile.OleFileIO(self.current_path)
            # .doc 文件中的文本通常在 WordDocument stream 中
            if ole.exists('WordDocument'):
                data = ole.openstream('WordDocument').read()
                # 尝试提取 ASCII/Unicode 文本
                text = ''
                # 方法1: 读取 1Table 或 0Table 中的文本
                for stream_name in ['1Table', '0Table']:
                    if ole.exists(stream_name):
                        table_data = ole.openstream(stream_name).read()
                        # 简单提取可读文本
                        import struct
                        try:
                            # 解析 FIB (File Information Block)
                            # 从 WordDocument stream 的 0x01A2 处获取文本偏移
                            if len(data) > 0x01A4:
                                cb = struct.unpack_from('<H', data, 0x01A2)[0]
                                fc = struct.unpack_from('<I', data, 0x01A4)[0]
                                # 从 table stream 读取文本
                                raw_text = table_data[fc:fc+cb]
                                text = raw_text.decode('utf-16-le', errors='replace')
                                if text.strip():
                                    break
                        except Exception:
                            continue

                if not text.strip():
                    # 方法2: 直接提取所有可读文本
                    text = ''
                    for i in range(0, len(data), 2):
                        try:
                            char = data[i:i+2].decode('utf-16-le')
                            if char.isprintable() or char in '\n\r\t':
                                text += char
                        except Exception:
                            text += ' '
                    text = ' '.join(text.split())

                ole.close()
                if text.strip():
                    return text.strip()

            ole.close()
            return None
        except Exception as e:
            print(f"olefile 读取 .doc 失败: {e}")
            return None

    def extract_text_file(self):
        """Extract text from DOC/DOCX or TXT file."""
        if not self.current_path:
            return
        self.status_var.set("正在提取文本...")
        self.progress_var.set(0)
        self.ocr_results = []
        self.pdf_images = []
        self.code_locations = []
        self.extracted_codes = []
        self.list_tree.delete(*self.list_tree.get_children())
        self.check_tree.delete(*self.check_tree.get_children())
        self.pdf_canvas.delete('all')

        try:
            if self.file_type == 'docx':
                # .docx: 用 python-docx（无需 Office）
                doc = _get_docx()['Document'](self.current_path)
                full_text = '\n'.join([p.text for p in doc.paragraphs])
            elif self.file_type == 'doc':
                # .doc: 先用 olefile（无需 Office），失败后用 Word COM
                full_text = self._read_doc_via_olefile()
                if not full_text:
                    full_text = self._read_doc_via_com()
                if not full_text:
                    raise RuntimeError("无法读取 .doc 文件，请将文件另存为 .docx 格式")
            elif self.file_type == 'txt':
                with open(self.current_path, 'r', encoding='utf-8', errors='ignore') as f:
                    full_text = f.read()
            elif self.file_type == 'xlsx':
                full_text = self._read_xlsx_text()
            elif self.file_type == 'pptx':
                full_text = self._read_pptx_text()
            elif self.file_type in ('xls', 'ppt'):
                # .xls / .ppt 是二进制 OLE 格式，简单提示
                full_text = f"[{self.file_type.upper()}] 该格式为旧版二进制格式，请在 Office 中另存为 .xlsx / .pptx 后打开。"
            else:
                messagebox.showwarning("提示", "不支持的文件格式")
                return

            self.ocr_results = [full_text]
            self.page_var.set(f"📄 {Path(self.current_path).name}")
            self.progress_var.set(100)
            self.status_var.set("文本提取完成")

            # Show text in OCR tab
            self.ocr_text.delete('1.0', tk.END)
            self.ocr_text.insert(tk.END, full_text)

            # Store as ocr_results for consistent handling
            self.ocr_results = [full_text]

            # Extract standard codes
            self._extract_codes_from_text(full_text)

        except Exception as e:
            messagebox.showerror("错误", f"读取文件失败: {e}")
            self.status_var.set("读取文件失败")

    def _render_dwg_to_image(self):
        """DWG 预览：1)LibreDWG(内置) 2)ODA 3)AutoCAD COM 4)引导提示"""
        if not self.current_path or self.file_type != 'dwg':
            return
        self.status_var.set("正在转换 DWG...")
        self.progress_var.set(0)
        self.pdf_images = []
        self.ocr_results = []

        # ── Tier 1: LibreDWG (开源 GNU，内置，dwg2dxf → DXF → ezdxf 渲染) ──
        if self._convert_via_libredwg():
            return

        # ── Tier 2: ODA File Converter ──
        oda_exe = self._find_oda_converter()
        if oda_exe and self._convert_via_oda(oda_exe):
            return

        # ── Tier 3: AutoCAD COM ──
        if self._convert_via_autocad():
            return

        # ── Tier 4: 引导用户 ──
        msg = (
            "本程序无法直接打开 DWG 文件。\n\n"
            "您可以将 DWG 另存为 DXF 或 PDF 后重新打开：\n"
            "1. 在 AutoCAD / 其他 CAD 软件中打开\n"
            "2. 另存为 DXF 格式（推荐）或打印为 PDF\n"
            "3. 用本程序打开转换后的文件即可"
        )
        messagebox.showinfo("DWG 预览指引", msg)
        self.status_var.set("DWG 需转为 DXF 或 PDF 格式")

    def _convert_via_libredwg(self):
        """通过内置 LibreDWG 将 DWG → DXF，再用 ezdxf 渲染"""
        try:
            import subprocess, tempfile

            # 查找内置的 LibreDWG dwg2dxf.exe
            libredwg_exe = None
            if getattr(sys, 'frozen', False):
                base = Path(sys._MEIPASS)
            else:
                base = Path(__file__).parent.resolve()

            candidates = [
                base / "libredwg" / "bin" / "dwg2dxf.exe",
                base / "libredwg" / "dwg2dxf.exe",
                base / "libredwg" / "dwg2dxf",
                base.parent / "libredwg" / "bin" / "dwg2dxf.exe",
                base.parent / "libredwg" / "dwg2dxf.exe",
            ]
            for c in candidates:
                if c.exists():
                    libredwg_exe = str(c)
                    break

            if not libredwg_exe:
                return False

            self.status_var.set("LibreDWG: DWG → DXF 转换中...")
            self.root.update_idletasks()

            # 转换 DWG → DXF
            temp_dir = tempfile.mkdtemp(prefix="dwg_libredwg_")
            output_dir = str(Path(temp_dir))
            result = subprocess.run(
                [libredwg_exe, self.current_path, "-o", output_dir, "-f", "ACAD2010"],
                capture_output=True, text=True, timeout=60,
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0),
            )

            # 找到输出的 DXF 文件
            dwg_stem = Path(self.current_path).stem
            dxf_path = os.path.join(output_dir, f"{dwg_stem}.dxf")
            if not os.path.exists(dxf_path):
                # 尝试小写扩展名
                dxf_path = os.path.join(output_dir, f"{dwg_stem}.DXF")
            if not os.path.exists(dxf_path):
                # 尝试在输出目录中查找任何 .dxf 文件
                for f in os.listdir(output_dir):
                    if f.lower().endswith('.dxf'):
                        dxf_path = os.path.join(output_dir, f)
                        break

            if not os.path.exists(dxf_path):
                print(f"LibreDWG 转换失败: {result.stderr[:300] if result.stderr else '无输出'}")
                return False

            print(f"LibreDWG 成功: {dxf_path} ({os.path.getsize(dxf_path) / 1024:.0f} KB)")

            # 用 ezdxf 渲染转换后的 DXF
            self.current_path = dxf_path
            self.file_type = 'dxf'
            self._render_dxf_to_image()
            if self.pdf_images:
                self.status_var.set(f"DWG → DXF (LibreDWG): {dwg_stem} 共 {len(self.pdf_images)} 张图")
                return True

            return False
        except subprocess.TimeoutExpired:
            print("LibreDWG 转换超时")
            return False
        except Exception as e:
            print(f"LibreDWG 错误: {e}")
            return False

    def _render_dxf_to_image(self):
        """将 DXF 文件渲染为图像预览（内置 ezdxf + matplotlib，无需任何额外安装）"""
        if not self.current_path or self.file_type != 'dxf':
            return
        self.status_var.set("正在渲染 DXF...")
        self.progress_var.set(0)
        self.pdf_images = []
        self.ocr_results = []

        try:
            import ezdxf
            from ezdxf.addons.drawing import RenderContext, Frontend
            from ezdxf.addons.drawing.matplotlib import MatplotlibBackend
            import matplotlib
            matplotlib.use('Agg')
            import matplotlib.pyplot as plt

            doc = ezdxf.readfile(self.current_path)
            msp = doc.modelspace()

            fig, ax = plt.subplots(figsize=(16, 12), dpi=150)
            ax.set_aspect('equal')
            ax.axis('off')

            ctx = RenderContext(doc)
            backend = MatplotlibBackend(ax)
            frontend = Frontend(ctx, backend)
            # ezdxf 1.4+ 使用 draw_layout 替代 draw
            if hasattr(frontend, 'draw_layout'):
                frontend.draw_layout(msp)
            else:
                frontend.draw(msp)

            if msp:
                try:
                    extents = msp.extents()
                    if extents and extents[0] and extents[1]:
                        xmin, ymin = extents[0]
                        xmax, ymax = extents[1]
                        if xmax > xmin and ymax > ymin:
                            margin = max((xmax - xmin), (ymax - ymin)) * 0.05
                            ax.set_xlim(xmin - margin, xmax + margin)
                            ax.set_ylim(ymin - margin, ymax + margin)
                except Exception:
                    pass

            img_path = tempfile.mktemp(suffix='.png')
            fig.savefig(img_path, bbox_inches='tight', pad_inches=0.1,
                       facecolor='white', dpi=150)
            plt.close(fig)

            self.pdf_images.append(img_path)
            self.pdf_images_meta = [{'type': 'dxf', 'path': self.current_path}]
            self.status_var.set(f"DXF 已渲染: {Path(self.current_path).name}")

        except ImportError:
            messagebox.showerror("缺少依赖", "需要安装 ezdxf 和 matplotlib 才能预览 DXF。\n请运行: pip install ezdxf matplotlib")
            self.status_var.set("渲染 DXF 失败：缺少依赖库")
            return
        except Exception as e:
            import traceback
            traceback.print_exc()
            messagebox.showerror("DXF 错误", f"无法渲染 DXF 文件:\n{e}")
            self.status_var.set(f"渲染 DXF 失败: {e}")
            return

        if self.pdf_images:
            self.show_page(0)

    def _find_oda_converter(self):
        """查找 ODA File Converter（内置 → 系统安装 → PATH）"""
        # 1. 内置
        bundled = _resource_path("oda_converter") / "ODAFileConverter.exe"
        if bundled.exists():
            return str(bundled)
        # 2. 开发目录
        dev = Path(__file__).parent / "oda_converter" / "ODAFileConverter.exe"
        if dev.exists():
            return str(dev)
        # 3. 系统安装
        for base in [r"C:\Program Files\ODA", r"C:\Program Files (x86)\ODA"]:
            for d in sorted(glob.glob(os.path.join(base, "ODAFileConverter*")), reverse=True):
                p = os.path.join(d, "ODAFileConverter.exe")
                if os.path.exists(p):
                    return p
        # 4. 标准路径
        for p in [
            r"C:\Program Files\ODA\ODAFileConverter\ODAFileConverter.exe",
            r"C:\Program Files (x86)\ODA\ODAFileConverter\ODAFileConverter.exe",
        ]:
            if os.path.exists(p):
                return p
        # 5. PATH
        oda = shutil.which("ODAFileConverter.exe")
        if oda:
            return oda
        return None

    def _convert_via_oda(self, oda_exe):
        """通过 ODA File Converter 将 DWG → PDF"""
        try:
            temp_dir = tempfile.mkdtemp(prefix="dwg_")
            shutil.copy2(self.current_path, os.path.join(temp_dir, Path(self.current_path).name))

            self.status_var.set("ODA 转换中，请稍候...")
            self.root.update_idletasks()

            proc = subprocess.run([
                oda_exe, temp_dir, temp_dir,
                "ACAD2024", "PDF", "0", "1",
            ], capture_output=True, text=True, timeout=120,
               cwd=os.path.dirname(oda_exe),
               creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))

            dwg_stem = Path(self.current_path).stem
            expected_pdf = os.path.join(temp_dir, f"{dwg_stem}.pdf")

            if os.path.exists(expected_pdf):
                self.current_path = expected_pdf
                self.file_type = 'pdf'
                self.pdf_images_meta = [{'type': 'dwg'}]
                self.convert_pdf_to_images()
                self.status_var.set(f"DWG→PDF: {dwg_stem}.pdf 共 {len(self.pdf_images)} 页")
                return True

            pdf_files = list(Path(temp_dir).glob("*.pdf"))
            if pdf_files:
                self.current_path = str(pdf_files[0])
                self.file_type = 'pdf'
                self.pdf_images_meta = [{'type': 'dwg'}]
                self.convert_pdf_to_images()
                return True

            print(f"ODA 失败: {proc.stderr[:300] if proc.stderr else '无输出'}")
            return False
        except subprocess.TimeoutExpired:
            print("ODA 超时")
            return False
        except Exception as e:
            print(f"ODA 错误: {e}")
            return False

    def _convert_via_autocad(self):
        """通过 AutoCAD COM 自动化将 DWG → PDF"""
        try:
            import win32com.client
            import pythoncom
            pythoncom.CoInitialize()

            try:
                acad = win32com.client.GetActiveObject('AutoCAD.Application')
            except Exception:
                try:
                    acad = win32com.client.Dispatch('AutoCAD.Application')
                    acad.Visible = False
                except Exception:
                    return False

            self.status_var.set("AutoCAD 转换中...")
            self.root.update_idletasks()

            doc = acad.Documents.Open(self.current_path)
            dwg_stem = Path(self.current_path).stem
            temp_dir = tempfile.mkdtemp(prefix="dwg_acad_")

            try:
                # 方法1: 导出为 PDF
                pdf_path = os.path.join(temp_dir, f"{dwg_stem}.pdf")
                doc.Export(pdf_path, "PDF", doc.ActiveLayout)
            except Exception:
                try:
                    # 方法2: Plot 到 PDF
                    layout = doc.ActiveLayout
                    layout.RefreshPlotDeviceInfo()
                    pdf_path = os.path.join(temp_dir, f"{dwg_stem}_plot.pdf")
                    doc.Plot.PlotToFile(pdf_path)
                except Exception:
                    doc.Close(False)
                    pythoncom.CoUninitialize()
                    return False

            doc.Close(False)
            pythoncom.CoUninitialize()

            if os.path.exists(pdf_path):
                self.current_path = pdf_path
                self.file_type = 'pdf'
                self.pdf_images_meta = [{'type': 'dwg', 'via': 'autocad'}]
                self.convert_pdf_to_images()
                self.status_var.set(f"DWG→PDF (AutoCAD): {dwg_stem}.pdf 共 {len(self.pdf_images)} 页")
                return True
            return False
        except ImportError:
            return False
        except Exception as e:
            print(f"AutoCAD COM 错误: {e}")
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass
            return False

    def _read_xlsx_text(self):
        """纯标准库解析 .xlsx，零第三方依赖"""
        try:
            import zipfile, xml.etree.ElementTree as ET
            texts = []
            with zipfile.ZipFile(self.current_path, 'r') as z:
                # 读取共享字符串表
                shared = []
                if 'xl/sharedStrings.xml' in z.namelist():
                    root = ET.fromstring(z.read('xl/sharedStrings.xml'))
                    ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
                    shared = [si.findtext('.//s:t', '', ns) for si in root.findall('s:si', ns)]

                # 读取所有 sheet
                for name in z.namelist():
                    if name.startswith('xl/worksheets/sheet') and name.endswith('.xml'):
                        root = ET.fromstring(z.read(name))
                        ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
                        rows = []
                        for row in root.findall('.//s:row', ns):
                            cells = []
                            for c in row.findall('s:c', ns):
                                v = c.findtext('s:v', '', ns)
                                t = c.get('t', '')
                                if t == 's' and v and shared:
                                    v = shared[int(v)] if int(v) < len(shared) else v
                                cells.append(v)
                            rows.append('\t'.join(cells))
                        texts.append(f'--- Sheet: {name.split("/")[-1]} ---\n' + '\n'.join(rows))

            return '\n\n'.join(texts) if texts else '（空表格或无数据）'
        except Exception as e:
            print(f"读取 xlsx 失败: {e}")
            return f'[错误] 读取 Excel 文件失败: {e}'

    def _read_pptx_text(self):
        """纯标准库解析 .pptx，零第三方依赖"""
        try:
            import zipfile, xml.etree.ElementTree as ET
            texts = []
            ns = {'a': 'http://schemas.openxmlformats.org/drawingml/2006/main',
                  'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'}
            with zipfile.ZipFile(self.current_path, 'r') as z:
                for name in sorted(z.namelist()):
                    if name.startswith('ppt/slides/slide') and name.endswith('.xml'):
                        root = ET.fromstring(z.read(name))
                        # 提取所有 a:t 文本元素
                        slide_texts = []
                        for t in root.iter('{http://schemas.openxmlformats.org/drawingml/2006/main}t'):
                            if t.text:
                                slide_texts.append(t.text)
                        slide_num = name.split('/')[-1].replace('slide', '').replace('.xml', '')
                        texts.append(f'--- 第 {slide_num} 页 ---\n' + '\n'.join(slide_texts))

            return '\n\n'.join(texts) if texts else '（空演示文稿）'
        except Exception as e:
            print(f"读取 pptx 失败: {e}")
            return f'[错误] 读取 PPT 文件失败: {e}'

    def _preprocess_ocr_text(self, text):
        """OCR 文本预处理：全角→半角、符号统一、常见 OCR 误识修正"""
        if not text:
            return text
        result = fullwidth_to_halfwidth(text)
        # 全角符号→半角（补充 normalize_for_matching 未覆盖的符号）
        extra_punct = {
            '\u3000': ' ',   # 全角空格
            '\u00B7': '.',   # 中间点 ·
            '\u2022': '.',   # 项目符号 •
            '\u25CB': '0',   # ○ → 0
            '\u25CF': '.',   # ● → .（常用作分段标记）
            '\u3010': '[',   # 【
            '\u3011': ']',   # 】
            '\u3008': '<',   # 〈
            '\u3009': '>',   # 〉
            '\u300A': '<',   # 《
            '\u300B': '>',   # 》
        }
        for cn, en in extra_punct.items():
            result = result.replace(cn, en)
        return result

    def _extract_codes_from_text(self, text):
        """Extract standard codes and names from text and populate list."""
        # ── OCR 文本预处理：全角字母数字→半角 + 符号统一 ──
        text = self._preprocess_ocr_text(text)
        raw_codes = CODE_PATTERN.findall(text)
        raw_names = NAME_PATTERN.findall(text)
        name_map = {}
        for raw_name in raw_names:
            cleaned = normalize_for_matching(raw_name).strip()
            if cleaned:
                name_map[cleaned] = raw_name

        # Also extract names by looking at text after each code
        for code in raw_codes:
            # Find the code position in text
            code_pos = text.find(code)
            if code_pos >= 0:
                # Look at text after the code (up to 80 chars)
                after_text = text[code_pos + len(code):code_pos + len(code) + 80]
                # Extract Chinese text (the name)
                name_match = re.search(r'[一-鿿]{2,30}', after_text)
                if name_match:
                    potential_name = name_match.group(0)
                    cleaned_name = normalize_for_matching(potential_name).strip()
                    if cleaned_name and len(cleaned_name) > 2:
                        name_map[cleaned_name] = potential_name

        seen = set()
        self.extracted_codes = []
        self.extracted_code_info = {}
        for code in raw_codes:
            normalized = normalize_for_matching(code)
            if normalized not in seen:
                seen.add(normalized)
                self.extracted_codes.append(code)
                # Try to find matching name
                matched_name = ''
                for cname in name_map:
                    if normalized.replace(' ', '') in cname.replace(' ', '') or cname.replace(' ', '') in normalized.replace(' ', ''):
                        matched_name = name_map[cname]
                        break
                self.extracted_code_info[normalized] = {
                    'name': matched_name,
                    'original': code,
                }

        for i, code in enumerate(self.extracted_codes, 1):
            info = self.extracted_code_info.get(normalize_for_matching(code), {})
            name = info.get('name', '')
            self.list_tree.insert('', tk.END, values=(i, code, name))

        if self.extracted_codes:
            self.notebook.select(self.list_tree.master)
        self.status_var.set(f"提取完成: 识别到 {len(self.extracted_codes)} 个规范编号")

    def show_page(self, idx):
        if idx < 0 or idx >= len(self.pdf_images):
            return

        # 检查 canvas 是否已就绪（布局完成），否则延迟重试
        canvas_w = self.pdf_canvas.winfo_width() or 0
        canvas_h = self.pdf_canvas.winfo_height() or 0
        if canvas_w < 50 or canvas_h < 50:
            self.root.after(100, lambda: self.show_page(idx))
            return

        self.pdf_canvas.delete('all')
        img_path = self.pdf_images[idx]
        pil, _ = _get_pil()
        Image = pil['Image']
        ImageTk = pil['ImageTk']
        # 关闭旧图片资源
        if hasattr(self, '_current_base_image') and self._current_base_image:
            try:
                self._current_base_image.close()
            except Exception:
                pass
        img = Image.open(img_path)
        self._current_base_image = img
        img_w, img_h = img.size
        base_scale = min(canvas_w / img_w, canvas_h / img_h)
        zoom = getattr(self, '_zoom_level', 1.0)
        scale = base_scale * zoom
        new_w, new_h = int(img_w * scale), int(img_h * scale)
        img_resized = img.resize((new_w, new_h), Image.Resampling.BOX)
        self.current_img = ImageTk.PhotoImage(img_resized)
        center_x = canvas_w // 2 + getattr(self, '_pan_image_x', 0)
        center_y = canvas_h // 2 + getattr(self, '_pan_image_y', 0)
        self.current_image_item = self.pdf_canvas.create_image(center_x, center_y, image=self.current_img)
        self.page_var.set(f"第 {idx + 1} / {len(self.pdf_images)} 页")
        self.current_display_index = idx

        # Update selector target
        if self.selector:
            self.selector.image_item_id = self.current_image_item

        # Draw existing region if any
        if self.ocr_region:
            self._draw_region_overlay(self.ocr_region, scale)

        # Draw code location markers for this page
        self._draw_code_markers_for_page(idx, scale)

        # 翻页后重建激活的高亮框
        self._rebuild_highlight()

    def _prev_page(self):
        if not self.pdf_images:
            return
        idx = getattr(self, 'current_display_index', 0) - 1
        if idx < 0:
            idx = len(self.pdf_images) - 1
        self.show_page(idx)

    def _next_page(self):
        if not self.pdf_images:
            return
        idx = getattr(self, 'current_display_index', 0) + 1
        if idx >= len(self.pdf_images):
            idx = 0
        self.show_page(idx)

    def _zoom_in(self):
        """Zoom in on PDF preview (debounced)."""
        if not hasattr(self, '_zoom_level'):
            self._zoom_level = 1.0
        self._zoom_level = min(self._zoom_level * 1.2, 5.0)
        self._schedule_zoom_update()

    def _zoom_out(self):
        """Zoom out on PDF preview (debounced)."""
        if not hasattr(self, '_zoom_level'):
            self._zoom_level = 1.0
        self._zoom_level = max(self._zoom_level / 1.2, 0.2)
        self._schedule_zoom_update()

    def _on_mouse_wheel(self, event):
        """Handle mouse wheel zoom — 更灵敏：ctrl+滚轮=缩放，纯滚轮=上下翻页"""
        if not hasattr(self, '_zoom_level'):
            self._zoom_level = 1.0

        # Ctrl+滚轮：缩放
        if event.state & 0x4:  # Ctrl 键
            if event.delta > 0:
                self._zoom_level = min(self._zoom_level * 1.2, 8.0)
            else:
                self._zoom_level = max(self._zoom_level / 1.2, 0.1)
            self._schedule_zoom_update()
        else:
            # 纯滚轮：上下滚动预览区域，同时翻页
            if event.delta > 0:
                self._prev_page()
            else:
                self._next_page()

    def _schedule_zoom_update(self):
        """Debounce zoom: 合并连续缩放事件，80ms 内只做一次重渲染"""
        if hasattr(self, '_zoom_timer') and self._zoom_timer:
            try:
                self.root.after_cancel(self._zoom_timer)
            except Exception:
                pass
        self._zoom_timer = self.root.after(80, self._apply_zoom)

    def _apply_zoom(self):
        """实际执行缩放后的重渲染（使用快速滤波）"""
        self._zoom_timer = None
        if not hasattr(self, '_current_base_image') or not self._current_base_image:
            return
        if not self.pdf_images:
            return
        pil, _ = _get_pil()
        Image = pil['Image']
        ImageTk = pil['ImageTk']
        img = self._current_base_image
        img_w, img_h = img.size
        canvas_w = self.pdf_canvas.winfo_width() or 400
        canvas_h = self.pdf_canvas.winfo_height() or 600

        base_scale = min(canvas_w / img_w, canvas_h / img_h)
        scale = base_scale * getattr(self, '_zoom_level', 1.0)

        new_w, new_h = int(img_w * scale), int(img_h * scale)
        # 交互中使用 BOX 滤波（比 LANCZOS 快 5x），最终画质差异肉眼不可见
        img_resized = img.resize((new_w, new_h), Image.Resampling.BOX)
        self.pdf_canvas.delete('all')
        self.current_img = ImageTk.PhotoImage(img_resized)
        center_x = canvas_w // 2 + getattr(self, '_pan_image_x', 0)
        center_y = canvas_h // 2 + getattr(self, '_pan_image_y', 0)
        self.current_image_item = self.pdf_canvas.create_image(
            center_x, center_y, image=self.current_img
        )

        # Redraw overlays
        if self.ocr_region:
            self._draw_region_overlay(self.ocr_region, scale)
        if hasattr(self, 'current_display_index'):
            self._draw_code_markers_for_page(self.current_display_index, scale)

        # 重建高亮框（delete('all') 已清除）
        self._rebuild_highlight()

    def _reset_zoom(self):
        """Reset zoom to default."""
        self._zoom_level = 1.0
        self._pan_image_x = 0
        self._pan_image_y = 0
        self._redraw_current_page()

    def _on_pan_start(self, event):
        """Start panning the preview — 使用 canvas.move 避免重渲染"""
        self._panning = True
        self._pan_start_x = event.x
        self._pan_start_y = event.y
        self.pdf_canvas.config(cursor="fleur")

    def _on_pan_drag(self, event):
        """Pan by moving canvas items directly — 零重渲染，即时响应"""
        if not self._panning:
            return
        dx = event.x - self._pan_start_x
        dy = event.y - self._pan_start_y
        self._pan_start_x = event.x
        self._pan_start_y = event.y
        self._pan_image_x += dx
        self._pan_image_y += dy

        # 只移动已有画布项，不重渲染
        if hasattr(self, 'current_image_item') and self.current_image_item:
            try:
                self.pdf_canvas.move(self.current_image_item, dx, dy)
            except Exception:
                pass
        if hasattr(self, '_code_marker_ids'):
            for mid in list(self._code_marker_ids):
                try:
                    self.pdf_canvas.move(mid, dx, dy)
                except Exception:
                    pass
        if hasattr(self, '_region_overlay_id') and self._region_overlay_id:
            try:
                self.pdf_canvas.move(self._region_overlay_id, dx, dy)
            except Exception:
                pass
        if hasattr(self, '_highlight_rect_id') and self._highlight_rect_id:
            try:
                self.pdf_canvas.move(self._highlight_rect_id, dx, dy)
            except Exception:
                pass
        if hasattr(self, '_highlight_fill_id') and self._highlight_fill_id:
            try:
                self.pdf_canvas.move(self._highlight_fill_id, dx, dy)
            except Exception:
                pass
        if hasattr(self, '_highlight_label_id') and self._highlight_label_id:
            try:
                self.pdf_canvas.move(self._highlight_label_id, dx, dy)
            except Exception:
                pass

    def _on_pan_end(self, event):
        """End panning."""
        self._panning = False
        self.pdf_canvas.config(cursor="")

    def _draw_code_markers_for_page(self, page_idx, scale):
        """Draw markers on canvas for standard codes found on this page."""
        if not hasattr(self, 'current_image_item'):
            return
        # Remove old markers
        if hasattr(self, '_code_marker_ids'):
            for marker_id in self._code_marker_ids:
                self.pdf_canvas.delete(marker_id)
        self._code_marker_ids = []

        # Account for centered image offset on canvas
        offset_x = 0
        offset_y = 0
        if hasattr(self, '_current_base_image') and self._current_base_image and scale:
            canvas_w = self.pdf_canvas.winfo_width() or 400
            canvas_h = self.pdf_canvas.winfo_height() or 600
            img_w, img_h = self._current_base_image.size
            new_w, new_h = int(img_w * scale), int(img_h * scale)
            offset_x = (canvas_w - new_w) // 2 + getattr(self, '_pan_image_x', 0)
            offset_y = (canvas_h - new_h) // 2 + getattr(self, '_pan_image_y', 0)

        # Find codes on this page
        page_codes = [loc for loc in self.code_locations if loc['page'] == page_idx]
        for loc in page_codes:
            x1, y1, x2, y2 = loc['bbox']
            if scale:
                x1, y1, x2, y2 = x1 * scale, y1 * scale, x2 * scale, y2 * scale
            x1 += offset_x
            y1 += offset_y
            x2 += offset_x
            y2 += offset_y
            rect_id = self.pdf_canvas.create_rectangle(
                x1, y1, x2, y2,
                outline='red', width=2, dash=(4, 2)
            )
            self._code_marker_ids.append(rect_id)
            # Add label
            label_id = self.pdf_canvas.create_text(
                x1, y1 - 12, text=loc['code'],
                fill='red', anchor='sw', font=(self._font_family, 9)
            )
            self._code_marker_ids.append(label_id)

    def _draw_region_overlay(self, region, scale):
        if not hasattr(self, 'current_image_item'):
            return
        x1, y1, x2, y2 = region
        if scale:
            x1, y1, x2, y2 = x1 * scale, y1 * scale, x2 * scale, y2 * scale

        # Account for centered image offset on canvas
        if hasattr(self, '_current_base_image') and self._current_base_image and scale:
            canvas_w = self.pdf_canvas.winfo_width() or 400
            canvas_h = self.pdf_canvas.winfo_height() or 600
            img_w, img_h = self._current_base_image.size
            new_w, new_h = int(img_w * scale), int(img_h * scale)
            offset_x = (canvas_w - new_w) // 2 + getattr(self, '_pan_image_x', 0)
            offset_y = (canvas_h - new_h) // 2 + getattr(self, '_pan_image_y', 0)
            x1 += offset_x
            y1 += offset_y
            x2 += offset_x
            y2 += offset_y

        if hasattr(self, '_region_overlay_id') and self._region_overlay_id:
            self.pdf_canvas.delete(self._region_overlay_id)
        self._region_overlay_id = self.pdf_canvas.create_rectangle(
            x1, y1, x2, y2, outline='red', width=2, dash=(4, 2)
        )

    def _on_canvas_resize(self, event):
        """Redraw current PDF page when canvas is resized (debounced)."""
        if not hasattr(self, '_current_base_image') or not self._current_base_image or not self.pdf_images:
            return
        # 去抖：resize 事件频繁触发，合并到下一个帧
        if hasattr(self, '_resize_timer') and self._resize_timer:
            try:
                self.root.after_cancel(self._resize_timer)
            except Exception:
                pass
        self._resize_timer = self.root.after(150, self._do_deferred_resize)

    def _do_deferred_resize(self):
        """实际执行 resize 重绘"""
        self._resize_timer = None
        self._redraw_current_page()

    def _start_periodic_redraw(self):
        """Start periodic check for canvas resize."""
        if not hasattr(self, 'pdf_canvas') or not self.pdf_canvas:
            return  # UI not ready yet
        self._last_canvas_size = (self.pdf_canvas.winfo_width(), self.pdf_canvas.winfo_height())
        self._periodic_redraw()

    def _periodic_redraw(self):
        """Periodically check if canvas size changed and redraw."""
        if hasattr(self, 'pdf_canvas') and self.pdf_canvas:
            if hasattr(self, '_current_base_image') and self._current_base_image and self.pdf_images:
                current_size = (self.pdf_canvas.winfo_width(), self.pdf_canvas.winfo_height())
                if current_size != getattr(self, '_last_canvas_size', None):
                    self._last_canvas_size = current_size
                    if current_size[0] > 10 and current_size[1] > 10:
                        self._redraw_current_page()
        self.root.after(200, self._periodic_redraw)

    def _redraw_current_page(self, canvas_w=None, canvas_h=None):
        """Redraw current page with current zoom level and optional canvas size."""
        if not hasattr(self, '_current_base_image') or not self._current_base_image:
            return
        if not self.pdf_images:
            return
        
        pil, _ = _get_pil()
        Image = pil['Image']
        ImageTk = pil['ImageTk']
        img = self._current_base_image
        img_w, img_h = img.size
        canvas_w = canvas_w or self.pdf_canvas.winfo_width() or 400
        canvas_h = canvas_h or self.pdf_canvas.winfo_height() or 600
        
        # Calculate scale with zoom level
        base_scale = min(canvas_w / img_w, canvas_h / img_h)
        scale = base_scale * getattr(self, '_zoom_level', 1.0)
        
        new_w, new_h = int(img_w * scale), int(img_h * scale)
        img_resized = img.resize((new_w, new_h), Image.Resampling.BOX)
        
        self.pdf_canvas.delete('all')
        self.current_img = ImageTk.PhotoImage(img_resized)
        center_x = canvas_w // 2 + getattr(self, '_pan_image_x', 0)
        center_y = canvas_h // 2 + getattr(self, '_pan_image_y', 0)
        self.current_image_item = self.pdf_canvas.create_image(
            center_x, center_y, image=self.current_img
        )
        
        # Redraw region overlay if exists
        if self.ocr_region:
            self._draw_region_overlay(self.ocr_region, scale)
        
        # Redraw code markers for current page
        if hasattr(self, 'current_display_index'):
            self._draw_code_markers_for_page(self.current_display_index, scale)

        # 重建高亮框（delete('all') 已清除）
        self._rebuild_highlight()

    def start_selection(self):
        if not self.pdf_images:
            messagebox.showwarning("提示", "请先打开PDF文件")
            return
        self.selection_mode = True
        self.status_var.set("请在预览图上拖拽选择识别区域，右键或再次点击按钮取消")
        if self.selector:
            self.selector.enable()
            self.selector.image_item_id = getattr(self, 'current_image_item', None)

    def clear_region(self):
        self.ocr_region = None
        if hasattr(self, '_region_overlay_id') and self._region_overlay_id:
            self.pdf_canvas.delete(self._region_overlay_id)
            self._region_overlay_id = None
        self.region_var.set("识别区域：未设置（全页识别）")
        self.status_var.set("已清除识别区域，将使用全页识别")

    def _on_region_selected(self, region):
        # Convert canvas drag coordinates back to original image coordinates
        if hasattr(self, '_current_base_image') and self._current_base_image:
            img_w, img_h = self._current_base_image.size
            canvas_w = self.pdf_canvas.winfo_width() or 400
            canvas_h = self.pdf_canvas.winfo_height() or 600
            scale = min(canvas_w / img_w, canvas_h / img_h)
            new_w, new_h = int(img_w * scale), int(img_h * scale)
            offset_x = (canvas_w - new_w) // 2
            offset_y = (canvas_h - new_h) // 2

            x1, y1, x2, y2 = region
            self.ocr_region = (
                max(0, (x1 - offset_x) / scale),
                max(0, (y1 - offset_y) / scale),
                min(img_w, (x2 - offset_x) / scale),
                min(img_h, (y2 - offset_y) / scale),
            )
        else:
            self.ocr_region = region

        self.selection_mode = False
        self.region_var.set(f"识别区域：({int(self.ocr_region[0])}, {int(self.ocr_region[1])}) -> ({int(self.ocr_region[2])}, {int(self.ocr_region[3])})")
        self.status_var.set("识别区域已设置，后续页面将按此区域识别")
        # Redraw overlay in original image coordinates
        if hasattr(self, '_current_base_image') and self._current_base_image:
            img_w, img_h = self._current_base_image.size
            canvas_w = self.pdf_canvas.winfo_width() or 400
            canvas_h = self.pdf_canvas.winfo_height() or 600
            scale = min(canvas_w / img_w, canvas_h / img_h)
            self._draw_region_overlay(self.ocr_region, scale)

    def remove_selected_code(self, event=None):
        selected = self.list_tree.selection()
        if not selected:
            return
        for item in selected:
            vals = self.list_tree.item(item, 'values')
            if len(vals) < 2:
                self.list_tree.delete(item)
                continue
            orig_code = vals[1]  # 原始规范编号（第2列）
            norm = normalize_for_matching(orig_code)
            # 从 extracted_codes 列表移除（该列表存储原始编号）
            if orig_code in self.extracted_codes:
                self.extracted_codes.remove(orig_code)
            elif norm in self.extracted_codes:
                self.extracted_codes.remove(norm)
            # 同步清除 extracted_code_info 缓存
            self.extracted_code_info.pop(norm, None)
            self.list_tree.delete(item)
        for i, item in enumerate(self.list_tree.get_children(), 1):
            vals = self.list_tree.item(item, 'values')
            name = vals[2] if len(vals) > 2 else ''
            self.list_tree.item(item, values=(i, vals[1], name))
        self.status_var.set(f"已移除选中项，剩余 {len(self.extracted_codes)} 个规范")

    def on_list_item_double_click(self, event=None):
        """双击规范识别列表项（左侧）→ 弹出搜索对话框。"""
        selected = self.list_tree.selection()
        if not selected:
            return
        item = selected[0]
        values = self.list_tree.item(item, 'values')
        if len(values) < 2:
            return
        code = values[1]
        name = values[2] if len(values) > 2 else ''

        dialog = StandardSearchDialog(self, self.checker, code=code, name=name)
        self.wait_window(dialog)

    def on_code_selected(self, event=None):
        """When user selects a code in the list, navigate to its page and highlight.

        Supports all image-based file types: PDF, DWG→DXF→image, DXF→image.
        Also supports text files: scrolls to the matching text location.
        """
        selected = self.list_tree.selection()
        if not selected:
            return
        item = selected[0]
        values = self.list_tree.item(item, 'values')
        code = values[1]
        name = values[2] if len(values) > 2 else ''

        # Update preview name label
        preview_name = f"{code} {name}".strip()
        if hasattr(self, '_preview_name_var'):
            self._preview_name_var.set(preview_name)

        # ── 图片型文件（PDF / DWG / DXF）：切页 + 高亮 ──
        if self.pdf_images:
            code_norm = normalize_for_matching(code)
            # 优先从 code_locations 找到精确位置（有 bbox）
            target_loc = None
            for loc in self.code_locations:
                if normalize_for_matching(loc['code']) == code_norm:
                    target_loc = loc
                    break

            if target_loc:
                # 有 OCR bbox → 精准高亮（内部自动处理切页）
                bbox = target_loc.get('bbox', (0, 0, 0, 0))
                if not all(v == 0 for v in bbox):
                    self._highlight_code_location(target_loc)
                else:
                    # bbox 为空 → 先切页再用 fitz 搜索
                    page_idx = target_loc.get('page', 0)
                    if page_idx != getattr(self, 'current_display_index', -1):
                        self.show_page(page_idx)
                        self.root.update_idletasks()
                    self._highlight_standard_on_preview(code, name)
            else:
                # OCR 结果里没有，用 fitz 搜索当前页
                self._highlight_standard_on_preview(code, name)

        # ── 文本型文件（docx / xlsx / pptx / txt）：在文字控件中跳转 ──
        self._highlight_code_in_text(code)

    def _highlight_code_in_text(self, code):
        """在 OCR 文本框中高亮指定规范编号，并滚动到第一个匹配位置。"""
        if not hasattr(self, 'ocr_text'):
            return
        self.ocr_text.tag_remove('highlight', '1.0', tk.END)
        if not code:
            return
        first_pos = None
        start = '1.0'
        while True:
            pos = self.ocr_text.search(code, start, stopindex=tk.END, nocase=True)
            if not pos:
                break
            end = f"{pos}+{len(code)}c"
            self.ocr_text.tag_add('highlight', pos, end)
            if first_pos is None:
                first_pos = pos
            start = end
        self.ocr_text.tag_config('highlight', background='#FFFF00', foreground='#CC0000',
                                  font=(self._font_family, 10, 'bold'))
        # 滚动到第一个匹配位置
        if first_pos:
            self.ocr_text.see(first_pos)
            self.ocr_text.mark_set(tk.INSERT, first_pos)

    def _crop_image_to_region(self, image_path, region):
        """按选定区域裁剪图片，返回临时文件路径"""
        if region is None:
            return image_path
        try:
            pil, _ = _get_pil()
            Image = pil['Image']
            img = Image.open(image_path)
            x1, y1, x2, y2 = region
            left = max(0, int(x1))
            top = max(0, int(y1))
            right = min(img.width, int(x2))
            bottom = min(img.height, int(y2))
            if right <= left or bottom <= top:
                return image_path
            cropped = img.crop((left, top, right, bottom))
            out = tempfile.mktemp(suffix='.png')
            cropped.save(out)
            return out
        except Exception as e:
            print(f"crop error: {e}")
            return image_path

    def _detect_and_split_columns(self, image_path):
        """Detect two-column layout and split image if needed. Returns list of image paths."""
        try:
            pil, _ = _get_pil()
            Image = pil['Image']
            img = Image.open(image_path).convert('L')
            w, h = img.size
            if w < 300:
                return [image_path]

            # Downscale for fast analysis
            analysis_w = 120
            analysis_h = max(1, int(h * analysis_w / w))
            analysis_img = img.resize((analysis_w, analysis_h), Image.Resampling.BOX)
            # Threshold: text pixels are dark
            binary = analysis_img.point(lambda p: 255 if p > 150 else 0)

            width, height = binary.size
            profile = [0] * width
            pixels = list(binary.getdata())
            for y in range(height):
                for x in range(width):
                    if pixels[y * width + x] == 0:
                        profile[x] += 1

            # Smooth profile
            smoothed = []
            for i in range(width):
                start = max(0, i - 4)
                end = min(width, i + 5)
                smoothed.append(sum(profile[start:end]) / (end - start))

            # Find valley in middle 50%
            mid_start = int(width * 0.25)
            mid_end = int(width * 0.75)
            mid_vals = smoothed[mid_start:mid_end]
            if not mid_vals:
                return [image_path]

            min_idx = mid_vals.index(min(mid_vals))
            split_x = mid_start + min_idx

            # Check if valley is significant compared to surrounding density
            left_start = max(0, split_x - 20)
            right_end = min(width, split_x + 21)
            left_avg = sum(smoothed[left_start:split_x]) / max(1, split_x - left_start)
            right_avg = sum(smoothed[split_x + 1:right_end]) / max(1, right_end - split_x - 1)
            valley = smoothed[split_x]
            threshold = max(3, (left_avg + right_avg) * 0.25)

            if left_avg > threshold and right_avg > threshold and valley < threshold:
                scale = w / width
                split_original = max(1, int(split_x * scale))
                img_rgb = Image.open(image_path)
                left = img_rgb.crop((0, 0, split_original, h))
                right = img_rgb.crop((split_original, 0, w, h))
                left_path = tempfile.mktemp(suffix='.png')
                right_path = tempfile.mktemp(suffix='.png')
                left.save(left_path)
                right.save(right_path)
                print(f"  Detected two-column layout, split at x={split_original}")
                return [left_path, right_path]

            return [image_path]
        except Exception as e:
            print(f"column detect error: {e}")
            return [image_path]

    def _split_pdf_page_to_columns(self, image_path):
        """Split PDF page image into two columns for A3 two-column layout."""
        try:
            pil, _ = _get_pil()
            Image = pil['Image']
            img = Image.open(image_path)
            w, h = img.size
            # Always split at middle for A3 two-column format
            split_x = w // 2
            left = img.crop((0, 0, split_x, h))
            right = img.crop((split_x, 0, w, h))
            left_path = tempfile.mktemp(suffix='.png')
            right_path = tempfile.mktemp(suffix='.png')
            left.save(left_path)
            right.save(right_path)
            print(f"  Split A3 page at x={split_x}")
            return [left_path, right_path]
        except Exception as e:
            print(f"pdf column split error: {e}")
            return [image_path]

    def start_ocr(self):
        mode = self._left_mode_var.get()

        if mode == 'text':
            # Text input mode: extract codes from pasted text directly
            text = self._get_active_text()
            if not text:
                messagebox.showwarning("提示", "请在左侧文本框中粘贴需要检查的内容")
                return

            self.status_var.set("正在从文本中提取规范编号...")
            self.progress_var.set(0)
            self.extracted_codes = []
            self.code_locations = []
            self.check_results = []
            self.ocr_results = [text]
            self.list_tree.delete(*self.list_tree.get_children())
            self.check_tree.delete(*self.check_tree.get_children())

            # Show text in OCR tab
            self.ocr_text.delete('1.0', tk.END)
            self.ocr_text.insert(tk.END, text)

            # Extract codes
            self._extract_codes_from_text(text)
            self.progress_var.set(100)
            self.status_var.set(f"文本提取完成: 识别到 {len(self.extracted_codes)} 个规范编号")
            self.notebook.select(self.list_tree.master)
            return

        # File mode: original OCR logic
        if self.file_type == 'pdf':
            if not self.pdf_images:
                messagebox.showwarning("提示", "请先打开PDF文件")
                return
        elif self.file_type in ('docx', 'txt'):
            if not self.current_path:
                messagebox.showwarning("提示", "请先打开文件")
                return
        else:
            messagebox.showwarning("提示", "不支持的文件格式")
            return

        self.status_var.set("开始 OCR 识别...")
        self.progress_var.set(0)
        self.ocr_text.delete('1.0', tk.END)
        self.ocr_results = []
        self.extracted_codes = []
        self.code_locations = []
        self.list_tree.delete(*self.list_tree.get_children())

        self._ocr_queue = []
        self._ocr_done = False

        def do_ocr():
            pil, _ = _get_pil()
            Image = pil['Image']
            try:
                page_code_blocks = {}  # normalized_code -> [(page_idx, bbox), ...]

                if self.file_type == 'pdf':
                    total = len(self.pdf_images)
                    for i, img_path in enumerate(self.pdf_images):
                        page_blocks = []
                        try:
                            cropped_path = self._crop_image_to_region(img_path, self.ocr_region)
                            crop_offsets = (0, 0)
                            if self.ocr_region and cropped_path != img_path:
                                crop_offsets = (int(self.ocr_region[0]), int(self.ocr_region[1]))
                            masked_path = mask_seals_pil(cropped_path)
                            column_paths = []
                            try:
                                column_paths = self._split_pdf_page_to_columns(masked_path)
                                page_texts = []
                                split_x = 0
                                if len(column_paths) == 2:
                                    with Image.open(masked_path) as _img:
                                        split_x = _img.width // 2
                                for col_idx, col_path in enumerate(column_paths):
                                    try:
                                        t, blocks = self.checker.ocr_image(col_path)
                                        page_texts.append(t)
                                        for block_text, bbox in blocks:
                                            x1, y1, x2, y2 = bbox
                                            if col_idx == 1:
                                                x1 += split_x
                                                x2 += split_x
                                            if crop_offsets != (0, 0):
                                                x1 += crop_offsets[0]
                                                y1 += crop_offsets[1]
                                                x2 += crop_offsets[0]
                                                y2 += crop_offsets[1]
                                            page_blocks.append((block_text, (x1, y1, x2, y2)))
                                    finally:
                                        if col_path != masked_path and os.path.exists(col_path):
                                            try:
                                                os.remove(col_path)
                                            except Exception:
                                                pass
                                text = '\n'.join(page_texts)
                            finally:
                                if masked_path != cropped_path and os.path.exists(masked_path):
                                    try:
                                        os.remove(masked_path)
                                    except Exception:
                                        pass
                                if cropped_path != img_path and os.path.exists(cropped_path):
                                    try:
                                        os.remove(cropped_path)
                                    except Exception:
                                        pass
                        except Exception as page_error:
                            text = f"OCR_PAGE_ERROR: {page_error}"
                            print(f"OCR page {i+1} error: {page_error}")

                        self.ocr_results.append(text)
                        self._ocr_queue.append(('page', i + 1, total, text))

                        page_cleaned = fullwidth_to_halfwidth(text)
                        page_codes = CODE_PATTERN.findall(page_cleaned)
                        for code in page_codes:
                            normalized = code.upper().strip()
                            if normalized not in page_code_blocks:
                                page_code_blocks[normalized] = []
                            bbox = (0, 0, 0, 0)
                            for block_text, block_bbox in page_blocks:
                                if normalized.replace(' ', '') in block_text.replace(' ', '') or block_text.replace(' ', '') in normalized.replace(' ', ''):
                                    bbox = block_bbox
                                    break
                            page_code_blocks[normalized].append((i, bbox))
                else:
                    # Non-PDF files: use stored text or re-read file
                    text = ''
                    if self.current_path and self.file_type in ('docx', 'txt'):
                        try:
                            if self.file_type == 'docx':
                                doc = _get_docx()['Document'](self.current_path)
                                text = '\n'.join([p.text for p in doc.paragraphs])
                            else:
                                with open(self.current_path, 'r', encoding='utf-8', errors='ignore') as f:
                                    text = f.read()
                        except Exception:
                            text = '\n'.join(self.ocr_results)
                    else:
                        text = '\n'.join(self.ocr_results)
                    self._ocr_queue.append(('page', 1, 1, text))
                    cleaned = fullwidth_to_halfwidth(text)
                    raw_codes = CODE_PATTERN.findall(cleaned)
                    for code in raw_codes:
                        normalized = code.upper().strip()
                        if normalized not in page_code_blocks:
                            page_code_blocks[normalized] = []
                        page_code_blocks[normalized].append((0, (0, 0, 0, 0)))

                self._ocr_queue.append(('status', '正在提取规范编号...'))
                all_text = '\n'.join(self.ocr_results)
                cleaned_text = fullwidth_to_halfwidth(all_text)
                raw_names = NAME_PATTERN.findall(cleaned_text)
                name_map = {}
                for raw_name in raw_names:
                    name_map[raw_name.strip()] = raw_name

                seen = set()
                self.extracted_codes = []
                self.extracted_code_info = {}
                for code in list(page_code_blocks.keys()):
                    if code in seen:
                        continue
                    seen.add(code)
                    self.extracted_codes.append(code)
                    matched_name = ''
                    for cname in name_map:
                        if code.replace(' ', '') in cname.replace(' ', '') or cname.replace(' ', '') in code.replace(' ', ''):
                            matched_name = name_map[cname]
                            break
                    self.extracted_code_info[normalize_for_matching(code)] = {
                        'name': matched_name,
                        'original': code,
                    }
                    first_page, first_bbox = page_code_blocks[code][0]
                    self.code_locations.append({
                        'code': code,
                        'page': first_page,
                        'bbox': first_bbox,
                    })

                self._ocr_queue.append(('codes', self.extracted_codes))

            except Exception as e:
                self._ocr_queue.append(('status', f'OCR 出错: {e}'))
                print(f"OCR fatal error: {e}")
            finally:
                self._ocr_done = True

        def process_queue():
            if not self._ocr_queue and not self._ocr_done:
                self.root.after(50, process_queue)
                return

            while self._ocr_queue:
                item = self._ocr_queue.pop(0)
                kind = item[0]
                if kind == 'page':
                    _, page_no, total, text = item
                    self.ocr_text.insert(tk.END, f"--- 第{page_no}页 ---\n{text}\n\n")
                    self.ocr_text.see(tk.END)
                    self.progress_var.set(page_no / total * 100)
                    self.status_var.set(f"OCR 识别中: {page_no}/{total}")
                elif kind == 'status':
                    _, msg = item
                    self.status_var.set(msg)
                elif kind == 'codes':
                    codes = item[1]
                    self.list_tree.delete(*self.list_tree.get_children())
                    if codes:
                        for i, code in enumerate(codes, 1):
                            info = self.extracted_code_info.get(normalize_for_matching(code), {})
                            name = info.get('name', '')
                            self.list_tree.insert('', tk.END, values=(i, code, name))
                        self.notebook.select(self.list_tree.master)
                        self.status_var.set(f"OCR 完成: 识别到 {len(codes)} 个规范编号")
                    else:
                        # Show preview text so user can see what OCR actually got
                        sample = '\n'.join(self.ocr_results[:3])
                        self.list_tree.insert('', tk.END, values=(1, '【未识别到规范编号】'))
                        self.list_tree.insert('', tk.END, values=(2, '请查看左侧 OCR 识别文本 确认内容'))
                        if sample.strip():
                            self.list_tree.insert('', tk.END, values=(3, sample[:120].replace('\n', ' ')))
                        self.notebook.select(self.list_tree.master)
                        self.status_var.set("OCR 完成，但未识别到规范编号，请查看 OCR 文本")

                    self.progress_var.set(100)

                    # Redraw current page to show markers
                    if self.file_type == 'pdf' and self.pdf_images:
                        self.show_page(self.current_display_index)

            if not self._ocr_done or self._ocr_queue:
                self.root.after(50, process_queue)
            else:
                self._ocr_queue = []
                self._ocr_done = False

        threading.Thread(target=do_ocr, daemon=True).start()
        self.root.after(50, process_queue)

    def check_standards(self):
        if not self.extracted_codes:
            messagebox.showwarning("提示", "请先进行 OCR 识别并提取规范编号")
            return
        if not self.checker:
            messagebox.showerror("错误", "规范数据库未加载，无法进行检查。\n请重新启动程序。")
            return
        self.status_var.set("检查规范中...")
        self.progress_var.set(0)
        self.check_tree.delete(*self.check_tree.get_children())
        self.check_results = []

        # 更新列头显示更多信息
        self.check_tree.heading('std_type', text='标准类型')
        self.check_tree.heading('action_date', text='状态/日期')

        unique_codes = list(self.extracted_codes)
        total = len(unique_codes)
        for i, code in enumerate(unique_codes):
            info = self.extracted_code_info.get(normalize_for_matching(code), {})
            name = info.get('name', '')
            result = self.checker.check_code(code, name=name)
            self.check_results.append((code, result))

            status = result.get('status', '未找到')
            std_type = result.get('std_type', '')
            is_eng = result.get('is_eng', False)
            publish_date = result.get('publish_date', '')
            matched_name = result.get('matched_name', result.get('matched_code', '')) or name

            # 显示用编号
            display_code = code
            if result.get('matched_code') and normalize_for_matching(code) != normalize_for_matching(result['matched_code']):
                display_code = f"{code} → {result['matched_code']}"
            elif not result.get('found'):
                similar = self.checker.find_similar_codes(code, limit=2)
                if similar:
                    similar_str = '; '.join([f"{s[1]}"[:40] for s in similar])
                    display_code = f"{code} [相似:{similar_str}]"

            # 状态+操作
            if result.get('found'):
                if status in ('废止', '作废'):
                    action_text = '⚠ 需替换'
                elif status == '有更新版':
                    action_text = '📢 有新版'
                elif status == '即将实施':
                    action_text = '⏳ 即将实施'
                else:
                    action_text = '✅ 现行'
                if result.get('dual_match'):
                    action_text += ' ✓'
            else:
                action_text = '❓ 未查询到'

            # 工程标记 + 日期
            eng_badge = '🏗️' if is_eng else ''
            date_info = publish_date[:4] if publish_date else ''
            extra_info = f"{eng_badge} {date_info}".strip()

            # 类型+日期列
            type_date_str = f"{std_type or ''}".strip()

            item_id = self.check_tree.insert('', tk.END, text=str(i+1),
                                           values=(display_code, matched_name, status, type_date_str, f"{extra_info} {action_text}".strip()))

            # 行颜色
            if status in ('废止', '作废'):
                self.check_tree.item(item_id, tags=('obsolete',))
            elif status == '有更新版':
                self.check_tree.item(item_id, tags=('updated',))
            elif status == '即将实施':
                self.check_tree.item(item_id, tags=('pending',))
            elif result.get('found'):
                self.check_tree.item(item_id, tags=('active',))
            else:
                self.check_tree.item(item_id, tags=('notfound',))

            self.progress_var.set((i + 1) / total * 100)
            self.root.update_idletasks()

        # 配置标签颜色
        self.check_tree.tag_configure('obsolete', foreground='#C62828', font=(self._font_family, 10, 'bold'))
        self.check_tree.tag_configure('updated', foreground='#E65100')
        self.check_tree.tag_configure('pending', foreground='#1565C0')
        self.check_tree.tag_configure('active', foreground='#2E7D32')
        self.check_tree.tag_configure('notfound', foreground='#9CA3AF')

        self.progress_var.set(100)
        self.status_var.set(f"检查完成: {len(unique_codes)} 个规范")
        self.notebook.select(self.check_tree.master)

    def export_doc(self):
        if not self.check_results:
            messagebox.showwarning("提示", "没有检查结果可导出")
            return

        path = filedialog.asksaveasfilename(
            title="保存 DOC 报告",
            defaultextension=".docx",
            filetypes=[("Word documents", "*.docx"), ("All files", "*.*")]
        )
        if not path:
            return

        self.status_var.set("正在生成报告...")
        docx = _get_docx()
        doc = docx['Document']()

        title = doc.add_heading('标准规范检查报告', 0)
        title.alignment = docx['WD_ALIGN_PARAGRAPH'].CENTER

        doc.add_paragraph(f'生成时间: {datetime.now().strftime("%Y-%m-%d %H:%M:%S")}')
        doc.add_paragraph(f'文件: {os.path.basename(self.current_path) if self.current_path else "N/A"}')
        doc.add_paragraph()

        doc.add_heading('检查摘要', 1)
        total = len(self.check_results)
        found = sum(1 for _, r in self.check_results if r.get('found'))
        obsolete = sum(1 for _, r in self.check_results if r.get('status', '') in ('废止', '作废'))
        eng_cnt = sum(1 for _, r in self.check_results if r.get('is_eng'))
        updated = sum(1 for _, r in self.check_results if r.get('status', '') == '有更新版')
        doc.add_paragraph(f'共识别 {total} 个规范编号')
        doc.add_paragraph(f'数据库中查询到 {found} 个')
        doc.add_paragraph(f'其中废止/作废 {obsolete} 个')
        if updated:
            doc.add_paragraph(f'有更新版 {updated} 个（建议获取新版）')
        if eng_cnt:
            doc.add_paragraph(f'工程标准 {eng_cnt} 个')
        doc.add_paragraph()

        doc.add_heading('详细检查结果', 1)
        table = doc.add_table(rows=1, cols=7)
        table.style = 'Light Grid Accent 1'
        hdr_cells = table.rows[0].cells
        hdr_cells[0].text = '序号'
        hdr_cells[1].text = '规范编号'
        hdr_cells[2].text = '规范名称'
        hdr_cells[3].text = '状态'
        hdr_cells[4].text = '标准类型'
        hdr_cells[5].text = '发布日期'
        hdr_cells[6].text = '建议'

        for i, (code, result) in enumerate(self.check_results, 1):
            status = result.get('status', '未找到')
            std_type = result.get('std_type', '')
            publish_date = result.get('publish_date', '')
            is_eng = result.get('is_eng', False)
            matched_name = result.get('matched_name', result.get('matched_code', ''))
            if result.get('found'):
                if status in ('废止', '作废'):
                    action = '需替换'
                elif status == '有更新版':
                    action = '建议更新'
                elif status == '即将实施':
                    action = '即将实施'
                else:
                    action = '现行'
                if result.get('dual_match'):
                    action += '(✓)'
            else:
                action = '未查询到'

            type_display = f"{'🏗️' if is_eng else ''}{std_type}".strip()

            row_cells = table.add_row().cells
            row_cells[0].text = str(i)
            row_cells[1].text = code
            row_cells[2].text = matched_name
            row_cells[3].text = status
            row_cells[4].text = type_display
            row_cells[5].text = publish_date[:10] if publish_date else ''
            row_cells[6].text = action

        doc.save(path)
        self.progress_var.set(0)
        self.status_var.set(f"报告已保存: {path}")
        messagebox.showinfo("完成", f"报告已保存到:\n{path}")

    def on_check_item_selected(self, event=None):
        """当用户在"规范检查结果"列表中选中一项，跳转到对应位置并高亮。

        支持所有文件类型：PDF/DWG/DXF/docx/xlsx/pptx/txt
        """
        selected = self.check_tree.selection()
        if not selected:
            return
        item = selected[0]
        values = self.check_tree.item(item, 'values')
        if not values:
            return
        display_code = values[0]
        name = values[1] if len(values) > 1 else ''

        # Update preview name label
        original_code = display_code.split('[')[0].split('→')[0].strip()
        preview_name = f"{original_code} {name}".strip()
        if hasattr(self, '_preview_name_var'):
            self._preview_name_var.set(preview_name)

        # Extract original code from display (may contain " → " or " [相似:")
        code_norm = normalize_for_matching(original_code)

        # ── 图片型文件：切页 + 高亮 ──
        if self.pdf_images:
            target_loc = None
            for loc in self.code_locations:
                if normalize_for_matching(loc['code']) == code_norm:
                    target_loc = loc
                    break

            if target_loc:
                bbox = target_loc.get('bbox', (0, 0, 0, 0))
                if not all(v == 0 for v in bbox):
                    self._highlight_code_location(target_loc)
                else:
                    page_idx = target_loc.get('page', 0)
                    if page_idx != getattr(self, 'current_display_index', -1):
                        self.show_page(page_idx)
                        self.root.update_idletasks()
                    self._highlight_standard_on_preview(original_code, name)
            else:
                self._highlight_standard_on_preview(original_code, name)

        # ── 同步选中识别列表中的对应项 ──
        for item in self.list_tree.get_children():
            values = self.list_tree.item(item, 'values')
            if len(values) > 1 and normalize_for_matching(values[1]) == code_norm:
                self.list_tree.selection_set(item)
                self.list_tree.see(item)
                break

        # ── 文本型文件：高亮 ──
        self._highlight_code_in_text(original_code)
    
    def _highlight_code_location(self, loc):
        """在预览图上高亮指定规范的位置。

        1. 自动切到对应页面
        2. 根据 OCR bbox 绘制醒目高亮框（半透明填充 + 红色边框）
        3. 闪烁3次动画后保持显示，直到选中下一个规范
        4. 正确处理缩放和平移偏移
        """
        if not hasattr(self, '_current_base_image') or not self._current_base_image:
            return
        if not self.pdf_images:
            return

        # 1. 清除旧高亮（然后立刻恢复 loc，确保 show_page rebuild 时能找到）
        self.pdf_canvas.delete('code_highlight')
        self._highlight_rect_id = None
        self._highlight_fill_id = None
        self._highlight_label_id = None
        self._active_highlight_loc = loc  # 立刻设置，因为 show_page 依赖它

        # 2. 切换到对应页面
        page_idx = loc.get('page', 0)
        if page_idx != getattr(self, 'current_display_index', -1):
            self.show_page(page_idx)
            self.root.update_idletasks()

        # 3. 计算坐标变换（与 show_page/_apply_zoom 保持一致）
        canvas_w = self.pdf_canvas.winfo_width() or 400
        canvas_h = self.pdf_canvas.winfo_height() or 600
        img_w, img_h = self._current_base_image.size
        base_scale = min(canvas_w / img_w, canvas_h / img_h)
        zoom = getattr(self, '_zoom_level', 1.0)
        scale = base_scale * zoom
        offset_x = (canvas_w - int(img_w * scale)) // 2 + getattr(self, '_pan_image_x', 0)
        offset_y = (canvas_h - int(img_h * scale)) // 2 + getattr(self, '_pan_image_y', 0)

        x1, y1, x2, y2 = loc.get('bbox', (0, 0, 0, 0))
        if all(v == 0 for v in (x1, y1, x2, y2)):
            return

        # 从图像坐标变换到 canvas 坐标
        cx1 = x1 * scale + offset_x
        cy1 = y1 * scale + offset_y
        cx2 = x2 * scale + offset_x
        cy2 = y2 * scale + offset_y

        # 4. 绘制高亮框（半透明橙色填充 + 红色边框）
        pad = 4  # 外扩边距
        self._highlight_rect_id = self.pdf_canvas.create_rectangle(
            cx1 - pad, cy1 - pad, cx2 + pad, cy2 + pad,
            outline='#FF0000', width=3, dash=(),
            fill='', stipple='', tags='code_highlight'
        )
        # 内部填充半透明效果（用浅黄色模拟）
        self._highlight_fill_id = self.pdf_canvas.create_rectangle(
            cx1, cy1, cx2, cy2,
            outline='', fill='#FFFF00', stipple='gray25', tags='code_highlight'
        )

        # 标注文字
        code = loc.get('code', '')
        if code:
            self._highlight_label_id = self.pdf_canvas.create_text(
                cx1 - pad, cy1 - pad - 14, text=f"▶ {code}",
                fill='#CC0000', anchor='sw',
                font=(self._font_family, 10, 'bold'), tags='code_highlight'
            )
        else:
            self._highlight_label_id = None

        # 5. 脉冲闪烁动画（3次闪烁后保持）
        self._highlight_flash_count = 0
        self._highlight_flash()

    def _highlight_flash(self):
        """高亮框闪烁动画：3次闪烁后保持显示"""
        if self._highlight_rect_id is None:
            return
        self._highlight_flash_count += 1
        count = self._highlight_flash_count

        if count > 6:  # 3次完整闪烁(每闪算2次)
            # 停止闪烁，保持显示（红色边框）
            try:
                self.pdf_canvas.itemconfig(self._highlight_rect_id, outline='#FF0000', width=3)
                if hasattr(self, '_highlight_fill_id') and self._highlight_fill_id:
                    self.pdf_canvas.itemconfig(self._highlight_fill_id, fill='#FFFF00', stipple='gray25')
            except Exception:
                pass
            return

        # 交替显示/隐藏
        try:
            if count % 2 == 1:
                # 亮：红色边框 + 黄色填充
                self.pdf_canvas.itemconfig(self._highlight_rect_id, outline='#FF0000', width=3)
                if hasattr(self, '_highlight_fill_id') and self._highlight_fill_id:
                    self.pdf_canvas.itemconfig(self._highlight_fill_id, fill='#FFFF00', stipple='gray25')
            else:
                # 暗：橙色边框 + 无填充
                self.pdf_canvas.itemconfig(self._highlight_rect_id, outline='#FF6600', width=2)
                if hasattr(self, '_highlight_fill_id') and self._highlight_fill_id:
                    self.pdf_canvas.itemconfig(self._highlight_fill_id, fill='', stipple='')
        except Exception:
            return

        self.root.after(200, self._highlight_flash)

    def _clear_highlight(self):
        """清除所有高亮标记"""
        self.pdf_canvas.delete('code_highlight')
        self._highlight_rect_id = None
        self._highlight_fill_id = None
        self._highlight_label_id = None
        self._active_highlight_loc = None

    def _rebuild_highlight(self, scale=None):
        """在缩放/重绘后重建高亮框。

        _apply_zoom 和 show_page 会调用 pdf_canvas.delete('all')，
        本方法根据 _active_highlight_loc 重新绘制。
        """
        loc = getattr(self, '_active_highlight_loc', None)
        if not loc:
            return
        if not hasattr(self, '_current_base_image') or not self._current_base_image:
            return

        # 仅当高亮所属页面是当前显示页面时才绘制
        if loc.get('page', 0) != getattr(self, 'current_display_index', -1):
            return

        canvas_w = self.pdf_canvas.winfo_width() or 400
        canvas_h = self.pdf_canvas.winfo_height() or 600
        img_w, img_h = self._current_base_image.size
        base_scale = min(canvas_w / img_w, canvas_h / img_h)
        zoom = getattr(self, '_zoom_level', 1.0)
        s = base_scale * zoom
        offset_x = (canvas_w - int(img_w * s)) // 2 + getattr(self, '_pan_image_x', 0)
        offset_y = (canvas_h - int(img_h * s)) // 2 + getattr(self, '_pan_image_y', 0)

        bbox = loc.get('bbox', (0, 0, 0, 0))
        if all(v == 0 for v in bbox):
            return

        x1 = bbox[0] * s + offset_x
        y1 = bbox[1] * s + offset_y
        x2 = bbox[2] * s + offset_x
        y2 = bbox[3] * s + offset_y
        pad = 4
        self._highlight_rect_id = self.pdf_canvas.create_rectangle(
            x1 - pad, y1 - pad, x2 + pad, y2 + pad,
            outline='#FF0000', width=3, tags='code_highlight'
        )
        self._highlight_fill_id = self.pdf_canvas.create_rectangle(
            x1, y1, x2, y2,
            outline='', fill='#FFFF00', stipple='gray25', tags='code_highlight'
        )
        code = loc.get('code', '')
        if code:
            self._highlight_label_id = self.pdf_canvas.create_text(
                x1 - pad, y1 - pad - 14, text=f"▶ {code}",
                fill='#CC0000', anchor='sw',
                font=(self._font_family, 10, 'bold'), tags='code_highlight'
            )
    
    def _highlight_standard_on_preview(self, code, name):
        """当 OCR 没有 bbox 时，用 fitz 文本搜索定位高亮（PDF 专用）。"""
        if self.file_type != 'pdf' or not getattr(self, 'current_path', None):
            return
        if not hasattr(self, '_current_base_image') or not self._current_base_image:
            return
        try:
            doc = None
            try:
                doc = _get_fitz().open(self.current_path)
                page_idx = getattr(self, 'current_display_index', 0)
                if page_idx < 0 or page_idx >= len(doc):
                    return
                page = doc.load_page(page_idx)

                rect = None
                for search_text in (code, name):
                    if not search_text:
                        continue
                    blocks = page.search_for(search_text)
                    if blocks:
                        rect = blocks[0]
                        break

                if not rect:
                    return

                canvas_w = self.pdf_canvas.winfo_width() or 400
                canvas_h = self.pdf_canvas.winfo_height() or 600
                img_w, img_h = self._current_base_image.size
                dpi = 200
                scale_factor = dpi / 72.0
                base_scale = min(canvas_w / img_w, canvas_h / img_h)
                zoom = getattr(self, '_zoom_level', 1.0)
                scale = base_scale * zoom
                offset_x = (canvas_w - int(img_w * scale)) // 2 + getattr(self, '_pan_image_x', 0)
                offset_y = (canvas_h - int(img_h * scale)) // 2 + getattr(self, '_pan_image_y', 0)

                x1 = rect.x0 * scale_factor * scale + offset_x
                y1 = rect.y0 * scale_factor * scale + offset_y
                x2 = rect.x1 * scale_factor * scale + offset_x
                y2 = rect.y1 * scale_factor * scale + offset_y

                # 清除旧高亮
                self.pdf_canvas.delete('code_highlight')

                # 保存为 code_locations 格式，供缩放重建使用
                orig_x1 = rect.x0 * scale_factor
                orig_y1 = rect.y0 * scale_factor
                orig_x2 = rect.x1 * scale_factor
                orig_y2 = rect.y1 * scale_factor
                self._active_highlight_loc = {
                    'code': code,
                    'page': page_idx,
                    'bbox': (orig_x1, orig_y1, orig_x2, orig_y2),
                }

                # 绘制统一的高亮框
                pad = 4
                self._highlight_rect_id = self.pdf_canvas.create_rectangle(
                    x1 - pad, y1 - pad, x2 + pad, y2 + pad,
                    outline='#FF0000', width=3, tags='code_highlight'
                )
                self._highlight_fill_id = self.pdf_canvas.create_rectangle(
                    x1, y1, x2, y2,
                    outline='', fill='#FFFF00', stipple='gray25', tags='code_highlight'
                )
                if code:
                    self._highlight_label_id = self.pdf_canvas.create_text(
                        x1 - pad, y1 - pad - 14, text=f"▶ {code}",
                        fill='#CC0000', anchor='sw',
                        font=(self._font_family, 10, 'bold'), tags='code_highlight'
                    )

                # 脉冲动画
                self._highlight_flash_count = 0
                self._highlight_flash()
            finally:
                if doc is not None:
                    doc.close()
        except Exception as e:
            print(f"highlight error: {e}")
    
    def on_check_item_double_click(self, event=None):
        """Double-click on check result item — always show search dialog.

        双击规范检查结果列表中的任何项，都弹出搜索对话框，
        方便手动查找规范或替换为正确规范。
        """
        selected = self.check_tree.selection()
        if not selected:
            return
        item = selected[0]
        values = self.check_tree.item(item, 'values')
        if not values:
            return

        display_code = values[0]
        original_code = display_code.split('[')[0].split('→')[0].strip()

        # Get associated name if available
        name = ''
        if hasattr(self, 'extracted_code_info'):
            info = self.extracted_code_info.get(normalize_for_matching(original_code), {})
            name = info.get('name', '')
        # 也从检查结果列表的第2列取名称
        if not name and len(values) > 1:
            name = values[1]

        dialog = StandardSearchDialog(self, self.checker, code=original_code, name=name)
        self.wait_window(dialog)

    def _show_about(self):
        """显示关于对话框"""
        about = tk.Toplevel(self.root)
        about.title("关于 规范标准助手")
        about.geometry("480x320")
        about.resizable(False, False)
        about.transient(self.root)
        about.grab_set()

        # 居中
        about.update_idletasks()
        x = self.root.winfo_x() + (self.root.winfo_width() - 480) // 2
        y = self.root.winfo_y() + (self.root.winfo_height() - 320) // 2
        about.geometry(f"+{x}+{y}")

        card = tk.Frame(about, bg="#FFFFFF", padx=20, pady=20)
        card.pack(fill=tk.BOTH, expand=True)

        # Logo
        if hasattr(self, '_logo_photo'):
            logo_lbl = tk.Label(card, image=self._logo_photo, bg="#FFFFFF")
            logo_lbl.pack(pady=(0, 8))

        tk.Label(card, text="规范标准助手", font=(self._font_family, 18, "bold"),
                 fg="#1E3A5F", bg="#FFFFFF").pack()
        tk.Label(card, text=f"版本 {VERSION_APP}", font=(self._font_family, 11),
                 fg="#2B6CB0", bg="#FFFFFF").pack(pady=(2, 8))

        info_text = (
            "工程标准规范智能检查工具\n\n"
            "📄 OCR识别 → 规范提取 → 状态检查 → 报告导出\n\n"
            "支持 30万+ 条标准规范数据\n"
            "支持 GB/GB/T/JGJ/CJJ/CECS/DB 等全系列标准\n"
            "支持工程标准（🏗️）特殊标记"
        )
        tk.Label(card, text=info_text, font=(self._font_family, 9),
                 fg="#374151", bg="#FFFFFF", justify=tk.LEFT).pack(pady=8)

        tk.Label(card, text="© 2024-2025 LDAssistant Team",
                 font=(self._font_family, 8), fg="#9CA3AF", bg="#FFFFFF").pack(side=tk.BOTTOM)

        tk.Button(card, text="关闭", command=about.destroy, cursor="hand2",
                 font=(self._font_family, 9), bg="#2B6CB0", fg="#FFFFFF",
                 relief="flat", padx=20, pady=4).pack(side=tk.BOTTOM, pady=(10, 0))

    def run(self):
        self.root.mainloop()


class StandardSearchDialog(tk.Toplevel):
    """规范搜索与推荐弹窗（增强版：显示类型/日期/工程标记/状态过滤）"""
    def __init__(self, parent, checker, code='', name=''):
        super().__init__(parent)
        self.checker = checker
        self.code = code
        self.name = name
        self.title("规范搜索与推荐")
        self.geometry("850x620")
        self.minsize(700, 500)
        self.transient(parent)
        self.grab_set()

        self._setup_ui()
        self._search_recommend()

    def _setup_ui(self):
        parent_root = self.master
        # 尝试从父窗口获取字体变量
        font_family = "Microsoft YaHei"
        try:
            if parent_root and hasattr(parent_root, '_font_family'):
                font_family = parent_root._font_family
        except Exception:
            pass

        # 配色
        primary = "#1E3A5F"
        primary_light = "#2B6CB0"
        bg = "#F5F6FA"
        card = "#FFFFFF"

        # Top search area — 卡片风格
        search_card = tk.Frame(self, bg=card, highlightbackground="#E5E7EB", highlightthickness=1)
        search_card.pack(side=tk.TOP, fill=tk.X, padx=10, pady=(10, 0))

        search_frame = tk.Frame(search_card, bg=card, padx=10, pady=10)
        search_frame.pack(side=tk.TOP, fill=tk.X)

        tk.Label(search_frame, text="🔍 搜索规范:", font=(font_family, 10, "bold"),
                 fg=primary, bg=card).pack(side=tk.LEFT, padx=(0, 6))
        self.search_var = tk.StringVar()
        search_entry = tk.Entry(search_frame, textvariable=self.search_var, width=50,
                               font=(font_family, 10), relief="solid", borderwidth=1,
                               highlightbackground="#D1D5DB", highlightcolor=primary_light,
                               highlightthickness=1)
        search_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 6))
        search_entry.bind('<Return>', lambda e: self._do_search())

        for txt, cmd in [("🔎 搜索", self._do_search), ("📋 推荐相近", self._search_recommend)]:
            tk.Button(search_frame, text=txt, command=cmd, cursor="hand2",
                     font=(font_family, 9), bg=primary_light, fg=card,
                     relief="flat", padx=10, pady=4).pack(side=tk.LEFT, padx=(0, 4))

        # 过滤行
        filter_frame = tk.Frame(search_card, bg=card, padx=10, pady=0)
        filter_frame.pack(side=tk.TOP, fill=tk.X, pady=(0, 10))

        # 规范类型过滤
        tk.Label(filter_frame, text="类型:", font=(font_family, 9), fg="#374151", bg=card).pack(side=tk.LEFT, padx=(0, 3))
        self.type_var = tk.StringVar(value='全部')
        type_combo = ttk.Combobox(filter_frame, textvariable=self.type_var, width=10, state='readonly')
        type_combo['values'] = ('全部', 'GB', 'GB/T', 'JGJ', 'JGJ/T', 'CJJ', 'CJJ/T',
                                'CECS', 'T/CECS', 'JG', 'JG/T', 'DB', 'DB/T',
                                'DG/TJ', 'DBJ', '其他')
        type_combo.pack(side=tk.LEFT, padx=(0, 12))
        type_combo.bind('<<ComboboxSelected>>', lambda e: self._do_search())

        tk.Label(filter_frame, text="状态:", font=(font_family, 9), fg="#374151", bg=card).pack(side=tk.LEFT, padx=(0, 3))
        self.status_var = tk.StringVar(value='全部')
        status_combo = ttk.Combobox(filter_frame, textvariable=self.status_var, width=10, state='readonly')
        status_combo['values'] = ('全部', '现行', '废止', '作废', '有更新版', '即将实施')
        status_combo.pack(side=tk.LEFT, padx=(0, 12))
        status_combo.bind('<<ComboboxSelected>>', lambda e: self._do_search())

        self.eng_only_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(filter_frame, text='🏗️ 仅工程标准', variable=self.eng_only_var,
                        command=self._do_search).pack(side=tk.LEFT, padx=(0, 8))

        tk.Label(filter_frame, text="结果:", font=(font_family, 9), fg="#374151", bg=card).pack(side=tk.LEFT, padx=(12, 3))
        self.result_count_var = tk.StringVar(value='0 条')
        tk.Label(filter_frame, textvariable=self.result_count_var, font=(font_family, 9),
                 fg="#6B7280", bg=card).pack(side=tk.LEFT)

        # 当前规范提示
        info_card = tk.Frame(self, bg=card, highlightbackground="#E5E7EB", highlightthickness=1)
        info_card.pack(side=tk.TOP, fill=tk.X, padx=10, pady=(6, 0))
        if self.code:
            info_text = f"📌 当前规范: {self.code}" + (f"  《{self.name}》" if self.name else "")
        else:
            info_text = "💡 请输入编号或名称搜索规范"
        tk.Label(info_card, text=info_text, font=(font_family, 9), fg="#6B7280",
                 bg=card, padx=10, pady=6).pack(side=tk.LEFT)

        # 结果区域
        results_card = tk.Frame(self, bg=card, highlightbackground="#E5E7EB", highlightthickness=1)
        results_card.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=10, pady=(6, 10))

        results_frame = tk.Frame(results_card, bg=card, padx=6, pady=6)
        results_frame.pack(side=tk.TOP, fill=tk.BOTH, expand=True)

        columns = ('code', 'name', 'status', 'std_type', 'publish_date', 'eng_badge')
        # 使用 ttk.Treeview 但先设置样式
        style = ttk.Style()
        style.configure("Search.Treeview",
                        rowheight=28,
                        font=(font_family, 9),
                        foreground="#1A1A2E",
                        background=card,
                        fieldbackground=card,
                        borderwidth=0)
        style.map("Search.Treeview",
                  background=[('selected', '#BFDBFE')],
                  foreground=[('selected', '#1A1A2E')])
        style.configure("Search.Treeview.Heading",
                        font=(font_family, 9, "bold"),
                        foreground=card,
                        background=primary,
                        relief="flat",
                        borderwidth=0,
                        padding=(4, 4))
        style.map("Search.Treeview.Heading",
                  background=[('active', primary_light)])

        self.result_tree = ttk.Treeview(results_frame, columns=columns, show='headings',
                                        height=15, style="Search.Treeview")
        self.result_tree.heading('code', text='规范编号')
        self.result_tree.heading('name', text='规范名称')
        self.result_tree.heading('status', text='状态')
        self.result_tree.heading('std_type', text='类型')
        self.result_tree.heading('publish_date', text='发布日期')
        self.result_tree.heading('eng_badge', text='工程')
        self.result_tree.column('code', width=150)
        self.result_tree.column('name', width=320)
        self.result_tree.column('status', width=80)
        self.result_tree.column('std_type', width=70, anchor=tk.CENTER)
        self.result_tree.column('publish_date', width=100, anchor=tk.CENTER)
        self.result_tree.column('eng_badge', width=50, anchor=tk.CENTER)
        self.result_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        scroll = ttk.Scrollbar(results_frame, orient=tk.VERTICAL, command=self.result_tree.yview)
        self.result_tree.configure(yscrollcommand=scroll.set)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)

        # 底部按钮
        btn_frame = tk.Frame(self, bg="#F5F6FA", padx=10, pady=10)
        btn_frame.pack(side=tk.BOTTOM, fill=tk.X)
        def _copy_selected():
            sel = self.result_tree.selection()
            if not sel:
                messagebox.showwarning("提示", "请先选择一条记录", parent=self)
                return
            values = self.result_tree.item(sel[0], 'values')
            self.clipboard_clear()
            self.clipboard_append(f"{values[0]} {values[1]}")
            messagebox.showinfo("完成", "已复制规范信息", parent=self)

        tk.Button(btn_frame, text="📋 复制选中", command=_copy_selected, cursor="hand2",
                 font=(font_family, 9), bg="#2B6CB0", fg=card, relief="flat",
                 padx=12, pady=4).pack(side=tk.LEFT, padx=(0, 6))
        tk.Button(btn_frame, text="❌ 关闭", command=self.destroy, cursor="hand2",
                 font=(font_family, 9), bg="#FFFFFF", fg="#374151", relief="solid",
                 borderwidth=1, padx=12, pady=4).pack(side=tk.RIGHT)

        # 绑定双击事件
        self.result_tree.bind('<Double-Button-1>', self._on_result_double_click)

    def _do_search(self):
        query = self.search_var.get().strip()
        if not query:
            messagebox.showwarning("提示", "请输入搜索内容", parent=self)
            return
        self._perform_search(query)

    def _search_recommend(self):
        if self.code:
            self._perform_search(self.code)

    def _perform_search(self, query):
        self.result_tree.delete(*self.result_tree.get_children())
        norm_query = normalize_for_matching(query)

        # 获取过滤条件
        filter_type = self.type_var.get().strip()
        filter_status = self.status_var.get().strip()
        eng_only = self.eng_only_var.get()

        results = []

        # 判断是否使用 SQLite 后端
        sqlite_checker = getattr(self.checker, '_sqlite_checker', None)
        if sqlite_checker is not None:
            # SQLite 模式：通过 search_by_keyword + 过滤
            try:
                raw_rows = sqlite_checker.search_by_keyword(query, limit=200)
                for r in raw_rows:
                    code = r.get('code', '')
                    name = r.get('name', '')
                    status = r.get('status', '')
                    std_type = r.get('std_type', '')
                    is_eng = r.get('is_eng', 0)
                    publish_date = r.get('publish_date', '')

                    # 类型过滤
                    if filter_type != '全部':
                        if filter_type == '其他':
                            known_types = ('GB', 'GB/T', 'JGJ', 'JGJ/T', 'CJJ', 'CJJ/T',
                                           'CECS', 'T/CECS', 'JG', 'JG/T', 'DB', 'DB/T',
                                           'DG/TJ', 'DBJ')
                            if std_type in known_types:
                                continue
                        elif std_type != filter_type:
                            continue
                    # 状态过滤
                    if filter_status != '全部':
                        if filter_status not in status:
                            continue
                    # 工程标准过滤
                    if eng_only and not is_eng:
                        continue

                    norm_code = normalize_for_matching(code)
                    norm_name = normalize_for_matching(name)
                    score = 0
                    if norm_query in norm_code or norm_code in norm_query:
                        score = max(score, len(norm_query) / max(len(norm_code), 1))
                    if norm_query in norm_name or norm_name in norm_query:
                        score = max(score, len(norm_query) / max(len(norm_name), 1))
                    if score > 0:
                        results.append((score, r))
            except Exception:
                results = []
        else:
            # JSON 模式：原有逻辑
            for k, v in self.checker.code_index.items():
                code = v.get('code', '')
                name = v.get('name', '')
                status = v.get('status', '')
                std_type = v.get('std_type', '')
                is_eng = v.get('is_eng', 0)
                publish_date = v.get('publish_date', '')

                # 类型过滤
                if filter_type != '全部':
                    if filter_type == '其他':
                        known_types = ('GB', 'GB/T', 'JGJ', 'JGJ/T', 'CJJ', 'CJJ/T',
                                       'CECS', 'T/CECS', 'JG', 'JG/T', 'DB', 'DB/T',
                                       'DG/TJ', 'DBJ')
                        if std_type in known_types:
                            continue
                    elif std_type != filter_type:
                        continue

                # 状态过滤
                if filter_status != '全部':
                    if filter_status not in status:
                        continue

                # 工程标准过滤
                if eng_only and not is_eng:
                    continue

                norm_code = normalize_for_matching(code)
                norm_name = normalize_for_matching(name)

                score = 0
                if norm_query in norm_code or norm_code in norm_query:
                    score = max(score, len(norm_query) / max(len(norm_code), 1))
                if norm_query in norm_name or norm_name in norm_query:
                    score = max(score, len(norm_query) / max(len(norm_name), 1))
                if score > 0:
                    results.append((score, v))

        results.sort(key=lambda x: x[0], reverse=True)
        results = results[:80]

        for _, v in results:
            code = v.get('code', '')
            name = v.get('name', '')
            status = v.get('status', '')
            std_type = v.get('std_type', '')
            publish_date = v.get('publish_date', '')
            is_eng = v.get('is_eng', 0)

            eng_text = '🏗️' if is_eng else ''
            pub_short = publish_date[:10] if publish_date else ''

            item_id = self.result_tree.insert('', tk.END,
                values=(code, name, status, std_type, pub_short, eng_text))

            # 行颜色
            if status in ('废止', '作废'):
                self.result_tree.item(item_id, tags=('obsolete',))
            elif status == '有更新版':
                self.result_tree.item(item_id, tags=('updated',))
            elif status == '即将实施':
                self.result_tree.item(item_id, tags=('pending',))
            else:
                self.result_tree.item(item_id, tags=('active',))

        self.result_tree.tag_configure('obsolete', foreground='#C62828')
        self.result_tree.tag_configure('updated', foreground='#E65100')
        self.result_tree.tag_configure('pending', foreground='#1565C0')
        self.result_tree.tag_configure('active', foreground='#2E7D32')

        count = len(results)
        self.result_count_var.set(f'{count} 条')
        if count == 0:
            # 在状态栏也提示一下
            pass

    def _on_result_double_click(self, event):
        item = self.result_tree.selection()
        if not item:
            return
        values = self.result_tree.item(item[0], 'values')
        code, name, status, std_type, publish_date, eng_text = values[0], values[1], values[2], values[3], values[4], values[5]

        # 复制信息弹窗（增强版）
        copy_win = tk.Toplevel(self)
        copy_win.title("规范详细信息")
        copy_win.geometry("520x260")
        copy_win.transient(self)
        copy_win.grab_set()

        # 尝试获取字体
        ff = "Microsoft YaHei"
        try:
            if hasattr(self, '_font_family'):
                ff = self._font_family
        except Exception:
            pass

        card = tk.Frame(copy_win, bg="#FFFFFF", padx=15, pady=12)
        card.pack(fill=tk.BOTH, expand=True)

        info_text = f"规范编号: {code}"
        tk.Label(card, text=info_text, font=(ff, 11, "bold"),
                 fg="#1E3A5F", bg="#FFFFFF").pack(anchor=tk.W, pady=(0, 4))

        tk.Label(card, text=f"规范名称: {name}", font=(ff, 11),
                 fg="#374151", bg="#FFFFFF").pack(anchor=tk.W, pady=2)

        status_color = '#C62828' if status in ('废止', '作废') else ('#E65100' if status == '有更新版' else '#2E7D32')
        tk.Label(card, text=f"状态: {status}", font=(ff, 10, "bold"),
                 fg=status_color, bg="#FFFFFF").pack(anchor=tk.W, pady=2)

        extra = f"标准类型: {std_type}"
        if publish_date:
            extra += f"  |  发布日期: {publish_date}"
        if eng_text:
            extra += "  |  🏗️ 工程标准"
        tk.Label(card, text=extra, font=(ff, 9),
                 fg="#6B7280", bg="#FFFFFF").pack(anchor=tk.W, pady=2)

        tk.Label(card, text="💡 提示：双击可复制整条规范信息", font=(ff, 8),
                 fg="#9CA3AF", bg="#FFFFFF").pack(anchor=tk.W, pady=(10, 0))

        btn_bar = tk.Frame(card, bg="#FFFFFF")
        btn_bar.pack(side=tk.BOTTOM, fill=tk.X, pady=(10, 0))

        # 剪贴板复制函数
        def copy_code():
            self.clipboard_clear()
            self.clipboard_append(code)
            copy_win.destroy()

        def copy_name():
            self.clipboard_clear()
            self.clipboard_append(name)
            copy_win.destroy()

        def copy_all():
            self.clipboard_clear()
            self.clipboard_append(f"{code} {name} 状态:{status}")
            copy_win.destroy()

        for txt, cmd in [("📋 复制编号", copy_code), ("📝 复制名称", copy_name), ("📄 复制全部", copy_all)]:
            tk.Button(btn_bar, text=txt, command=cmd, cursor="hand2",
                     font=(ff, 9), bg="#2B6CB0", fg="#FFFFFF",
                     relief="flat", padx=10, pady=3).pack(side=tk.LEFT, padx=(0, 5))

        tk.Button(btn_bar, text="关闭", command=copy_win.destroy, cursor="hand2",
                 font=(ff, 9), bg="#FFFFFF", fg="#374151",
                 relief="solid", borderwidth=1, padx=12, pady=3).pack(side=tk.RIGHT)


def main():
    print("Starting 工程助手 LDAssistant...")
    app = App()
    app.run()
    print("Application exited.")


if __name__ == "__main__":
    main()
