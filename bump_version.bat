@echo off
chcp 65001 >nul
title 工程助手 — 版本号提升工具
echo ========================================
echo   工程助手 — 版本号自动提升 (bump)
echo ========================================
echo.

set VERSION_FILE=J:\WorkBuddy-work\csres-standards\VERSION.py

if not exist "%VERSION_FILE%" (
    echo [错误] VERSION.py 未找到！
    pause
    exit /b 1
)

:: 读取当前版本号
for /f "tokens=2 delims=()" %%a in ('findstr "VERSION =" "%VERSION_FILE%"') do set VERSION_LINE=%%a
:: 解析 major, minor, patch
for /f "tokens=1,2,3 delims=, " %%a in ("%VERSION_LINE%") do (
    set MAJOR=%%a
    set MINOR=%%b
    set PATCH=%%c
)

:: 补丁号 +1
set /a NEW_PATCH=%PATCH%+1

echo   当前版本: %MAJOR%.%MINOR%.%PATCH%
echo   新版本号:  %MAJOR%.%MINOR%.%NEW_PATCH%
echo.
echo   即将更新 VERSION.py 并创建 commit ...
echo.

:: 替换 VERSION.py 中的版本号
powershell -Command "(Get-Content '%VERSION_FILE%') -replace 'VERSION = \(\s*%MAJOR%\s*,\s*%MINOR%\s*,\s*%PATCH%\s*\)', 'VERSION = (%MAJOR%, %MINOR%, %NEW_PATCH%)' | Set-Content '%VERSION_FILE%' -Encoding UTF8"

echo ✅ VERSION.py 已更新

:: 提交到 git
cd /d "J:\WorkBuddy-work\csres-standards"
git add VERSION.py standard_checker.py
git commit -m "chore: bump version to %MAJOR%.%MINOR%.%NEW_PATCH%"
git push origin v10-build

echo.
echo ✅ 版本已提升至 %MAJOR%.%MINOR%.%NEW_PATCH% 并推送
echo.
pause
