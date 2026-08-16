# Builds PwAutoClick.dll (x64, static CRT) from PwAutoClick.cpp using the MSVC toolchain.
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1
#
# The compiled DLL is written to ..\PwAutoClick.dll (one level up, next to the .csproj's Native folder)
# where Hi3Helper.PerfectWorld.Core.csproj embeds it as a resource. Commit the rebuilt DLL alongside the
# source change, exactly as the repo already commits Indexer.exe.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'PwAutoClick.cpp'
$out  = Join-Path (Split-Path -Parent $here) 'PwAutoClick.dll'

# Locate vcvars64.bat via vswhere (works for any VS 2022/2026 edition), with a couple of fallbacks.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = $null
if (Test-Path $vswhere) {
    $vsInstall = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vsInstall) { $vcvars = Join-Path $vsInstall 'VC\Auxiliary\Build\vcvars64.bat' }
}
if (-not $vcvars -or -not (Test-Path $vcvars)) {
    foreach ($p in @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat')) {
        if (Test-Path $p) { $vcvars = $p; break }
    }
}
if (-not $vcvars -or -not (Test-Path $vcvars)) {
    throw "vcvars64.bat not found. Install the MSVC 'Desktop development with C++' workload."
}

$objDir = Join-Path $here 'obj'
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

# vcvars must set env vars and cl/link must run in the SAME cmd.exe process.
$cl = "cl /nologo /std:c++17 /utf-8 /O2 /MT /GS /guard:cf /DNDEBUG /D_UNICODE /DUNICODE /LD " +
      "`"$src`" /Fo:`"$objDir\\`" /Fe:`"$out`" /link /DLL /OPT:REF /OPT:ICF " +
      "kernel32.lib user32.lib advapi32.lib"
$cmd = "call `"$vcvars`" >nul && $cl"

& $env:ComSpec /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

if (Test-Path $out) {
    Write-Host "Built: $out" -ForegroundColor Green
    Get-Item $out | Select-Object FullName, Length, LastWriteTime | Format-List
} else {
    throw "Compilation reported success but $out is missing."
}
