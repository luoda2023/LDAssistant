#!/bin/bash
set -e
export PATH="/c/Program Files/dotnet:$PATH"
cd /d/ZCODE/LDAssistant

echo "=== 编译 ==="
dotnet build -c Release 2>&1 | grep -E "error|Build succeeded" | head -5

echo "=== 发布（单文件+自包含） ==="
dotnet publish -c Release -r win-x64 2>&1 | tail -2

echo "=== 部署到 dist ==="
rm -rf /d/ZCODE/dist/LDAssistant 2>/dev/null
mkdir -p /d/ZCODE/dist/LDAssistant

# 复制 publish 目录（单文件exe + native DLL）
cp -r /d/ZCODE/LDAssistant/bin/Release/net8.0-windows/win-x64/publish/* /d/ZCODE/dist/LDAssistant/

# 确保 pdfium.dll 在 exe 同级目录
cp /d/ZCODE/dist/LDAssistant/x64/pdfium.dll /d/ZCODE/dist/LDAssistant/pdfium.dll 2>/dev/null || true

# 复制 PdfiumViewer.dll（managed DLL，虽然打包在exe中但额外放置以防万一）
cp /d/ZCODE/LDAssistant/bin/Release/net8.0-windows/win-x64/PdfiumViewer.dll /d/ZCODE/dist/LDAssistant/ 2>/dev/null || true

# 清理不需要的模型文件
rm -f /d/ZCODE/dist/LDAssistant/models/v5/latin_PP-OCRv5_rec_mobile_infer.onnx
rm -f /d/ZCODE/dist/LDAssistant/models/v5/ppocrv5_latin_dict.txt
rm -f /d/ZCODE/dist/LDAssistant/models/v5/ch_PP-OCRv5_mobile_det.onnx

# 复制数据文件
cp /d/ZCODE/standards.db /d/ZCODE/dist/LDAssistant/ 2>/dev/null || true
cp /d/ZCODE/app_icon.ico /d/ZCODE/dist/LDAssistant/ 2>/dev/null || true

# 清理日志
rm -f /d/ZCODE/dist/LDAssistant/ocr_init.log

echo "=== 部署完成 ==="
echo "dist 目录内容："
ls -la /d/ZCODE/dist/LDAssistant/ | grep -v "^total"
echo ""
echo "models 目录："
ls -la /d/ZCODE/dist/LDAssistant/models/v5/

echo "=== 启动测试 ==="
taskkill //F //IM LDAssistant.exe 2>/dev/null || true
sleep 1
cd /d/ZCODE/dist/LDAssistant && ./LDAssistant.exe &
sleep 4
echo "=== 完成 ==="
