@echo off
chcp 65001 >nul
title Pico wake environment
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0wake-env.ps1" %*
echo.
pause
