#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 app_icon.png 生成标准 256x256 ICO，供 PyInstaller --icon 使用。
生成纯 ICO 格式（不含 PNG 多帧），确保 Windows 任务栏 / 资源管理器正确显示。
"""
import struct
import io
from pathlib import Path
from PIL import Image, ImageDraw

PNG = Path(__file__).parent / "app_icon.png"
OUT = Path(__file__).parent / "app_icon.ico"

# 读取源 PNG
src = Image.open(PNG).convert("RGBA")
src = src.resize((256, 256), Image.Resampling.LANCZOS)

# 写标准 ICO 文件（含 256x256、48x48、32x32、16x16 四尺寸）
ICO_SIZE = [(256, 256), (48, 48), (32, 32), (16, 16)]

png_bufs = []
for w, h in ICO_SIZE:
    small = src.resize((w, h), Image.Resampling.LANCZOS)
    buf = io.BytesIO()
    small.save(buf, format="PNG")
    png_bufs.append((w, h, buf.getvalue()))

# ICO header: 0, 1, count
header = struct.pack("<HHH", 0, 1, len(ICO_SIZE))

# ICO directory entries
dir_entries = b""
offset = 6 + len(ICO_SIZE) * 16  # header + directory
total_png = sum(len(p[2]) for p in png_bufs)
for w, h, data in png_bufs:
    entry = struct.pack(
        "<BBBBHHII",
        0 if w == 256 else w,  # bWidth (0 = 256 per ICO spec)
        0 if h == 256 else h,  # bHeight (0 = 256 per ICO spec)
        0,
        0,
        1,
        32,
        len(data),
        offset,
    )
    dir_entries += entry
    offset += len(data)

with open(OUT, "wb") as f:
    f.write(header + dir_entries)
    for _, _, data in png_bufs:
        f.write(data)

print(f"Written: {OUT} ({OUT.stat().st_size} bytes)")
