@echo off
chcp 65001 >nul
rem 用法：把 1~4 個 ROM 檔拖曳到這個檔案上；只拖一個 ROM 時會開 2 個視窗玩同一個遊戲
if "%~1"=="" (
  echo 請把 ROM 檔拖曳到這個 .bat 上，或執行：
  echo   powershell -ExecutionPolicy Bypass -File "%~dp0Start-Link.ps1" -Rom "遊戲.gba" -Players 2
  pause
  exit /b
)
set ROMS="%~1"
if not "%~2"=="" set ROMS=%ROMS%,"%~2"
if not "%~3"=="" set ROMS=%ROMS%,"%~3"
if not "%~4"=="" set ROMS=%ROMS%,"%~4"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0Start-Link.ps1' -Rom %ROMS%"
