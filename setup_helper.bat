@echo off
chcp 65001 >nul
title 工程助手 LDAssistant v10 — 安装完成

setlocal enabledelayedexpansion

:: ======================================================
:: 工程助手 LDAssistant v10 — 安装后配置脚本
:: ======================================================

:: 获取当前目录（即安装目录）
set "APP_DIR=%~dp0"
set "EXE_PATH=%APP_DIR%工程助手.exe"
set "APP_NAME=工程助手 LDAssistant"

echo.
echo ========================================
echo   工程助手 LDAssistant v10
echo   安装配置完成正在进行...
echo ========================================
echo.

:: --- 创建开始菜单快捷方式 ---
set "START_MENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%"
if not exist "%START_MENU%" mkdir "%START_MENU%"
if exist "%EXE_PATH%" (
    mshta "javascript:var sh=new ActiveXObject('WScript.Shell');var lnk=sh.CreateShortcut('%START_MENU%\\%APP_NAME%.lnk');lnk.TargetPath='%EXE_PATH%';lnk.WorkingDirectory='%APP_DIR%';lnk.Description='工程助手 LDAssistant — 标准规范智能识别工具';lnk.Save();close();" 2>nul
    echo [✓] 开始菜单快捷方式已创建
)

:: --- 创建桌面快捷方式 ---
set "DESKTOP=%USERPROFILE%\Desktop"
if exist "%DESKTOP%" (
    mshta "javascript:var sh=new ActiveXObject('WScript.Shell');var lnk=sh.CreateShortcut('%DESKTOP%\\%APP_NAME%.lnk');lnk.TargetPath='%EXE_PATH%';lnk.WorkingDirectory='%APP_DIR%';lnk.Description='工程助手 LDAssistant — 标准规范智能识别工具';lnk.Save();close();" 2>nul
    echo [✓] 桌面快捷方式已创建
)

:: --- 添加卸载信息到注册表 ---
set "UNINSTALL_KEY=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\%APP_NAME%"
reg add "%UNINSTALL_KEY%" /v "DisplayName" /t REG_SZ /d "%APP_NAME% v10" /f 2>nul
reg add "%UNINSTALL_KEY%" /v "DisplayIcon" /t REG_SZ /d "%EXE_PATH%" /f 2>nul
reg add "%UNINSTALL_KEY%" /v "InstallLocation" /t REG_SZ /d "%APP_DIR%" /f 2>nul
reg add "%UNINSTALL_KEY%" /v "DisplayVersion" /t REG_SZ /d "10.0" /f 2>nul
reg add "%UNINSTALL_KEY%" /v "Publisher" /t REG_SZ /d "LDAssistant Team" /f 2>nul
reg add "%UNINSTALL_KEY%" /v "UninstallString" /t REG_SZ /d "cmd /c rmdir /s /q \"%APP_DIR%\"" /f 2>nul
echo [✓] 卸载信息已注册

echo.
echo ========================================
echo   安装完成！
echo   工程助手 已安装到：
echo   %APP_DIR%
echo ========================================
echo.
echo 即将启动程序...
ping -n 2 127.0.0.1 >nul

:: --- 启动程序 ---
start "" "%EXE_PATH%"

exit /b 0
