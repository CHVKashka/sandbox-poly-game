<#
  Export the game to a standalone Windows exe (double click, no Godot editor needed).
  (Script is ASCII-only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without BOM.)

  Uses YOUR double-precision export templates from the custom engine build (see Docs/01-engine-build.md).
  The export preset is generated here (export_presets.cfg contains machine-specific paths and is not committed).

  Run from anywhere:
    powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1
    powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1 -GodotSrc D:\Godot\godot-src -Config debug

  Result: Builds\Windows\SW_V2.exe (+ SW_V2.pck and the data_SW_V2_windows_x86_64 folder - keep them together).
#>
param(
    [string]$GodotSrc = $(if ($env:GODOT_SRC) { $env:GODOT_SRC } else { "C:\Godot\godot-src" }),
    [ValidateSet("release", "debug")]
    [string]$Config = "release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $GodotSrc "bin"
$editor = Join-Path $bin "godot.windows.editor.double.x86_64.mono.console.exe"
$templateRelease = Join-Path $bin "godot.windows.template_release.double.x86_64.mono.exe"
$templateDebug = Join-Path $bin "godot.windows.template_debug.double.x86_64.mono.exe"

foreach ($path in @($editor, $templateRelease, $templateDebug)) {
    if (-not (Test-Path $path)) { throw "Engine file not found: $path (check -GodotSrc and the build from Docs/01-engine-build.md)" }
}

$outDir = Join-Path $repo "Builds\Windows"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outExe = Join-Path $outDir "SW_V2.exe"

function ToGodotPath([string]$p) { return $p.Replace("\", "/") }

$preset = @"
[preset.0]

name="Windows Desktop"
platform="Windows Desktop"
runnable=true
advanced_options=false
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter="Docs/*, Tools/*, Builds/*"
export_path="Builds/Windows/SW_V2.exe"
patches=PackedStringArray()
encryption_include_filters=""
encryption_exclude_filters=""
seed=0
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.0.options]

custom_template/debug="$(ToGodotPath $templateDebug)"
custom_template/release="$(ToGodotPath $templateRelease)"
debug/export_console_wrapper=0
binary_format/embed_pck=false
texture_format/s3tc_bptc=true
texture_format/etc2_astc=false
binary_format/architecture="x86_64"
codesign/enable=false
application/modify_resources=false
application/product_name="SW_V2"
application/file_description="SW_V2"
dotnet/include_scripts_content=false
dotnet/include_debug_symbols=true
dotnet/embed_build_outputs=false
"@

# UTF-8 WITHOUT BOM and LF endings: Godot's config parser does not accept a BOM in front of "[preset.0]".
$presetText = ($preset -replace "`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText((Join-Path $repo "export_presets.cfg"), $presetText, (New-Object System.Text.UTF8Encoding($false)))

$flag = if ($Config -eq "debug") { "--export-debug" } else { "--export-release" }
Write-Host "Exporting ($Config) -> $outExe"
Push-Location $repo
try {
    & $editor --headless --path $repo $flag "Windows Desktop" $outExe
    if ($LASTEXITCODE -ne 0) { throw "Export failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $outExe)) { throw "Export did not produce $outExe" }
Write-Host "Done: $outExe"
