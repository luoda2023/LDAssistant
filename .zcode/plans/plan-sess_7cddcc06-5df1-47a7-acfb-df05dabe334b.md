## 计划：AI聊天窗口全功能与新暗色UI无缝整合

通过审查代码，AI聊天窗口的核心功能（消息气泡、富文本Markdown渲染、复制、导出、OCR推送、表格/图片/文件组件）已经应用了暗色主题，但有几个关键接合点需要修复以确保完全整合：

### 问题1：`_export_chat()` 导出对话框未应用暗色主题（L1011-1031）
导出格式选择对话框使用 `ttk.Label` 和 `ttk.Button`，对话框背景未设置，在深色窗口下会显示白色背景，与整体暗色风格不协调。

**修复方案：** 设置对话框背景为 `C['bg_dark']`，将 `ttk.Label`/`ttk.Button` 替换为 `tk.Label`/`tk.Label`（扁平暗色风格），与 AI 配置对话框风格一致。

### 问题2：`_open_config()` 配置对话框中的 `ttk.Combobox`（L1173-1175）
在暗色主题下，`ttk.Combobox` 使用默认样式（白色背景），与对话框的深色背景不协调。需要替换为自定义的 `tk.Frame` + `tk.Entry` + 下拉按钮组合。

### 问题3：OCR 结果自动推送的 UI 集成验证
确保 `set_ocr_results()` 和 `send_standard_check()` 调用 `add_message()` 时，表格/消息渲染全部使用暗色主题——已确认已完成。

### 涉及文件
仅 `standard_checker_v2.py`

### 实施步骤
1. 修复 `_export_chat()` 导出对话框：暗色背景 + 扁平 tk.Label 按钮替代 ttk.Button
2. 修复 `_open_config()` 中 `ttk.Combobox` 为暗色自定义下拉框
3. 最终语法验证及全面检查