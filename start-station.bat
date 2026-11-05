@echo off
title PrintAway Station & Cloudflare Tunnel
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-tunnel.ps1
pause
