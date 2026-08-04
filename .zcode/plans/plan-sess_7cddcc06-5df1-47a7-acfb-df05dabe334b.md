# LDAssistant C# WPF 重写计划

## 技术栈
- **C# .NET 8 + WPF** — 原生 Windows 桌面，界面美观
- **MaterialDesign/HandyControl** — 现代 UI 控件库
- **PictureBox / DrawFlow** — PDF/图片预览渲染
- **Microsoft.Data.Sqlite** — 直接读取现有 `standards.db`（SQLite + FTS5）
- **System.Text.RegularExpressions** — 复用现有正则

## 复用现有数据（不重新生成）
- `D:\ZCODE\standards.db` — 54万条标准数据库，SQLite + FTS5
- `D:\ZCODE\data\all_standards_merged_*.json` — JSON 备选
- OCR: `D:\Program Files\图片文字识别\UmiOCR-data\plugins\win7_x64_PaddleOCR-json\PaddleOCR-json.exe`
- AI API: `http://47.114.75.115:40000/v1/chat/completions`，Key: `sk-proxy-local-...`，Model: `hermesAPI`
- 正则: `[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?\s*\d+(?:\.\d+)?-\d{4}`

## 界面布局：左中右三栏 + 浅色清爽风格

```
┌──────────────────────────────────────────────────┐
│ 顶部工具栏（白色底）                                │
│ 打开 | 文件夹 | < > | 放大 缩小 旋转 | OCR 检查 导出 AI │
├──────┬───────────────────┬──────────────────────┤
│ 缩略图│                   │ Tab: [OCR文本][编号][检查] │
│ 栏    │   预览大图区       │                      │
│ 180px │   (白色底)         │ 列表内容              │
│      │                   │ 规范编号 / 状态 / 替代   │
│      │                   │                      │
├──────┴───────────────────┴──────────────────────┤
│ 状态栏: 就绪              进度: ███ 100%          │
└──────────────────────────────────────────────────┘
```

- 浅色主题：白底 `#FFFFFF` + 浅灰边框 `#E0E0E0` + 蓝色高亮 `#2196F3`
- 字体：微软雅黑 14px

## 项目结构

```
LDAssistant/
├── App.xaml / App.xaml.cs          — 启动入口
├── MainWindow.xaml / .cs           — 主窗口 + 三栏布局
├── ViewModels/
│   ├── MainViewModel.cs            — 主逻辑
│   ├── OcrService.cs               — OCR 调用 PaddleOCR-json
│   ├── StandardChecker.cs          — SQLite 标准检查
│   ├── CodeExtractor.cs            — 正则提取规范编号
│   ├── AiService.cs                — AI API 调用
│   └── FilePreviewService.cs       — PDF/图片/Word 预览
├── Models/
│   ├── StandardRecord.cs           — 标准数据模型
│   ├── CheckResult.cs              — 检查结果模型
│   └── OcrResult.cs                — OCR 结果模型
└── LDAssistant.csproj              — .NET 8 项目文件
```

## 四大核心功能

### 1. 文件预览
- PDF: 用 PdfiumViewer（轻量 PDF 渲染库）渲染，支持翻页/缩放/旋转
- 图片: WPF Image 控件 + 翻页/缩放/旋转
- Word: 提取文本渲染为图片（复用现有逻辑思路）
- CAD(DXF): 用 netDxf 库读取 + 简单渲染
- 缩略图栏: 左侧显示所有已打开文件的缩略图

### 2. OCR 识别
- 调用现有 `PaddleOCR-json.exe`，参数 `-image_path=<path>`
- 框选区域 OCR：鼠标拖拽选区 → 裁剪图片 → OCR
- 批量 OCR：遍历所有页面
- 解析 JSON 输出（text + box 坐标）

### 3. 规范编号检查
- 正则提取: `[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?\s*\d+(?:\.\d+)?-\d{4}`
- 从 OCR 文本中提取所有编号
- 查询 `standards.db`：精确匹配 → 名称匹配 → FTS5 模糊匹配
- 显示状态：现行/作废/被替代
- 右侧"检查结果"Tab 显示列表

### 4. 报告导出 + AI 助手
- 导出 Word: 用 OpenXML SDK 生成 .docx
- 导出 Excel: 用 ClosedXML 生成 .xlsx
- AI 对话: HTTP POST 到 `http://47.114.75.115:40000/v1/chat/completions`，OpenAI 兼容格式

## 构建方式
- 用 GitHub Actions 的 `windows-latest` + `dotnet` 环境构建
- 生成单个 .exe（.NET 8 self-contained publish）
- 复用现有 `build.yml` 的 Release 上传逻辑

## 工作步骤
1. 创建 WPF 项目骨架 + .csproj
2. 实现 MainWindow.xaml 三栏布局（浅色主题）
3. 实现文件预览（PDF + 图片）
4. 实现 OCR 服务（调用 PaddleOCR）
5. 实现规范编号检查（SQLite 查询）
6. 实现报告导出（Word + Excel）
7. 实现 AI 对话窗口
8. 更新 build.yml 为 .NET 构建
9. 构建测试