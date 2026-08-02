"""下载并解压 PaddleOCR-json 到 ocr/ 目录（供 CI 构建使用）"""
import urllib.request
import urllib.error
import os
import sys
import shutil
import time

URL = "https://github.com/hiroi-sora/PaddleOCR-json/releases/download/v1.4.1/PaddleOCR-json_v1.4.1_windows_x64.7z"
OCR_DIR = os.path.join(os.path.dirname(__file__), "ocr")
MAX_RETRIES = 5
DOWNLOAD_TIMEOUT = 120  # 单次下载超时秒数


def _download_with_retry(url, dest, retries=MAX_RETRIES):
    """带超时和重试的下载"""
    for attempt in range(1, retries + 1):
        try:
            print(f"[DL]  下载尝试 {attempt}/{retries} ({url})...")
            req = urllib.request.Request(url, headers={'User-Agent': 'LDAssistant-Build/1.0'})
            with urllib.request.urlopen(req, timeout=DOWNLOAD_TIMEOUT) as resp:
                with open(dest, 'wb') as f:
                    while True:
                        chunk = resp.read(8192)
                        if not chunk:
                            break
                        f.write(chunk)
            size = os.path.getsize(dest)
            print(f"[OK] 下载完成: {size/1024/1024:.1f} MB")
            return True
        except Exception as e:
            print(f"[X] 下载失败 (尝试 {attempt}/{retries}): {e}")
            if os.path.exists(dest):
                os.remove(dest)
            if attempt < retries:
                wait = attempt * 10
                print(f"[WAIT] 等待 {wait} 秒后重试...")
                time.sleep(wait)
    return False


def main():
    if os.path.isdir(OCR_DIR) and os.path.isfile(os.path.join(OCR_DIR, "PaddleOCR-json.exe")):
        exe_size = os.path.getsize(os.path.join(OCR_DIR, "PaddleOCR-json.exe"))
        print(f"[OK] OCR 已存在: {OCR_DIR} (PaddleOCR-json.exe {exe_size/1024:.0f} KB)")
        models_dir = os.path.join(OCR_DIR, "models")
        if os.path.isdir(models_dir):
            print(f"[OK] 模型目录已存在: {models_dir}")
            return
        print("[WARN] 模型目录缺失，将重新下载")
        shutil.rmtree(OCR_DIR)

    # 清理旧目录
    if os.path.isdir(OCR_DIR):
        shutil.rmtree(OCR_DIR)
    os.makedirs(OCR_DIR, exist_ok=True)

    # 下载（带重试）
    temp_path = os.path.join(OCR_DIR, "download.7z")
    if not _download_with_retry(URL, temp_path):
        print("[X] 下载失败，已耗尽所有重试次数")
        sys.exit(1)

    # 解压
    print("[PACKAGE] 正在解压...")
    try:
        import subprocess
        # 尝试用 7z.exe
        for sevenz in ["7z", "7z.exe", "C:/Program Files/7-Zip/7z.exe",
                       "C:/Program Files (x86)/7-Zip/7z.exe"]:
            try:
                result = subprocess.run([sevenz, "x", temp_path, f"-o{OCR_DIR}", "-y"],
                                        capture_output=True, timeout=120, check=True)
                print(f"[OK] 使用 {sevenz} 解压成功")
                break
            except (FileNotFoundError, subprocess.TimeoutExpired, subprocess.CalledProcessError) as e:
                if isinstance(e, subprocess.CalledProcessError):
                    print(f"[WARN] {sevenz} 解压返回非零退出码: {e.returncode}")
                continue
        else:
            # 7z 不可用，尝试用 Python 的 py7zr
            try:
                import py7zr
                with py7zr.SevenZipFile(temp_path, 'r') as archive:
                    archive.extractall(path=OCR_DIR)
                print("[OK] 使用 py7zr 解压成功")
            except ImportError:
                print("[WARN] 未找到 7z/py7zr，尝试安装 py7zr...")
                subprocess.run([sys.executable, "-m", "pip", "install", "py7zr"],
                               capture_output=True, check=True)
                import py7zr
                with py7zr.SevenZipFile(temp_path, 'r') as archive:
                    archive.extractall(path=OCR_DIR)
                print("[OK] 使用 py7zr 解压成功")
    except Exception as e:
        print(f"[X] 解压失败: {e}")
        sys.exit(1)
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)

    # 验证
    exe_path = os.path.join(OCR_DIR, "PaddleOCR-json.exe")
    if os.path.isfile(exe_path):
        exe_size = os.path.getsize(exe_path)
        print(f"[OK] PaddleOCR-json.exe 已就绪: {exe_size/1024:.0f} KB")
    else:
        # 可能解压到了子目录
        for root, dirs, files in os.walk(OCR_DIR):
            for f in files:
                if f.lower() == "paddleocr-json.exe":
                    src = os.path.join(root, f)
                    shutil.move(src, exe_path)
                    print(f"[OK] 移动 {src} -> {exe_path}")
                    break
        if not os.path.isfile(exe_path):
            print(f"[X] 未找到 PaddleOCR-json.exe")
            print(f"  解压目录内容: {os.listdir(OCR_DIR)}")
            sys.exit(1)

    # 验证模型目录
    models_dir = os.path.join(OCR_DIR, "models")
    if os.path.isdir(models_dir):
        model_count = len(os.listdir(models_dir))
        print(f"[OK] 模型目录就绪: {model_count} 个文件/目录")
    else:
        print(f"[WARN] 未找到 models 目录，OCR 可能无法正常工作")
        print(f"  ocr 目录内容: {os.listdir(OCR_DIR)}")

    total_size = sum(os.path.getsize(os.path.join(dp, f))
                     for dp, dn, filenames in os.walk(OCR_DIR)
                     for f in filenames) / 1024 / 1024
    print(f"[OK] OCR 目录总大小: {total_size:.0f} MB")
    print(f"[OK] OCR 下载并解压完成: {OCR_DIR}")


if __name__ == "__main__":
    main()
