@echo off
REM Builds the C# project (sandbox-poly-game.csproj) against the custom double-precision Godot packages.
REM See Docs\01-engine-build.md / Docs\02-project-setup.md for the one-time engine/NuGet setup.
setlocal
cd /d "%~dp0"

echo Building sandbox-poly-game.csproj ...
dotnet build sandbox-poly-game.csproj
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

echo.
echo Build OK.
pause
exit /b 0
