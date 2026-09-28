@echo off
REM Builds the C# project, then runs the game/build-editor (main scene) with the custom double-precision engine.
REM The engine is located automatically (see Tools\find-godot-engine.ps1) - no path to edit here.
REM To force a specific engine, set GODOT_SRC before running, e.g.:  set GODOT_SRC=D:\Godot\godot-src && run.bat
setlocal enabledelayedexpansion
cd /d "%~dp0"

for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\find-godot-engine.ps1"`) do set "GODOT_SRC=%%I"
if "%GODOT_SRC%"=="" (
    echo Could not locate the custom double-precision Godot engine on this machine.
    echo Build it per Docs\01-engine-build.md, or set GODOT_SRC to its source folder.
    exit /b 1
)
set "EDITOR=%GODOT_SRC%\bin\godot.windows.editor.double.x86_64.mono.console.exe"

echo Using engine: %GODOT_SRC%
echo Building sandbox-poly-game.csproj ...
dotnet build sandbox-poly-game.csproj
if errorlevel 1 (
    echo.
    echo Build failed - not starting.
    exit /b 1
)

echo.
echo Starting %EDITOR% --path .
"%EDITOR%" --path .
exit /b %errorlevel%
