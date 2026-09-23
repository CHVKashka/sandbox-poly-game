@echo off
REM Builds the C# project, then opens the project in the Godot GUI editor (custom double-precision build).
REM Override the engine location by setting GODOT_SRC before running (see run.bat / Docs\01-engine-build.md).
setlocal
if "%GODOT_SRC%"=="" set "GODOT_SRC=D:\Programs\Godot-4.6.3-double"
set "EDITOR=%GODOT_SRC%\bin\godot.windows.editor.double.x86_64.mono.exe"
cd /d "%~dp0"

if not exist "%EDITOR%" (
    echo Engine not found: %EDITOR%
    echo Set GODOT_SRC to your engine build folder, or build it per Docs\01-engine-build.md
    exit /b 1
)

echo Building sandbox-poly-game.csproj ...
dotnet build sandbox-poly-game.csproj
if errorlevel 1 (
    echo.
    echo Build failed - opening the editor anyway, but scripts will not load until you fix and rebuild
    echo (from the editor: Alt+B, or re-run build.bat).
)

echo.
echo Opening editor: %EDITOR%
start "" "%EDITOR%" --path . -e
