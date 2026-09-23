@echo off
REM Exports a standalone Windows build (Builds\Windows\sandbox-poly-game.exe) using your double-precision
REM export templates. Thin wrapper around Tools\export-windows.ps1 - forwards all arguments, e.g.:
REM   export.bat -Config debug
REM   export.bat -GodotSrc D:\Godot\godot-src
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\export-windows.ps1" %*
exit /b %errorlevel%
