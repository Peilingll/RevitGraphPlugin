# checkout.ps1 — move the current-state graph to any version recorded in the :Rule chain.
#
#   .\checkout.ps1                =  .\checkout.ps1 list   (show the chain + where things stand)
#   .\checkout.ps1 head           go to the newest version (replay the whole chain)
#   .\checkout.ps1 3              go to the state AFTER chain member seq 3 was applied
#   .\checkout.ps1 1              seq of a (:Baseline) = the empty/baseline state
#   .\checkout.ps1 3 -Ifc v3.ifc  …then round-trip the result to an IFC file
#
# How it works: the chain node tracks where the graph stands (checked_out_seq; a live
# sync session leaves it at HEAD). Checkout walks from there to the requested seq — undo
# backwards, replay forwards — one rule at a time. Direction matters: a rule's stored
# context refs are anchored on GlobalIds that ggifc regenerates on every re-conversion,
# so a rule applied to a state it was not recorded against may find no anchor at all.
#
# Requirements: local Neo4j (NEO4J_LOCAL_PASSWORD or 'password'), dotnet. Run while the
# live-sync session is OFF — the graph must not be Revit's mirror while we move it.

param(
    [Parameter(Position = 0)] [string]$Version = 'list',
    [string]$Target = 'plugin-live',
    [string]$Ifc
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── Neo4j HTTP helper ────────────────────────────────────────────────────────────
$pw = if ($env:NEO4J_LOCAL_PASSWORD) { $env:NEO4J_LOCAL_PASSWORD } else { 'password' }
$auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("neo4j:$pw"))

function Invoke-Cypher([string]$Statement, [hashtable]$Params = @{}) {
    $body = @{ statements = @(@{ statement = $Statement; parameters = $Params }) } |
        ConvertTo-Json -Depth 8
    $res = Invoke-RestMethod -Uri 'http://127.0.0.1:7474/db/neo4j/tx/commit' -Method Post `
        -Headers @{ Authorization = "Basic $auth" } -ContentType 'application/json' -Body $body
    if ($res.errors.Count -gt 0) { throw "Neo4j: $($res.errors[0].message)" }
    return $res.results[0].data | ForEach-Object { , $_.row }
}

# ── chain state ──────────────────────────────────────────────────────────────────
$members = Invoke-Cypher `
    'MATCH (m) WHERE m.target_ts = $t AND (m:Rule OR m:Baseline)
     RETURN m.seq, (m:Baseline), m.op ORDER BY m.seq' @{ t = $Target }
if (-not $members) { throw "no chain found for target '$Target'" }

$nodeCount = (Invoke-Cypher 'MATCH (n {timestamp: $t}) RETURN count(n)' @{ t = $Target })[0][0]
$headSeqAll = ($members | ForEach-Object { $_[0] } | Measure-Object -Maximum).Maximum
$currentRow = Invoke-Cypher 'MATCH (c:RuleChain {target_ts: $t}) RETURN c.checked_out_seq' @{ t = $Target }
$currentSeq = if ($currentRow -and $null -ne $currentRow[0][0]) { $currentRow[0][0] } else { $headSeqAll }

function Show-Chain {
    Write-Host "`nChain for '$Target'  (graph currently holds $nodeCount nodes, at seq $currentSeq)`n"
    foreach ($m in $members) {
        $kind = if ($m[1]) { 'Baseline' } else { "Rule $($m[2])" }
        $here = if ($m[0] -eq $currentSeq) { ' <- you are here' } else { '' }
        Write-Host ("  seq {0,-3} {1}{2}" -f $m[0], $kind, $here)
    }
    Write-Host "`nUsage: .\checkout.ps1 <seq> | head   [-Ifc out.ifc]`n"
}

if ($Version -eq 'list') { Show-Chain; return }

# ── resolve the requested version ────────────────────────────────────────────────
$headSeq = ($members | ForEach-Object { $_[0] } | Measure-Object -Maximum).Maximum
$newestBaseline = ($members | Where-Object { $_[1] } | ForEach-Object { $_[0] } |
    Measure-Object -Maximum).Maximum

$targetSeq = if ($Version -eq 'head') { $headSeq } else { [long]$Version }
if (-not ($members | Where-Object { $_[0] -eq $targetSeq })) {
    Show-Chain; throw "seq $targetSeq is not on the chain"
}
if ($targetSeq -lt $newestBaseline) {
    throw "cannot check out below the newest baseline anchor (seq $newestBaseline) — the graph was rebuilt there"
}

# ── run the harness (walks from where the graph stands to the target) ────────────
if ($targetSeq -eq $currentSeq) {
    Write-Host "already at seq $targetSeq"
}
else {
    $dir = if ($targetSeq -gt $currentSeq) { 'replaying forward' } else { 'undoing back' }
    Write-Host "$dir from seq $currentSeq to seq $targetSeq..."

    $env:CHAIN_TOOL = 'checkout'; $env:CHAIN_TARGET = $Target; $env:CHAIN_SEQ = "$targetSeq"
    try {
        Push-Location $repo
        $out = dotnet test tests/RevitGraphPlugin.Tests/RevitGraphPlugin.Tests.csproj `
            -c Release --nologo --filter ManualChainTools 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $out; throw "checkout failed (see output above)" }
    }
    finally {
        Pop-Location
        $env:CHAIN_TOOL = $null; $env:CHAIN_TARGET = $null; $env:CHAIN_SEQ = $null
    }
}

$nodeCount = (Invoke-Cypher 'MATCH (n {timestamp: $t}) RETURN count(n)' @{ t = $Target })[0][0]
Write-Host "checked out seq $targetSeq — '$Target' now holds $nodeCount nodes"

# ── optional round trip ──────────────────────────────────────────────────────────
# A bare filename lands in data\samples\ifc\DPO_test (the demo-output convention);
# an absolute or relative path is honored as given.
if ($Ifc) {
    if (-not [System.IO.Path]::IsPathRooted($Ifc) -and $Ifc -notmatch '[\\/]') {
        $outDir = Join-Path $repo 'data\samples\ifc\DPO_test'
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $Ifc = Join-Path $outDir $Ifc
    }
    # PLUGIN_PYTHON override, else sibling-clone default (ConMan2 next to this repo) —
    # same convention as the C# bridge (IfcSnippetSink).
    $py = if ($env:PLUGIN_PYTHON) { $env:PLUGIN_PYTHON }
          else { Join-Path (Split-Path -Parent $repo) 'ConMan2\venv\Scripts\python.exe' }
    if (-not (Test-Path $py)) {
        throw "python not found at '$py' — clone ConMan2 beside this repo or set PLUGIN_PYTHON"
    }
    & $py (Join-Path $repo 'tools\python\graph2ifc.py') $Target $Ifc
}
