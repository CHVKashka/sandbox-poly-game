@echo off
REM Builds the C# project, then runs the built-in self-tests headless (see Docs\04-testing.md).
REM Exit code 0 = all tests passed. Override the engine location with GODOT_SRC (see run.bat).
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
