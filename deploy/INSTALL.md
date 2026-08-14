# RevitGraphPlugin — Installation

Pre-built package: no compiler, no Python, no repository checkout needed. The Revit
version this package targets is recorded in `revit-version.txt` (e.g. `2026`).

## Prerequisites

- **Autodesk Revit** matching `revit-version.txt`
- **Neo4j 5.x** running locally (Neo4j Desktop, or any install) — the plugin mirrors
  the Revit model into it. No existing data is needed; the plugin writes everything.

## Steps

### 1. Start Neo4j

Create/start a local database instance so it is reachable at `bolt://127.0.0.1:7687`
with user `neo4j`. Two options for the password:

- set the database password to `password` (the plugin's default), **or**
- keep your own password and store it for Revit to inherit (PowerShell):

  ```powershell
  [Environment]::SetEnvironmentVariable("NEO4J_LOCAL_PASSWORD", "<your-password>", "User")
  ```

### 2. Install the add-in

From this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This copies the DLLs + `.addin` manifest to
`%AppData%\Autodesk\Revit\Addins\<version>\` — per-user, no admin rights required.

### 3. Run

1. Start Revit. On first launch a security dialog asks about the unsigned add-in —
   choose **Always Load**.
2. Open or create an **architectural project** (a project with levels).
3. Ribbon tab **RevitGraphPlugin** → click **Live Sync**. The button flips to ON,
   the baseline graph is written, and every model change flows to Neo4j as a
   transformation rule. Click again to turn it off.

Inspect the result in Neo4j Browser (`http://localhost:7474`), e.g.:

```cypher
MATCH (n {timestamp: 'plugin-live'}) RETURN n LIMIT 100
```

## Version checkout (optional)

Every change made while Live Sync is ON is stored as a transformation rule in a
versioned chain. `checkout.ps1` (included) moves the graph to any recorded version —
undo backwards, replay forwards. Turn Live Sync OFF first, then from this folder:

```powershell
.\checkout.ps1            # list the chain + where the graph currently stands
.\checkout.ps1 3          # move the graph to the state after chain member seq 3
.\checkout.ps1 head       # back to the newest version
```

This needs only the running Neo4j — no Python.

### IFC round-trip (optional, needs ConMan2)

`.\checkout.ps1 3 -Ifc v3.ifc` additionally exports the checked-out graph as an IFC
file via [ConMan2](https://github.com/seb-esser/ConMan2). Requires a ConMan2 clone
with its Python venv set up (`pip install -r src/requirements.txt`), then:

```powershell
$env:CONMAN2_PATH  = "<ConMan2>\src"
$env:PLUGIN_PYTHON = "<ConMan2>\venv\Scripts\python.exe"
```

## Troubleshooting

| Symptom                              | Fix                                                                   |
| ------------------------------------ | --------------------------------------------------------------------- |
| No `RevitGraphPlugin` ribbon tab     | Files not in `%AppData%\Autodesk\Revit\Addins\<version>\` — rerun step 2; check the version matches your Revit. |
| Live Sync reports connection failure | Neo4j not running, or wrong password — see step 1. Env var changes require restarting Revit. |
| `Unauthorized` from Neo4j            | `NEO4J_LOCAL_PASSWORD` is set but differs from the database password.  |

## Uninstall

Delete `RevitGraphPlugin.*` and the accompanying DLLs from
`%AppData%\Autodesk\Revit\Addins\<version>\`.

---

Source: https://github.com/Peilingll/RevitGraphPlugin (see its README for building from
source and for the ConMan2/Python bridge, which this package does not include).
