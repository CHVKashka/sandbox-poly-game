@echo off
REM Builds the C# project, then runs the game/build-editor (main scene) with the custom double-precision engine.
REM By default looks for the engine in D:\Programs\Godot-4.6.3-double (see Docs\01-engine-build.md).
REM Override by setting GODOT_SRC before running, e.g.:  set GODOT_SRC=D:\Godot\godot-src && run.bat
setlocal
if "%GODOT_SRC%"=="" set "GODOT_SRC=D:\Programs\Godot-4.6.3-double"
set "EDITOR=%GODOT_SRC%\bin\godot.windows.editor.double.x86_64.mono.console.exe"
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
    echo Build failed - not starting.
    exit /b 1
)

echo.
echo Starting %EDITOR% --path .
"%EDITOR%" --path .
exit /b %errorlevel%
