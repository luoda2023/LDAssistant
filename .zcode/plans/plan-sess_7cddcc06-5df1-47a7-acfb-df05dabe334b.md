## 诊断：文件打不开的根本原因

经过逐行审计，发现以下问题：

### 1. 所有文件显示/加载方法缺少 try/except
| 方法 | 未保护的行 | 风险 |
|------|-----------|------|
| `convert_pdf_to_images()` | `fitz.open(path)`, `pix.save()`, `show_page()` | PDF 损坏 → 崩溃 |
| `show_page()` | `Image.open(img_path)`, `img.resize()`, `ImageTk.PhotoImage()` | 图片格式问题 → 崩溃 |
| `_load_image_file()` | 无 try，直接 append + show_page | 路径错误 → 崩溃 |
| `_load_cad_file()` | `render_cad_to_image()`, `show_page()` | 渲染失败 → 崩溃 |
| `extract_text_file()`（docx分支） | `Document(path)`, `ImageFont.truetype()`, `ImageDraw.text()` | 字体缺失 → 崩溃 |

### 2. 批量处理中 `self.checker` 为 None
异步加载后 `self.checker = None`，`batch_process_all()` 中的 `self.checker.ocr_image()` 直接崩溃。

### 修复方案

给每个文件加载/显示方法加 try/except：
- 成功 → 正常显示
- 失败 → `messagebox.showerror()` 显示具体错误信息 + 状态栏提示
- 批量处理前检查 `self.checker is not None`

这样用户就能看到**具体是什么错误**（缺DLL、文件损坏、内存不足等），而不是静默崩溃。