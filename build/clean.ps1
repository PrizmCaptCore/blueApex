# Removes everything a build leaves behind: bin/ and obj/ under every project, and build\out
# (the published app and installers; released ones live on GitHub). Source and settings are untouched.
#   .\build\clean.ps1           -> also deletes build\out
#   .\build\clean.ps1 -KeepOut  -> keeps build\out (e.g. an installer you still want to run)
param([switch]$KeepOut)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$freed = 0
foreach ($dir in Get-ChildItem -Path (Join-Path $root "src"), (Join-Path $root "samples"), (Join-Path $root "tools") -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue) {
    $freed += (Get-ChildItem $dir.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    Remove-Item $dir.FullName -Recurse -Force
    Write-Host "removed $($dir.FullName.Substring($root.Length + 1))"
}
$out = Join-Path $PSScriptRoot "out"
if (-not $KeepOut -and (Test-Path $out)) {
    $freed += (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
    Remove-Item $out -Recurse -Force
    Write-Host "removed build\out"
}
Write-Host ("freed {0} MB" -f [math]::Round($freed / 1MB))
