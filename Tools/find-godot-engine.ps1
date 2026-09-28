<#
  Locates the custom double-precision Godot engine build on THIS machine and prints its root
  folder (the folder that contains bin\godot.windows.editor.double.x86_64.mono.exe) to stdout.
  Used by build.bat/run.bat/test.bat/edit.bat/export.bat so nobody has to hardcode a path or set
  GODOT_SRC by hand on every new device.

  Search order (first hit wins):
    1. $env:GODOT_SRC, if it actually contains the engine.
    2. .godot-engine-path.txt in the repo root, cached from a previous successful search.
    3. A short list of common install locations.
    4. A full scan of local fixed drives (slow - only runs once, result gets cached for next time).

  On success: prints the engine root to stdout and exits 0.
  On failure: prints nothing to stdout (so callers capturing it get an empty string),
  writes a diagnostic to stderr, and exits 1.

  (Script is ASCII-only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without BOM.)
#>

$ErrorActionPreference = "Stop"
$marker = "bin\godot.windows.editor.double.x86_64.mono.exe"
$repo = Split-Path -Parent $PSScriptRoot
$cacheFile = Join-Path $repo ".godot-engine-path.txt"

function Test-EngineRoot([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    return Test-Path (Join-Path $root $marker)
}

function Save-Cache([string]$root) {
    try { [System.IO.File]::WriteAllText($cacheFile, $root, (New-Object System.Text.UTF8Encoding($false))) } catch {}
}

# 1) Explicit override via environment variable
if ($env:GODOT_SRC) {
    if (Test-EngineRoot $env:GODOT_SRC) {
        Save-Cache (Resolve-Path $env:GODOT_SRC).Path
        Write-Output (Resolve-Path $env:GODOT_SRC).Path
        exit 0
    }
    Write-Error "GODOT_SRC is set to '$env:GODOT_SRC' but $marker was not found there - ignoring it and searching elsewhere." -ErrorAction Continue
}

# 2) Cached path from a previous successful search on this machine
if (Test-Path $cacheFile) {
    $cached = (Get-Content $cacheFile -Raw -ErrorAction SilentlyContinue)
    if ($cached) { $cached = $cached.Trim() }
    if (Test-EngineRoot $cached) {
        Write-Output $cached
        exit 0
    }
}

# 3) Common install locations
$candidates = @(
    "C:\Godot\godot-src", "D:\Godot\godot-src", "E:\Godot\godot-src",
    "C:\godot-src", "D:\godot-src",
    "$env:USERPROFILE\Godot\godot-src", "$env:USERPROFILE\godot-src",
    "C:\Programs\Godot-4.6.3-double", "D:\Programs\Godot-4.6.3-double",
    "C:\dev\godot-src", "D:\dev\godot-src",
    "C:\Godot\godot", "D:\Godot\godot"
)
foreach ($c in $candidates) {
    if (Test-EngineRoot $c) {
        Save-Cache $c
        Write-Output $c
        exit 0
    }
}

# 4) Full scan of local fixed drives - last resort, can take a while on a large disk
Write-Error "Engine not found via GODOT_SRC, cache, or common locations - scanning local drives (this can take a minute)..." -ErrorAction Continue
$drives = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq "Fixed" -and $_.IsReady }
foreach ($d in $drives) {
    $hit = $null
    try {
        $hit = Get-ChildItem -Path $d.RootDirectory.FullName -Filter "godot.windows.editor.double.x86_64.mono.exe" `
                   -Recurse -File -ErrorAction SilentlyContinue -Force |
               Where-Object { $_.DirectoryName -match '\\bin$' } |
               Select-Object -First 1
    } catch {}
    if ($hit) {
        $root = Split-Path $hit.DirectoryName -Parent
        Save-Cache $root
        Write-Output $root
        exit 0
    }
}

Write-Error "Could not find the custom double-precision Godot engine anywhere on this machine. Build it per Docs\01-engine-build.md, then either set GODOT_SRC to its source folder or place it at C:\Godot\godot-src." -ErrorAction Continue
exit 1
