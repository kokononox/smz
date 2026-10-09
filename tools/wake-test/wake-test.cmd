@echo off
chcp 65001 >nul
title Pico wake test - DRY (macro does NOT start)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wake-test.ps1" 
echo.
pause
