<#
.SYNOPSIS
  Writes the release notes for a tag: its CHANGELOG.md section, how to install, the checksums.
#>
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Output = "notes.md"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$lines = Get-Content (Join-Path $root "CHANGELOG.md") -Encoding utf8
$section = New-Object System.Collections.Generic.List[string]
$inside = $false
foreach ($l in $lines) {
    if ($l -match '^## ') {
        if ($inside) { break }
        if ($l -match "^## \[?$([regex]::Escape($Tag))\]?(\s|$)") { $inside = $true; continue }
    }
    if ($inside) { $section.Add($l) }
}
if ($section.Count -eq 0) { $section.Add("Build of $Tag.") }

$v = $Tag.TrimStart("v")
$notes = @()
$notes += ($section -join "`n").Trim()
$notes += ""
$notes += "### Download"
$notes += ""
$notes += "| File | What it is |"
$notes += "|---|---|"
$notes += "| **BetterConsole-$v-win-x64.zip** | The app, nothing else to install (recommended) |"
$notes += "| BetterConsole-$v-win-x64-small.zip | The same app, small; needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |"
$notes += "| betterconsole-addon-$v.zip | The companion addon and modules, if you prefer to install them yourself (the app does it automatically) |"
$notes += ""
$notes += "Unpack, start ``BetterConsole.exe``, choose your server folder in Settings, press Start. Full guide: [docs/getting-started.md](https://github.com/RainBowSheepx/gm_betterconsole/blob/main/docs/getting-started.md)."
$sums = Join-Path $root "dist\SHA256SUMS.txt"
if (Test-Path $sums) {
    $notes += ""
    $notes += "<details><summary>SHA-256</summary>"
    $notes += ""
    $notes += '```'
    $notes += (Get-Content $sums)
    $notes += '```'
    $notes += "</details>"
}
Set-Content $Output -Value ($notes -join "`n") -Encoding utf8
Write-Host "notes written to $Output"
