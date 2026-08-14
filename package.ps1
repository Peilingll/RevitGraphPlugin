# package.ps1 — build a distributable zip of the add-in for a given Revit version.
#
#   .\package.ps1                  package for Revit 2026 (default)
#   .\package.ps1 -RevitVersion 2025
#
# Result: dist\RevitGraphPlugin-<version>.zip — pre-built DLLs + .addin + install.ps1
# + INSTALL.md. The API assemblies come from the local Revit install if one matches,
# otherwise from the Nice3point NuGet packages (so any machine can package any version).

param(
    [string]$RevitVersion = '2026',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── build ────────────────────────────────────────────────────────────────────────
dotnet build (Join-Path $repo 'src\RevitGraphPlugin\RevitGraphPlugin.csproj') `
    -c $Configuration -p:Platform=x64 -p:RevitVersion=$RevitVersion
if ($LASTEXITCODE -ne 0) { throw "build failed (RevitVersion=$RevitVersion)" }

# ── collect ──────────────────────────────────────────────────────────────────────
$out  = Join-Path $repo "src\RevitGraphPlugin\bin\x64\$Configuration"
$dist = Join-Path $repo "dist\RevitGraphPlugin-$RevitVersion"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null

Copy-Item "$out\*.dll" $dist
Copy-Item "$out\RevitGraphPlugin.addin" $dist
Copy-Item (Join-Path $repo 'deploy\install.ps1') $dist
Copy-Item (Join-Path $repo 'deploy\INSTALL.md') $dist
# Version-checkout tooling: checkout/undo/replay needs only Neo4j; the optional
# -Ifc round-trip additionally needs a ConMan2 clone (see INSTALL.md).
Copy-Item (Join-Path $repo 'checkout.ps1') $dist
Copy-Item (Join-Path $repo 'tools\python\graph2ifc.py') $dist
Set-Content (Join-Path $dist 'revit-version.txt') $RevitVersion

# Safety net: the Revit API is provided by Revit at runtime and must never ship.
Get-ChildItem $dist -Filter 'RevitAPI*.dll' | Remove-Item -Force

# ── zip ──────────────────────────────────────────────────────────────────────────
$zip = "$dist.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$dist\*" -DestinationPath $zip

Write-Host "`npackaged: $zip"
Get-ChildItem $dist | ForEach-Object { Write-Host "  $($_.Name)" }
