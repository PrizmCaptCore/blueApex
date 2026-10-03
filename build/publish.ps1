# Builds the release: a self-contained win-x64 publish of BlueApex, then the installer.
#   .\build\publish.ps1            -> build\out\app\  and  build\out\BlueApex-Setup-<version>.exe
#   .\build\publish.ps1 -NoInstaller
# Needs the .NET 9 SDK; the installer step needs Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([switch]$NoInstaller)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\BlueApex\BlueApex.csproj"
$out = Join-Path $PSScriptRoot "out"
$app = Join-Path $out "app"

# The version lives in the csproj; everything else derives from it.
[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "BlueApex $version"

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $app `
    -p:PublishSingleFile=false -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The samples' plugin folders are not shipped; users drop widgets into %AppData%\BlueApex\widgets.
Copy-Item (Join-Path $root "LICENSE") $app
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $app
$size = [math]::Round((Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "published to $app ($size MB)"

if ($NoInstaller) { return }

# winget installs Inno Setup per user; an admin install lands in Program Files (x86).
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }
& $iscc "/DAppVersion=$version" "/DAppDir=$app" "/DOutDir=$out" (Join-Path $PSScriptRoot "BlueApex.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Write-Host "installer: $out\BlueApex-Setup-$version.exe"
