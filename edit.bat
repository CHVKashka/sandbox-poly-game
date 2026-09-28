@echo off
REM Builds the C# project, then opens the project in the Godot GUI editor (custom double-precision build).
REM The engine is located automatically (see Tools\find-godot-engine.ps1).
REM To force a specific engine, set GODOT_SRC before running (see run.bat).
setlocal enabledelayedexpansion
cd /d "%~dp0"

for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "Tools\find-godot-engine.ps1"`) do set "GODOT_SRC=%%I"
if "%GODOT_SRC%"=="" (
    echo Could not locate the custom double-precision Godot engine on this machine.
    echo Build it per Docs\01-engine-build.md, or set GODOT_SRC to its source folder.
    exit /b 1
)
set "EDITOR=%GODOT_SRC%\bin\godot.windows.editor.double.x86_64.mono.exe"

echo Using engine: %GODOT_SRC%
echo Building sandbox-poly-game.csproj ...
dotnet build sandbox-poly-game.csproj
if errorlevel 1 (
    echo.
    echo Build failed - opening the editor anyway, but scripts will not load until you fix and rebuild
    echo ^(from the editor: Alt+B, or re-run build.bat^).
)

echo.
echo Opening editor: %EDITOR%
start "" "%EDITOR%" --path . -e
