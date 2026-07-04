@echo off
chcp 65001 >nul

:: ── 从 VERSION.py 读取版本号 ──
for /f "tokens=*" %%i in ('powershell -Command "exec(open('J:\WorkBuddy-work\csres-standards\VERSION.py').read()); print(str(VERSION[0])+'.'+str(VERSION[1])+'.'+str(VERSION[2]))"') do set FULL_VER=%%i
for /f "tokens=*" %%i in ('powershell -Command "exec(open('J:\WorkBuddy-work\csres-standards\VERSION.py').read()); print('v'+str(VERSION[0]))"') do set DISPLAY_VER=%%i
for /f "tokens=*" %%i in ('powershell -Command "exec(open('J:\WorkBuddy-work\csres-standards\VERSION.py').read()); print(str(VERSION[0])+'.'+str(VERSION[1]))"') do set APP_VER=%%i

echo VERSION: %FULL_VER% / %DISPLAY_VER% / %APP_VER%

title 构建工程助手 %DISPLAY_VER%
echo ========================================
echo   工程助手 LDAssistant %DISPLAY_VER% — 构建脚本
echo ========================================
echo.

set PROJECT_DIR=J:\WorkBuddy-work\csres-standards

echo [1/5] 清理旧构建...
if exist "%PROJECT_DIR%\dist" rmdir /s /q "%PROJECT_DIR%\dist" 2>nul
if exist "%PROJECT_DIR%\build" rmdir /s /q "%PROJECT_DIR%\build" 2>nul
echo       ✅ 清理完成
echo.

echo [2/5] 确认图标文件...
if exist "%PROJECT_DIR%\app_icon.ico" (
    echo       ✓ 图标文件已就绪
) else (
    echo       ⚠ 图标文件未找到
)
echo.

echo [3/5] 开始 PyInstaller 构建（单文件 + 文件夹版）...
echo.
cd /d "%PROJECT_DIR%"

uv tool run pyinstaller --noconfirm --clean standard_checker.spec

echo.
if errorlevel 1 (
    echo [错误] PyInstaller 构建失败！
    pause
    exit /b 1
)
echo       ✅ PyInstaller 构建完成
echo.

echo [4/5] 打包 SFX 安装程序...
set "SEVENZ=D:\vcpkg\downloads\tools\7zip-26.01-windows\7z.exe"

:: 准备文件
rmdir /s /q "%TEMP%\sfx_build" 2>nul
mkdir "%TEMP%\sfx_build"
copy "%PROJECT_DIR%\dist\工程助手.exe" "%TEMP%\sfx_build\"
copy "%PROJECT_DIR%\setup_helper.bat" "%TEMP%\sfx_build\"

:: 创建 7z 压缩包
"%SEVENZ%" a -mx=9 -t7z "%TEMP%\installer.7z" "%TEMP%\sfx_build\*" -y >nul

:: 创建 SFX 配置文件
(
echo ;!@Install@!UTF-8!
echo Title="工程助手 LDAssistant v10 安装程序"
echo BeginPrompt="工程助手 LDAssistant v10\n\n标准规范智能识别工具\n\n本程序将安装到您的计算机中，继续请按确定。"
echo ExtractPath="%%ProgramW6432%%\工程助手 LDAssistant"
echo ExtractTitle="正在安装..."
echo RunProgram="setup_helper.bat"
echo Directory="工程助手"
echo OverwriteMode="a"
echo DeleteExtractedFiles="no"
echo ;!@InstallEnd@!
) > "%TEMP%\sfx_config.txt"

:: 构建 SFX
copy /b "D:\vcpkg\downloads\tools\7zip-26.01-windows\7z.sfx" + "%TEMP%\sfx_config.txt" + "%TEMP%\installer.7z" "%PROJECT_DIR%\工程助手_v10_安装程序.exe" >nul
echo       ✅ SFX 安装程序已创建
echo.

echo [5/5] 打包便携版 ZIP...
"%SEVENZ%" a -tzip -mx=9 "%PROJECT_DIR%\工程助手_v10_便携版.zip" "%PROJECT_DIR%\dist\工程助手.exe" -y >nul
"%SEVENZ%" a -tzip -mx=9 "%PROJECT_DIR%\工程助手_v10_完整便携版.zip" "%PROJECT_DIR%\dist\工程助手_v10\" -y >nul
echo       ✅ 便携版 ZIP 已创建
echo.

echo ========================================
echo   构建全部完成！产物清单：
echo ========================================
echo.
echo  📦 dist\工程助手.exe               — 单文件版 (可便携)
echo  📦 dist\工程助手_v10\              — 文件夹版 (含OCR)
echo  📦 工程助手_v10_安装程序.exe        — 安装程序 (SFX自解压)
echo  📦 工程助手_v10_便携版.zip          — 便携ZIP (单文件)
echo  📦 工程助手_v10_完整便携版.zip       — 完整ZIP (文件夹版)
echo.
echo ========================================
pause
