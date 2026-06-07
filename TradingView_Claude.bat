@echo off
title TradingView + Claude Code Launcher

echo Lancement de TradingView Desktop avec CDP port 9222...
start "" "C:\Program Files\WindowsApps\TradingView.Desktop_3.2.0.7916_x64__n534cwy3pjxzj\TradingView.exe" --remote-debugging-port=9222

timeout /t 2 /nobreak >nul

echo Lancement de Claude Code...
start "" powershell -NoExit -NoLogo -Command "Set-Location 'C:\Users\ASUS\claudeverstradingview'; claude"

echo Tout est lance !
timeout /t 3 /nobreak >nul
