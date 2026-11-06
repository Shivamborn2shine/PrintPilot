@echo off
title PrintAway - New PC & Printer Setup Wizard
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-station.ps1
pause

