<#
.SYNOPSIS
  Repository hygiene for CI: source files are valid UTF-8, and no build output or local
  settings are committed.
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$bad = @()

# Text files must be valid UTF-8 (GMod reads Lua as bytes; a stray ANSI character turns into mojibake).
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
Get-ChildItem $root -Recurse -File -Include *.lua, *.cs, *.xaml, *.md, *.json, *.ps1, *.cpp, *.h |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|build|third_party|dist|artifacts|\.git)\\' } |
    ForEach-Object {
        try { [void]$utf8.GetString([IO.File]::ReadAllBytes($_.FullName)) }
        catch { $bad += "not UTF-8: $($_.FullName)" }
    }

# Nothing generated or personal.
$tracked = git -C $root ls-files
foreach ($f in $tracked) {
    if ($f -match '(^|/)(bin|obj|dist|artifacts)/' -or $f -match '(^|/)settings(\.[^/]+)?\.json$' -or $f -match '\.(user|suo)$') {
        $bad += "should not be committed: $f"
    }
}

if ($bad.Count) {
    $bad | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "repository check passed"
