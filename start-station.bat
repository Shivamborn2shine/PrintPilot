@echo off
title PrintAway Station and Cloudflare Tunnel
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-tunnel.ps1
pause

