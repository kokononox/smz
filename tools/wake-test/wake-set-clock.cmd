@echo off
chcp 65001 >nul
title Pico set clock (no sleep)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wake-test.ps1" clock
echo.
pause
