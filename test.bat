@echo off
REM Builds the C# project, then runs the built-in self-tests headless (see Docs\04-testing.md).
REM Exit code 0 = all tests passed. The engine is located automatically (see Tools\find-godot-engine.ps1).
REM To force a specific engine, set GODOT_SRC before running (see run.bat).
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
    echo Build failed - not running tests.
    exit /b 1
)

echo.
echo Running self-tests ...
"%EDITOR%" --headless --path . -- --selftest
set "RESULT=%errorlevel%"
echo.
if "%RESULT%"=="0" (
    echo Self-tests: ALL PASSED
) else (
    echo Self-tests: FAILED (exit code %RESULT%)
)
exit /b %RESULT%
