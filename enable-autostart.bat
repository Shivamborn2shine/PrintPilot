@echo off
title PrintAway - Enable Windows Boot Auto-Start
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\enable-autostart.ps1
pause
