## 全面修复 LDAssistant — 图标、DWG、PDF/Word 显示、AI导出

### 问题1：EXE 图标没改过来
**根因**：最新 commit 0422d7f 添加了 PyInstaller `--icon` 参数并生成了多尺寸 ICO，但 EXE 图标依然没显示。原因有两条：
- (a) `_set_app_icon()` 方法（第1321行）在 **PyInstaller onedir 模式下**把 ICO 放进 `dist/LDAssistant/` 目录，但 `tk.Toplevel` 子窗口（结果面板、缩略图、AI聊天）没用上这个图标；
- (b) 关键：PyInstaller onedir 把 `app_icon.ico` 放在 **dist 根目录**，而 `--windowed` 的 EXE 需要 ICO 在打包时绑定。当前 build.yml 第63行 `--icon "app_icon.ico"` 是对的，但 `app_icon.ico` 是 **129KB 的多尺寸 ICO，包含 PNG 嵌入数据**，Windows 资源管理器有时取不到第一帧。需要生成一个标准 256x256 的纯 ICO。

**修复**：
1. 用 PNG 重新生成标准 256x256 ICO（PyInstaller 取第一帧做 EXE 图标）
2. 修复 `_set_app_icon`：不仅给主窗口，也给所有 `Toplevel`（AI聊天、结果面板、缩略图）设置图标
3. build.yml 确认 `--icon` 参数正确

### 问题2：DWG 打不开
**根因**：`ezdxf` **只能读 DXF，完全不能读 DWG**（第576行 `ezdxf.readfile()` 对 DWG 直接抛异常，被静默吞掉）。代码在 `CAD_EXTENSIONS`（第130行）和 filedialog（第1617/1622行）里假装支持 DWG。

**开源直读 DWG 的实情**（我去查了所有主流库）：
- `ezdxf`：只能 DXF，不能 DWG
- `ocdpy`：依赖 OpenCASCADE C++ 二进制，需要系统预装 OCC，**pip 装不了，纯 Python 行不通**
- `libredwg` Python 绑定：依赖 LibreDWG C 库，需自行编译，**Windows 没有预编译 wheel**
- `dwg2dxf`/`teigha`：都依赖外部二进制

**结论：不存在"pip install 就能直接打开 DWG"的纯开源 Python 库。** DWG 是 Autodesk 闭源私有格式。

**最务实的修复方案**：
1. 在 `_load_cad_file` 里判断：如果是 DWG，先调用 `ezdxf` 无法处理 → 弹窗友好提示用户"DWG 需先用 CAD 软件另存为 DXF 格式"，并给出具体操作步骤
2. 同时在打开文件对话框把 DWG 从"CAD文件"类型中移除，避免用户误选；但保留在"所有支持"里并做检测+提示，体验更好
3. 这样"打开DWG"不再是黑屏报错，而是清晰的引导

### 问题3：PDF / Word 打开后窗口空白
- **PDF**（`convert_pdf_to_images` 第2936行）：依赖 PyMuPDF（`fitz`），已验证 PyMuPDF 1.28.0 已安装 ✓，**这应该能工作**。如果空白，可能是 onedir 模式下 `_set_app_icon` 之类副作用。需确认。
- **Word**（`extract_text_file` 第2959行）：**设计上就不渲染到画布**——它只把文字抽出来放进"OCR文本"标签页，并把 `pdf_canvas` 清空。所以 Word 打开后主预览区必然是空白，只有结果面板有文字。

**修复**：
1. Word 文件打开后在预览区用 `tk.Text` 显示提取的文字内容（带边框、可滚动），而不是空白
2. PDF 增加错误兜底：若 PyMuPDF 不可用则明确报错

### 问题4：AI 对话框缺少保存按钮
用户说 AI 对话框里要有"导出 WORD 和 PDF"的功能按钮。当前 AI 窗口标题栏只有 📌 ⚙️ 🗑 — ✕，没有导出按钮。

**修复**：
1. 在 AI 聊天窗口标题栏添加 "💾 导出" 按钮（或 "📥"）
2. 点击后弹出选项：导出为 Word（.docx）/ 导出为 PDF（.pdf）
3. 导出内容 = 当前 AI 对话历史（`self._messages`）
4. PDF 导出需要 `reportlab` 或 `fpdf2` —— 会加入 requirements.txt 和 build.yml

---

### 实施顺序
1. 生成标准 256x256 ICO 并修复 `_set_app_icon` 对所有窗口生效
2. 修复 DWG：友好提示 + 引导转 DXF
3. 修复 Word 预览显示
4. AI 聊天加导出按钮（Word + PDF）
5. 更新 requirements.txt，确保 reportlab/fpdf2 在 CI 安装
6. 确认 build.yml 图标参数

### 风险与取舍
- DWG 直读因技术限制无法纯开源实现，采用"提示转 DXF"是业界标准做法
- PDF 导出引入 reportlab（成熟稳定），fpdf2 作为备选