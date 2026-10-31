@echo off
chcp 65001 >nul
title VBA-M 網際網路連線
rem 用法：把 ROM 拖曳到這個檔案上（或直接雙擊，稍後在 VBA-M 開啟遊戲）
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Online.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Online.ps1" -Rom "%~1"
)
echo.
pause
