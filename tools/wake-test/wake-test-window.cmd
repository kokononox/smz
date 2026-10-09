@echo off
chcp 65001 >nul
title Pico wake test - WINDOW (real shift window)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wake-test.ps1" window
echo.
pause
