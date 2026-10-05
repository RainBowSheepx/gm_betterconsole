<#
.SYNOPSIS
  Builds gmsv_betterconsole_win32.dll and gmsv_betterconsole_win64.dll with Visual Studio 2022
  (CMake + Ninja that ship with Visual Studio).

.PARAMETER Arch
  x64, x86 or both (default).

.PARAMETER Version
  Version string compiled into the module (default 0.1.0).

.EXAMPLE
  .\scripts\build-native.ps1
  .\scripts\build-native.ps1 -Arch x64
#>
param(
    [ValidateSet("x64", "x86", "both")] [string]$Arch = "both",
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root "native"

if (-not (Test-Path (Join-Path $native "third_party\gmod-module-base\include"))) {
    throw "native\third_party\gmod-module-base is empty. Run: git submodule update --init"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found: install Visual Studio 2022 with 'Desktop development with C++'." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio with the C++ toolset was found." }

$cmake = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = "cmake" }
$ninja = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
if (-not (Test-Path $ninja)) { $ninja = "ninja" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"
# vcvarsall looks for vswhere.exe on PATH and complains on stderr when it is not there.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
$ErrorActionPreference = "Continue"

# CMake wants a plain x.y.z (no "-dev" suffix).
$Version = ($Version.TrimStart("v") -split '[-+]')[0]

$archs = if ($Arch -eq "both") { @("x64", "x86") } else { @($Arch) }
foreach ($a in $archs) {
    $build = Join-Path $native "build\$a"
    Write-Host "== native $a -> $build" -ForegroundColor Cyan
    $cmd = "`"$vcvars`" $a >nul && `"$cmake`" -S `"$native`" -B `"$build`" -G Ninja -DCMAKE_MAKE_PROGRAM=`"$ninja`" -DCMAKE_BUILD_TYPE=Release -DBC_VERSION=$Version && `"$cmake`" --build `"$build`""
    & cmd.exe /d /s /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "native build failed for $a" }
}

Get-ChildItem -Path (Join-Path $native "build") -Recurse -Filter "gmsv_betterconsole_*.dll" | ForEach-Object { Write-Host "  $($_.FullName)" }
