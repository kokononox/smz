@echo off
chcp 65001 >nul
title Pico wake test - FULL (macro starts)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wake-test.ps1" full
echo.
pause
