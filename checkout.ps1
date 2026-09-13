# checkout.ps1: move the current-state graph to any version recorded in the :Rule chain.
#
#   .\checkout.ps1                  list the chain and the current position
#   .\checkout.ps1 head             newest version (replay the whole chain)
#   .\checkout.ps1 3                the state after chain member seq 3
#   .\checkout.ps1 3 -Ifc v3.ifc    ...and export that version as IFC
#
# Wraps the rulechain CLI (tools/RuleChainCli in the repo, rulechain\ in the
# distributed zip). Requires a local Neo4j (NEO4J_LOCAL_*). Run with Live Sync OFF.

param(
    [Parameter(Position = 0)] [string]$Version = 'list',
    [string]$Target = 'plugin-live',
    [string]$Ifc
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

# -- locate the CLI: built exe in the zip, else dotnet run from the repo ----------
$exe = Join-Path $repo 'rulechain\rulechain.exe'
if (Test-Path $exe) {
    $cli = { & $exe @args }
}
elseif (Test-Path (Join-Path $repo 'tools\RuleChainCli\RuleChainCli.csproj')) {
    $proj = Join-Path $repo 'tools\RuleChainCli\RuleChainCli.csproj'
    $cli = { dotnet run --project $proj -c Release -v q -- @args }
}
else {
    throw "rulechain CLI not found (expected rulechain\rulechain.exe or tools\RuleChainCli)"
}

# -- list or move ------------------------------------------------------------------
if ($Version -eq 'list') {
    & $cli list --target $Target
    if ($LASTEXITCODE -ne 0) { throw "rulechain failed" }
    return
}

& $cli checkout $Version --target $Target
if ($LASTEXITCODE -ne 0) { throw "checkout failed" }

# -- optional IFC export -----------------------------------------------------------
# A bare filename lands in data\out (ignored by git); a path is used as given.
if ($Ifc) {
    if (-not [System.IO.Path]::IsPathRooted($Ifc) -and $Ifc -notmatch '[\\/]') {
        $outDir = Join-Path $repo 'data\out'
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $Ifc = Join-Path $outDir $Ifc
    }
    # PLUGIN_PYTHON override, else the ConMan2 venv beside this repo (same convention
    # as the C# bridge, IfcSnippetSink).
    $py = if ($env:PLUGIN_PYTHON) { $env:PLUGIN_PYTHON }
          else { Join-Path (Split-Path -Parent $repo) 'ConMan2\venv\Scripts\python.exe' }
    if (-not (Test-Path $py)) {
        throw "python not found at '$py'. Set PLUGIN_PYTHON to a ConMan2 venv python " +
              "(and CONMAN2_PATH to ConMan2's src/), or clone ConMan2 beside this repo."
    }
    # graph2ifc.py lives in tools\python\ in the repo, or next to this script in the zip.
    $g2i = Join-Path $repo 'tools\python\graph2ifc.py'
    if (-not (Test-Path $g2i)) { $g2i = Join-Path $repo 'graph2ifc.py' }
    if (-not (Test-Path $g2i)) { throw "graph2ifc.py not found next to checkout.ps1" }
    & $py $g2i $Target $Ifc
}
