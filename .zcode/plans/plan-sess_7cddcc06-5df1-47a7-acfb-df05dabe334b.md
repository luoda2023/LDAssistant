## 问题分析与修复计划

### 问题 1：EXE 启动太慢

**原因：** `App.__init__` 中 `StandardChecker()` 同步加载 543791 条 JSON 数据（143MB），阻塞 UI 显示，用户看到空白窗口。

**修复方案：** 改为异步加载
1. `App.__init__` 中先创建窗口、显示 UI，再通过 `after()` 延迟加载数据
2. 加载期间状态栏显示"正在加载标准数据库..."，加载完成后显示"就绪"
3. 在加载完成前，查询功能返回"数据加载中，请稍候"

### 问题 2：DWG/Word/图片/PDF 打不开

**原因分析：**
- **DWG (AcmeCAD)**：需要 `D:/Program Files/AcmeCAD2023-v8.10.6.1560-Chs.exe` 和 `pywin32`，但 AcmeCAD 是第三方的，必须在用户电脑上安装。打包后用户电脑可能没有这个路径
- **DXF (ezdxf)**：需要 `ezdxf` 和 `matplotlib`，已 `--hidden-import` 但可能遗漏
- **DOCX (python-docx)**：已 `--hidden-import "docx"`，但 `docx.shared`、`docx.enum.text` 等子模块可能未打包全
- **PDF (PyMuPDF)**：`fitz` 是 C 扩展，PyInstaller 自动打包有时会漏掉 `fitz` 的 DLL
- **图片 (Pillow)**：PIL 的 `ImageCms`、`ImageDraw`、`ImageFont` 等可能未打包

**修复方案：**
1. 检查 build.yml 中 PyInstaller 的 hidden-import 是否完整
2. 为 DWG 的 AcmeCAD 路径不存在时给出友好提示
3. 添加 `--hidden-import` 补充：`docx.text`, `docx.document`, `PIL.ImageCms`, `PIL.ImageDraw`, `PIL.ImageFont`, `PIL.ImageFilter`, `PIL.ImageTk`, `fitz`, `fitz.utils`
4. 增加 `--collect-all` 或 `--add-data` 确保 DLL 被打包

### 问题 3：AI 聊天 UI 格式

**当前：** 用户和 AI 消息都用气泡格式，每个气泡底部都有操作栏（复制、导出等）

**需求：**
- 用户消息 → 保留在右侧（当前已实现），可以保留气泡形式
- AI/系统消息 → 不要气泡，**不要操作栏**，纯文本对齐左侧，类似普通聊天界面

**修复方案：**
1. `add_message()` 中当 `role == 'ai'` 时，不用气泡（`_add_text_bubble`），改用普通 `tk.Message` 或 `tk.Label` 加 `wraplength`
2. 去掉 AI 消息底部的操作栏（`_add_action_bar`）
3. 简化 AI 消息样式：白色背景、左对齐、无边框、无圆角

### 文件修改清单

| 文件 | 修改内容 |
|------|---------|
| `standard_checker_v2.py` | 1️⃣ 异步加载数据（`after_idle`） 2️⃣ 修复 AI 聊天 UI 3️⃣ 补充 hidden-import |
| `.github/workflows/build.yml` | 补充缺失的 `--hidden-import` 和 `--collect-all` |