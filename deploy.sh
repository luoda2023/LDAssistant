#!/bin/bash
set -e
export PATH="/c/Program Files/dotnet:$PATH"
cd /d/ZCODE/LDAssistant

BIN="/d/ZCODE/LDAssistant/bin/Release/net8.0-windows/win-x64"
PUB="$BIN/publish"
DIST="/d/ZCODE/dist/LDAssistant"
FINAL="/d/ZCODE/dist_final"

echo "=== 结束运行中的进程（否则 exe 被占用，复制会静默失败）==="
taskkill //F //IM LDAssistant.exe 2>/dev/null || true
sleep 1

echo "=== 编译 ==="
dotnet build -c Release 2>&1 | grep -E "error|Build succeeded|已成功生成" | head -5

echo "=== 发布（单文件+自包含） ==="
dotnet publish -c Release -r win-x64 2>&1 | tail -2

echo "=== 部署到 dist（单文件布局）==="
rm -rf "$DIST" 2>/dev/null || true
mkdir -p "$DIST"
cp -r "$PUB"/* "$DIST"/

cp "$DIST/x64/pdfium.dll" "$DIST/pdfium.dll" 2>/dev/null || true
cp "$BIN/PdfiumViewer.dll" "$DIST"/ 2>/dev/null || true

rm -f "$DIST/models/v5/latin_PP-OCRv5_rec_mobile_infer.onnx"
rm -f "$DIST/models/v5/ppocrv5_latin_dict.txt"
rm -f "$DIST/models/v5/ch_PP-OCRv5_mobile_det.onnx"
rm -f "$DIST/ocr_init.log"

cp /d/ZCODE/standards.db "$DIST"/ 2>/dev/null || true
cp /d/ZCODE/app_icon.ico "$DIST"/ 2>/dev/null || true

echo "=== 部署到 dist_final（多文件布局，用户实际运行目录）==="
# dist_final 与 bin 目录同构（非单文件），必须整目录覆盖，
# 只补部分子目录会让 exe 停在旧版本 —— 这是之前"改了没生效"的根因。
mkdir -p "$FINAL"
cp -rf "$BIN"/*.exe "$FINAL"/ 2>/dev/null || true
cp -rf "$BIN"/*.dll "$FINAL"/ 2>/dev/null || true
cp -rf "$BIN"/*.json "$FINAL"/ 2>/dev/null || true
cp -rf "$BIN"/*.pdb "$FINAL"/ 2>/dev/null || true
for d in runtimes x64 models fonts RapidOCR file-viewer Html; do
    [ -d "$BIN/$d" ] && cp -rf "$BIN/$d" "$FINAL"/ 2>/dev/null || true
done
cp "$FINAL/x64/pdfium.dll" "$FINAL/pdfium.dll" 2>/dev/null || true
cp /d/ZCODE/standards.db "$FINAL"/ 2>/dev/null || true
cp /d/ZCODE/app_icon.ico "$FINAL"/ 2>/dev/null || true

# cad-viewer 网页查看器（mlightcad/cad-viewer）静态站点
# 构建产物位于源码工作区的 packages/cad-viewer-example/dist
CADVIEWER_SRC="/d/ZCODE/cad-viewer/packages/cad-viewer-example/dist"
if [ -d "$CADVIEWER_SRC" ]; then
  for target in "$FINAL" "$DIST"; do
    rm -rf "$target/cad-viewer" 2>/dev/null || true
    mkdir -p "$target/cad-viewer"
    cp -r "$CADVIEWER_SRC"/* "$target/cad-viewer"/
  done
  echo "cad-viewer 已部署到 $FINAL/cad-viewer"
else
  echo "警告：未找到 cad-viewer 构建产物（$CADVIEWER_SRC），跳过部署"
fi

echo ""
echo "=== 校验：两个目录的 exe/主 DLL 时间戳应与刚编译的一致 ==="
for p in "$BIN/LDAssistant.dll" "$FINAL/LDAssistant.dll" "$DIST/LDAssistant.exe"; do
    [ -f "$p" ] && ls -la "$p"
done

echo ""
echo "=== 部署完成（未自动启动）==="
echo "请手动运行：$FINAL/LDAssistant.exe"
