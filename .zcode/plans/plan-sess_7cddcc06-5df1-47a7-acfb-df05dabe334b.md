## 方案：PDF/Word/OFD 用系统默认程序直接打开（和 DWG 一样）

### 核心思路
当前 PDF/Word/OFD 打开时会先把所有页面转成临时 PNG 再显示在画布上——太慢。
改为：像 DWG 一样，用 `os.startfile()` 直接调用系统默认程序打开原始文件。

### 改动详情

#### 1. `_load_file` 方法（L2448-2457）
- PDF → 调用 `os.startfile(path)` 直接用系统默认 PDF 阅读器打开
- Word(.docx) → 调用 `os.startfile(path)` 用 Word/WPS 打开
- OFD → 调用 `os.startfile(path)` 用系统默认 OFD 阅读器打开
- 图片 → 保持不变，直接在程序内 Canvas 显示
- CAD → 保持不变（DWG 用 AcmeCAD 嵌入，DXF 用 ezdxf 渲染）

#### 2. 新增 `_open_with_system` 方法
```python
def _open_with_system(self):
    """用系统默认程序直接打开原始文件"""
    if not self.current_path:
        return
    try:
        os.startfile(self.current_path)
        self.status_var.set(f"已用系统程序打开: {Path(self.current_path).name}")
    except Exception as e:
        messagebox.showerror("打开失败", f"无法打开文件:\n{self.current_path}\n\n错误: {e}")
```

#### 3. 清理不再需要的旧方法
- `convert_pdf_to_images()` → 删除（不再预渲染所有页面）
- `_load_docx_file()` → 简化为调用 `_open_with_system`
- `_load_ofd_file()` → 简化为调用 `_open_with_system`
- 保留 `_extract_text_from_docx()` 供批量处理 OCR 使用

#### 4. OCR 适配
- `start_ocr()` / `_ocr_current_file()`：OCR 仍需要图片，保留按需渲染逻辑
  - PDF: OCR 时临时用 fitz 渲染当前页为图片（不预渲染所有页）
  - Word: OCR 时用文本提取（已有 `_extract_text_from_docx`）
- 批量处理 `batch_process_all()`：保持现有逻辑（已有按需渲染）

#### 5. 翻页/缩放/旋转按钮适配
- PDF/Word/OFD 在系统程序中打开后，程序内的翻页/缩放/旋转按钮对这类文件禁用或隐藏
- 图片和 DXF 保持原有 Canvas 内缩放/旋转功能

### 不变的部分
- 图片：直接在 Canvas 打开（已有逻辑不变）
- DWG：AcmeCAD 嵌入（不变）
- DXF：ezdxf 渲染为图片显示（不变）
- 批量 OCR 识别：保持现有逻辑
- 规范编号识别：保持现有逻辑