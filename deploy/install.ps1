# install.ps1 — copy RevitGraphPlugin into the current user's Revit Addins folder.
#
#   .\install.ps1              install for the Revit version this package was built for
#   .\install.ps1 -RevitVersion 2026   override the target version folder
#
# No admin rights needed (installs per-user). See INSTALL.md for the full steps.

param([string]$RevitVersion)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $RevitVersion) {
    $vf = Join-Path $here 'revit-version.txt'
    if (-not (Test-Path $vf)) { throw 'revit-version.txt missing — pass -RevitVersion explicitly' }
    $RevitVersion = (Get-Content $vf -TotalCount 1).Trim()
}

$target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $here '*.dll') $target -Force
Copy-Item (Join-Path $here 'RevitGraphPlugin.addin') $target -Force

Write-Host "RevitGraphPlugin installed to $target"
Write-Host ''
Write-Host 'Before starting Revit, make sure a local Neo4j is running:'
Write-Host '  bolt://127.0.0.1:7687, user "neo4j", password "password"'
Write-Host '  (a different password? set NEO4J_LOCAL_PASSWORD at User scope — see INSTALL.md)'
