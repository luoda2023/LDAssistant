"""下载并解压 PaddleOCR-json 到 ocr/ 目录（供 CI 构建使用）"""
import urllib.request, urllib.error, zipfile, io, os, sys, shutil

URL = "https://github.com/hiroi-sora/PaddleOCR-json/releases/download/v1.4.1/PaddleOCR-json_v1.4.1_windows_x64.7z"
OCR_DIR = os.path.join(os.path.dirname(__file__), "ocr")

def main():
    if os.path.isdir(OCR_DIR) and os.path.isfile(os.path.join(OCR_DIR, "PaddleOCR-json.exe")):
        exe_size = os.path.getsize(os.path.join(OCR_DIR, "PaddleOCR-json.exe"))
        print(f"✅ OCR 已存在: {OCR_DIR} (PaddleOCR-json.exe {exe_size/1024:.0f} KB)")
        # 确保 models 目录也存在
        models_dir = os.path.join(OCR_DIR, "models")
        if os.path.isdir(models_dir):
            print(f"✅ 模型目录已存在: {models_dir}")
            return
        print("⚠️ 模型目录缺失，将重新下载")
        shutil.rmtree(OCR_DIR)

    # 清理旧目录
    if os.path.isdir(OCR_DIR):
        shutil.rmtree(OCR_DIR)
    os.makedirs(OCR_DIR, exist_ok=True)

    # 下载
    print(f"⬇️  正在下载 PaddleOCR-json v1.4.1 ({URL})...")
    temp_path = os.path.join(OCR_DIR, "download.7z")
    try:
        urllib.request.urlretrieve(URL, temp_path)
        size = os.path.getsize(temp_path)
        print(f"✅ 下载完成: {size/1024/1024:.1f} MB")
    except Exception as e:
        print(f"❌ 下载失败: {e}")
        if os.path.exists(temp_path):
            os.remove(temp_path)
        sys.exit(1)

    # 解压
    print("📦 正在解压...")
    try:
        # 7z 格式需要 7z 或调用外部工具
        # 改用 zipfile 尝试（但 .7z 文件不是标准 zip）
        # 尝试用系统自带的 tarfile 或调用 7z
        # 先检查是否有 7z 命令
        import subprocess
        # 尝试用 7z.exe
        for sevenz in ["7z", "7z.exe", "C:/Program Files/7-Zip/7z.exe",
                       "C:/Program Files (x86)/7-Zip/7z.exe"]:
            try:
                subprocess.run([sevenz, "x", temp_path, f"-o{OCR_DIR}", "-y"],
                              capture_output=True, timeout=120)
                print(f"✅ 使用 {sevenz} 解压成功")
                break
            except (FileNotFoundError, subprocess.TimeoutExpired):
                continue
        else:
            # 7z 不可用，尝试用 Python 的 py7zr
            try:
                import py7zr
                with py7zr.SevenZipFile(temp_path, 'r') as archive:
                    archive.extractall(path=OCR_DIR)
                print("✅ 使用 py7zr 解压成功")
            except ImportError:
                # 最后尝试: 下载并安装 py7zr
                print("⚠️ 未找到 7z/py7zr，尝试安装 py7zr...")
                subprocess.run([sys.executable, "-m", "pip", "install", "py7zr"],
                              capture_output=True)
                import py7zr
                with py7zr.SevenZipFile(temp_path, 'r') as archive:
                    archive.extractall(path=OCR_DIR)
                print("✅ 使用 py7zr 解压成功")
    except Exception as e:
        print(f"❌ 解压失败: {e}")
        sys.exit(1)
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)

    # 验证
    exe_path = os.path.join(OCR_DIR, "PaddleOCR-json.exe")
    if os.path.isfile(exe_path):
        exe_size = os.path.getsize(exe_path)
        print(f"✅ PaddleOCR-json.exe 已就绪: {exe_size/1024:.0f} KB")
    else:
        # 可能解压到了子目录
        for root, dirs, files in os.walk(OCR_DIR):
            for f in files:
                if f.lower() == "paddleocr-json.exe":
                    src = os.path.join(root, f)
                    shutil.move(src, exe_path)
                    print(f"✅ 移动 {src} -> {exe_path}")
                    break
        if not os.path.isfile(exe_path):
            print(f"❌ 未找到 PaddleOCR-json.exe")
            print(f"   解压目录内容: {os.listdir(OCR_DIR)}")
            sys.exit(1)

    # 验证模型目录
    models_dir = os.path.join(OCR_DIR, "models")
    if os.path.isdir(models_dir):
        model_count = len(os.listdir(models_dir))
        print(f"✅ 模型目录就绪: {model_count} 个文件/目录")
    else:
        print(f"⚠️ 未找到 models 目录，OCR 可能无法正常工作")
        print(f"   ocr 目录内容: {os.listdir(OCR_DIR)}")

    total_size = sum(os.path.getsize(os.path.join(dp, f))
                     for dp, dn, filenames in os.walk(OCR_DIR)
                     for f in filenames) / 1024 / 1024
    print(f"✅ OCR 目录总大小: {total_size:.0f} MB")
    print(f"✅ OCR 下载并解压完成: {OCR_DIR}")


if __name__ == "__main__":
    main()