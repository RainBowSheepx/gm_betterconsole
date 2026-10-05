<#
.SYNOPSIS
  Builds everything and packs the release files into dist\:

    BetterConsole-<ver>-win-x64.zip           self-contained, no .NET install needed (recommended)
    BetterConsole-<ver>-win-x64-small.zip     needs the .NET 8 Desktop Runtime
    betterconsole-addon-<ver>.zip             the companion addon + modules for a manual install
    SHA256SUMS.txt

.PARAMETER Version
  Version for the app, the SDK and the module, e.g. 0.1.0 (default: from Directory.Build.props).

.PARAMETER SkipNative
  Use the native modules already in native\build (CI builds them in a separate step).

.EXAMPLE
  .\scripts\package.ps1 -Version 0.2.0
#>
param(
    [string]$Version = "",
    [switch]$SkipNative,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $m = [regex]::Match((Get-Content (Join-Path $root "Directory.Build.props") -Raw), '<Version[^>]*>([^<]+)</Version>')
    $Version = $m.Groups[1].Value
}
$Version = $Version.TrimStart("v")
Write-Host "== BetterConsole $Version" -ForegroundColor Cyan

$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# 1. native module, both bitnesses
if (-not $SkipNative) {
    & (Join-Path $PSScriptRoot "build-native.ps1") -Arch both -Version $Version
}
foreach ($dll in "x64\bin\gmsv_betterconsole_win64.dll", "x86\bin\gmsv_betterconsole_win32.dll") {
    if (-not (Test-Path (Join-Path $root "native\build\$dll"))) { throw "missing native\build\$dll" }
}

# 2. tests
if (-not $SkipTests) {
    Invoke-Checked "tests" { dotnet test tests\BetterConsole.Tests -c Release -nologo -v q }
}

# 3. the app: self-contained single file, and a small framework-dependent one
$common = @("src\BetterConsole.App\BetterConsole.App.csproj", "-c", "Release", "-r", "win-x64", "-nologo", "-v", "q",
    "-p:Version=$Version", "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:GenerateDocumentationFile=false")
$big = Join-Path $root "artifacts\app"
$small = Join-Path $root "artifacts\app-small"
Invoke-Checked "publish (self-contained)" {
    dotnet publish @common --self-contained true "-p:IncludeNativeLibrariesForSelfExtract=true" "-p:EnableCompressionInSingleFile=true" -o $big
}
Invoke-Checked "publish (framework-dependent)" { dotnet publish @common --self-contained false -o $small }

# 4. sample plugin
$pluginOut = Join-Path $root "artifacts\plugin"
Invoke-Checked "sample plugin" {
    dotnet build samples\QuickCommandsPlugin\QuickCommandsPlugin.csproj -c Release -nologo -v q "-p:Version=$Version" -o $pluginOut
}

function New-AppFolder([string]$publishDir, [string]$name) {
    $dir = Join-Path $dist $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $publishDir "BetterConsole.exe") $dir
    # framework-dependent publish keeps a few side files next to the exe
    Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne "BetterConsole.exe" -and $_.Extension -in ".dll", ".json" } | ForEach-Object { Copy-Item $_.FullName $dir }
    New-Item -ItemType Directory -Force (Join-Path $dir "plugins\QuickCommands") | Out-Null
    Copy-Item (Join-Path $pluginOut "QuickCommands.dll") (Join-Path $dir "plugins\QuickCommands\")
    New-Item -ItemType Directory -Force (Join-Path $dir "themes") | Out-Null
    Copy-Item (Join-Path $root "samples\themes\*.json") (Join-Path $dir "themes\")
    New-Item -ItemType Directory -Force (Join-Path $dir "examples") | Out-Null
    Copy-Item (Join-Path $root "samples\lua\betterconsole_example") (Join-Path $dir "examples\") -Recurse
    Copy-Item (Join-Path $root "LICENSE") $dir
    Set-Content (Join-Path $dir "README.txt") -Encoding utf8 -Value @(
        "BetterConsole $Version - console for Garry's Mod dedicated servers",
        "",
        "1. Start BetterConsole.exe.",
        "2. Settings: choose the folder of your server (the one with srcds.exe), or import your start.bat.",
        "3. Press Start.",
        "",
        "The companion addon is installed into the server automatically (Settings -> Server).",
        "examples\betterconsole_example: a server addon that adds its own tab (copy it into garrysmod\addons).",
        "plugins\QuickCommands: a sample plugin. themes: your own colour themes.",
        "",
        "Documentation: https://github.com/RainBowSheepx/gm_betterconsole"
    )
    $zip = Join-Path $dist "$name.zip"
    Compress-Archive -Path (Join-Path $dir "*") -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $dir -Recurse -Force
    return $zip
}

$zips = @()
$zips += New-AppFolder $big "BetterConsole-$Version-win-x64"
$zips += New-AppFolder $small "BetterConsole-$Version-win-x64-small"

# 5. the companion addon for a manual install (same layout as garrysmod\)
$addon = Join-Path $dist "addon"
New-Item -ItemType Directory -Force (Join-Path $addon "garrysmod\addons"), (Join-Path $addon "garrysmod\lua\bin") | Out-Null
Copy-Item (Join-Path $root "addon\betterconsole") (Join-Path $addon "garrysmod\addons\") -Recurse
Copy-Item (Join-Path $root "native\build\x64\bin\gmsv_betterconsole_win64.dll") (Join-Path $addon "garrysmod\lua\bin\")
Copy-Item (Join-Path $root "native\build\x86\bin\gmsv_betterconsole_win32.dll") (Join-Path $addon "garrysmod\lua\bin\")
$addonZip = Join-Path $dist "betterconsole-addon-$Version.zip"
Compress-Archive -Path (Join-Path $addon "*") -DestinationPath $addonZip -CompressionLevel Optimal
Remove-Item $addon -Recurse -Force
$zips += $addonZip

# 6. checksums
$sums = foreach ($z in $zips) { "{0}  {1}" -f (Get-FileHash $z -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $z -Leaf) }
Set-Content (Join-Path $dist "SHA256SUMS.txt") -Value $sums -Encoding ascii

Write-Host "== done:" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { "  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
