@echo off
rem Double-click to build the release: dist\release\Konbini-Setup-<version>.exe and Konbini-<version>-portable.zip
title Konbini - release build
cd /d "%~dp0"
echo Building Konbini release (installer + portable zip)...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see the messages above.
    pause
    exit /b 1
)
echo.
echo Done. Opening dist\release...
start "" explorer "%~dp0dist\release"
pause
