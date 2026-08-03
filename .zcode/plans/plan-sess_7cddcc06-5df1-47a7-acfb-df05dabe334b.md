## 计划：PDF/Word/图片清晰渲染 + 文件全部能打开 + 消除虚边

### 核心问题分析

1. **PDF 虚边**：渲染 DPI 固定 200，未根据显示器 DPI 缩放。高 DPI 屏幕上文字模糊。缩放时只是对 200DPI 位图做 LANCZOS 上采样，越放大越虚。
2. **Word 只提取纯文本**：`.docx` 走 `extract_text_file()`，只提取 `doc.paragraphs` 纯文本，表格/图片/页眉全丢，用简陋 PNG 画文本，50 字截断、1600px 高度上限。用户看到的不是 Word 原始排版。
3. **`_redraw_current_page` 的 `if not HAS_FITZ: return`**：非 PDF 文件（图片/CAD/Word）在缩放/平移/窗口调整时完全不工作。
4. **`.ofd` 检测为受支持但无加载分支**：检测返回 `'ofd'`，但 `extract_text_file` 没有 ofd 分支 → "不支持的文件格式"。
5. **DXF 渲染 DPI 150 偏低**：CAD 图纸线条和标注模糊。

### 修复方案

#### 修复1：动态高 DPI PDF 渲染（消除虚边）

**文件**：`standard_checker_v2.py`

- 新增 `_get_render_dpi()` 方法：读取 Windows 缩放比例（`ctypes.windll.shcore.GetScaleFactorForDevice(0)`），返回 `max(300, 200 * dpi_scale / 100)`。即 100% 缩放→300DPI，150% 缩放→300DPI（上限 400），确保文字锐利。
- `convert_pdf_to_images()`（L4208）：`pix = page.get_pixmap(dpi=200)` → `pix = page.get_pixmap(dpi=self._render_dpi)`
- 批量 OCR 路径（L2940）：同样替换为 `self._render_dpi`
- `_highlight_standard_on_preview`（L3958 附近硬编码 `dpi=200`）：替换为 `self._render_dpi`

#### 修复2：Word 文档完整渲染（docx → PDF → pixmap）

**文件**：`standard_checker_v2.py`

- `_load_file()`（L2437）：添加 `elif self.file_type == 'docx':` 分支，调用新方法 `_load_docx_file()`
- 新增 `_load_docx_file()` 方法：
  1. 尝试用 `win32com.client`（Word COM）将 docx 转为临时 PDF，然后用 `fitz` 渲染为高 DPI 图片（与 PDF 路径一致）
  2. 若 Word COM 不可用或失败，尝试用 `docx2pdf` 库（如已安装）
  3. 都不可用时回退到当前文本提取方式（`extract_text_file`），但改进：也提取表格内容（`doc.tables`）
- 这样 Word 文档的所有排版、表格、图片都会忠实显示，文字清晰

#### 修复3：`_redraw_current_page` 去掉错误 guard

**文件**：`standard_checker_v2.py` L3134-3136

- 删除 `if not HAS_FITZ: return`
- 改为 `if not hasattr(self, '_current_base_image') or not self._current_base_image: return`
- 效果：图片/CAD/Word 文件也能正常缩放、平移、窗口调整

#### 修复4：`.ofd` 文件支持

**文件**：`standard_checker_v2.py`

- `_load_file()`：添加 `elif self.file_type == 'ofd':` 分支
- OFD 格式本质是 ZIP 包含 XML，可以用 `fitz` 直接打开（MuPDF 1.29 支持 OFD），若失败则提示需要安装相关支持
- 尝试 `fitz.open(path)` → 如果成功则走 PDF 渲染路径

#### 修复5：DXF 渲染 DPI 提升

**文件**：`standard_checker_v2.py` L593

- `render_cad_to_image(dxf_path, dpi=150)` → `render_cad_to_image(dxf_path, dpi=300)`
- 调用处（L2484）无需改动，使用默认值

#### 修复6：`fit_width` 模式垂直溢出处理

**文件**：`standard_checker_v2.py` L3048-3049

- `fit_width` 模式下，当缩放后图片高度超出画布时，改用 `min(canvas_w/img_w, canvas_h/img_h)` 约束，避免内容超出可视区域

### 涉及文件
仅 `standard_checker_v2.py`

### 验证步骤
1. `py_compile` 语法检查
2. AST 验证新方法存在且被正确调用
3. 确认 `_redraw_current_page` 不再依赖 `HAS_FITZ`
4. 提交推送触发 CI 构建