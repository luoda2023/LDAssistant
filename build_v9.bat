@echo off
echo Building standard_checker v9 (新数据库版)...
echo.

set SCRIPT=J:/WorkBuddy-work/csres-standards/standard_checker.py
set OUTPUT=J:/WorkBuddy-work/csres-standards/dist/工程助手v9
set DB=J:/WorkBuddy-work/csres-standards/standards_new.db
set OCR=D:/Program Files/图片文字识别/UmiOCR-data/plugins/win7_x64_PaddleOCR-json/PaddleOCR-json.exe

echo Step 1: Building EXE with PyInstaller...
pyinstaller --onedir --name "工程助手" --distpath "%OUTPUT%" ^
    --add-data "%DB%;data" ^
    --add-binary "%OCR%;ocr" ^
    --hidden-import=PIL --hidden-import=docx --hidden-import=fitz ^
    "%SCRIPT%"

if errorlevel 1 (
    echo [ERR] Build failed
    pause
    exit /b 1
)
echo [OK] Build complete

echo.
echo Step 2: Preparing portable package...
set PORTABLE=D:/tmp/工程助手v9_portable
set PKG=D:/Personal/Downloads/工程助手v9.zip

if not exist "%PORTABLE%" mkdir "%PORTABLE%"
if not exist "%PORTABLE%\app" mkdir "%PORTABLE%\app"

xcopy /E /Y /I "%OUTPUT%\*" "%PORTABLE%\app\"
copy /Y "%SCRIPT%" "%PORTABLE%\app\standard_checker.py"

echo [OK] Portable files prepared

echo.
echo Step 3: Creating ZIP package...
powershell -Command "Compress-Archive -Path '%PORTABLE%\*' -DestinationPath '%PKG%' -Force"

if errorlevel 1 (
    echo [ERR] ZIP creation failed
    pause
    exit /b 1
)
echo [OK] Package created: %PKG%

echo.
echo Done!
