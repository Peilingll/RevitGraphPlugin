# RevitGraphPlugin

A Revit 2025 add-in that translates native Revit elements into IFC entities and persists them as a Neo4j property graph following the [ConMan2](https://github.com/seb-esser/ConMan2/) schema, enabling change-tracking and version-diff workflows.

**Current state:** the empty-project IFC boilerplate (spatial breakdown, units, geometric contexts, OwnerHistory chain, default property sets) is written to Neo4j and aligns with the ConMan2 baseline — 25 of 26 entity types and all 95 relationships match (`data/samples/cypher/00_empty_*`). Per-element subgraphs (Wall, Window, …) are the next stage.

## Architecture — hybrid C# / Python

The pipeline is split at the IFC STEP text boundary. The .NET side does what only .NET can (read Revit, build an IFC tree with GeometryGym); the Python side does what it does best (parse IFC with ifcopenshell, write Neo4j via ConMan2). The handoff is an ISO-10303-21 STEP file — an international standard, lossless to pass.

```
 ┌── C# (in Revit process) ──────────────────┐   ┌── Python (subprocess) ───────────────┐
 │ Revit Document                             │   │                                       │
 │   → BoilerplateBuilder → ggifc tree        │   │                                       │
 │   → db.WriteFile() → temp .ifc (STEP) ─────┼───┼─► ifcopenshell parse                  │
 │                                            │   │   → ConMan2 IfcGraphInterface         │
 │                                            │   │   → Neo4j (nodes + edges)             │
 └────────────────────────────────────────────┘   └───────────────────────────────────────┘
```

Why this split: the Revit API is .NET-only, ifcopenshell has no usable C# binding, and reusing ConMan2's own Python importer guarantees the graph schema matches the baseline with zero drift. See [`doc/log/2026-05-31_hybrid-architecture.md`](doc/log/2026-05-31_hybrid-architecture.md) for the full rationale.

### Pipeline stages

| Stage | Script | Input | Output |
| ----- | ------ | ----- | ------ |
| 1 | `BoilerplateBuilder.Build()` | Revit `Document` (ProjectInformation + Levels) | `DatabaseIfc` (in-memory ggifc tree) |
| 2 | `IfcSnippetSink.Run()` | `DatabaseIfc` | temp `.ifc` STEP file + spawned `python.exe` |
| 3 | `snippet_to_cypher.py` | `.ifc` path + `--action` + `--timestamp` | Neo4j graph (via ConMan2) |

Neo4j is written **only** by the Python side (ConMan2's `Neo4jConnection`); the C# add-in no longer opens a Neo4j driver.

## Quick start

Prerequisites: Revit 2025, .NET 8 SDK, Neo4j Desktop (running 5.x instance).

**Clone ConMan2 as a sibling of this repo** — the plugin resolves it by relative path, so no
path configuration is needed when this layout is used:

```
<parent>/
├── RevitGraphPlugin/   (this repo)
└── ConMan2/            git clone https://github.com/seb-esser/ConMan2
```

```powershell
# 1. Set up ConMan2 (sibling clone) and its Python environment.
git clone https://github.com/seb-esser/ConMan2 ..\ConMan2
py -m venv ..\ConMan2\venv
..\ConMan2\venv\Scripts\pip install -r ..\ConMan2\src\requirements.txt

# 2. Set the Neo4j password (User scope, so Revit launched from the Start menu inherits it).
[Environment]::SetEnvironmentVariable("NEO4J_LOCAL_PASSWORD", "<your-password>", "User")

# 3. Build. The Debug build auto-deploys the DLL + .addin to
#    %AppData%\Autodesk\Revit\Addins\2025\.
dotnet restore
dotnet build RevitGraphPlugin.sln -c Debug
```

If ConMan2 lives elsewhere, set `CONMAN2_PATH` and `PLUGIN_PYTHON` to override the defaults
(see [Environment variables](#environment-variables)).

Then start a Neo4j instance, launch Revit 2025 from the Start menu, open or create an Architectural project, and click **Sync current doc** on the `RevitGraphPlugin` ribbon. A TaskDialog reports the temp IFC path and the Python script's output.

## Stack

| Component   | Version / Detail                                |
| ----------- | ----------------------------------------------- |
| Host        | Autodesk Revit 2025                             |
| Runtime     | .NET 8 (x64)                                    |
| IFC library | GeometryGym.Ifc (`GeometryGymIFC` 0.1.22), IFC4 |
| Bridge      | Python + `ifcopenshell` + ConMan2               |
| Graph DB    | Neo4j 5.x (local Neo4j Desktop)                |
| Neo4j write | ConMan2 `IfcGraphInterface` (Python)            |

## Environment variables

The C# add-in and the Python bridge read **different** variables. All path variables default to
the sibling-clone layout (derived by relative path) and only need setting if ConMan2 lives elsewhere.

### C# side (`IfcSnippetSink`) — locating the bridge

| Variable               | Default (sibling-clone layout)                  | Notes                          |
| ---------------------- | ----------------------------------------------- | ------------------------------ |
| `PLUGIN_PYTHON`        | `<repo>/../ConMan2/venv/Scripts/python.exe`     | Python interpreter to spawn    |
| `PLUGIN_SNIPPET_SCRIPT`| `<repo>/tools/python/snippet_to_cypher.py`      | Bridge script path             |

### Python side (`snippet_to_cypher.py` / ConMan2) — schema + Neo4j

| Variable               | Default (sibling-clone layout)  | Notes                                |
| ---------------------- | ------------------------------- | ------------------------------------ |
| `CONMAN2_PATH`         | `<repo>/../ConMan2/src`         | ConMan2 source root (added to `sys.path`) |
| `NEO4J_LOCAL_PASSWORD` | —                               | required                             |
| `NEO4J_LOCAL_USERNAME` | `neo4j`                         |                                      |
| `NEO4J_LOCAL_HOSTNAME` | `localhost`                     |                                      |
| `NEO4J_LOCAL_PORT`     | `7687`                          |                                      |

`tools/Neo4jSmokeTest` reads the same `NEO4J_LOCAL_*` names (falling back to legacy `NEO4J_*`) so it verifies the credentials the live pipeline actually uses.

## How ConMan2 is used

`snippet_to_cypher.py` does **not** copy or reimplement ConMan2 — it imports it:

```python
sys.path.insert(0, conman2_src)   # CONMAN2_PATH, or the sibling-clone default
from ifc_graph_interface.IfcGraphInterface import IfcGraphInterface
IfcGraphInterface().ifc_2_graph(ifc_path, timestamp)   # parse + classify + write Neo4j
```

The full "ifcopenshell parse → classify by ConMan2 schema → write Cypher" path runs inside ConMan2's `ifc_2_graph()`, so the resulting graph matches the baseline by construction. ConMan2 is referenced from a sibling clone (always its latest revision) rather than copied in — keeping it a clean upstream dependency. If the plugin ever needs to be self-contained for distribution, a later milestone could vendor a pinned ConMan2 revision into `tools/python/`.

## Repository layout

```
RevitGraphPlugin/
├── src/RevitGraphPlugin/
│   ├── RevitGraphApp.cs            # IExternalApplication: lifecycle + ribbon
│   ├── SyncCommand.cs              # IExternalCommand: button → BoilerplateBuilder → IfcSnippetSink
│   ├── Ifc/
│   │   ├── BoilerplateBuilder.cs   # Revit Document → in-memory IFC4 boilerplate tree
│   │   ├── RevitOwnerHistory.cs    # Overrides the ggifc OwnerHistory chain to match Revit's exporter
│   │   └── IfcGuidConverter.cs     # Revit UniqueId → IFC GlobalId (22-char base64)
│   ├── Cypher/
│   │   └── IfcSnippetSink.cs       # Serialises ggifc tree to STEP, spawns the Python bridge
│   ├── RevitGraphPlugin.addin      # Revit add-in manifest
│   └── RevitGraphPlugin.csproj     # .NET 8 / x64; references Revit API + GeometryGymIFC
├── tools/
│   ├── python/
│   │   └── snippet_to_cypher.py    # Python bridge: STEP → ifcopenshell → ConMan2 → Neo4j
│   └── Neo4jSmokeTest/             # Standalone console: verifies Neo4j connectivity outside Revit
├── tests/RevitGraphPlugin.Tests/   # xUnit: RevitOwnerHistory regression tests
├── data/samples/                   # IFC/Cypher baseline dataset for diff-driven discovery
│   ├── ifc/                        # IFC4 snapshots (00_empty … 06_deleted_window)
│   ├── cypher/                     # ConMan2 import results (Neo4j query dumps)
│   ├── rvt/                        # Source Revit project for re-export
│   └── README.md                   # what each snapshot represents
├── doc/log/                        # English research logs (per stage)
├── doc_process/                    # Working notes (Traditional Chinese)
├── RevitGraphPlugin.sln
└── README.md
```

## Verify

After clicking **Sync current doc**, inspect the temp IFC reported in the TaskDialog
(`%TEMP%\RevitGraphPlugin_last_sync.ifc`), then check Neo4j Browser:

```cypher
MATCH (n) RETURN n.EntityType AS entity, count(*) AS n ORDER BY n DESC;
```

Compare counts against [`data/samples/cypher/BASELINE.md`](data/samples/cypher/BASELINE.md). The
boilerplate should match the baseline on every entity type except `IfcCartesianPoint` (the plugin
emits 5 vs the baseline's 2 — a known ggifc placement-origin de-duplication difference, not a
structural one; all 95 relationships still match).

## Troubleshooting

| Symptom                                       | Likely cause                                                                                                          |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| Build fails finding `RevitAPI.dll`            | Revit not at `D:\Autodesk\Revit 2025\` — set `$env:RevitInstallPath2025` before `dotnet build`.                      |
| Ribbon tab missing after launch               | Debug build did not deploy (Release skips the deploy target); check `%AppData%\Autodesk\Revit\Addins\2025\`.          |
| TaskDialog "Python interpreter not found"     | `PLUGIN_PYTHON` not set / wrong path — point it at your venv `python.exe`.                                            |
| TaskDialog "ConMan2 source not found"         | `CONMAN2_PATH` not set, or set process-scope only — set at User scope and restart Revit.                             |
| Python exits with a Neo4j auth error          | `NEO4J_LOCAL_PASSWORD` not set / wrong — verify with `dotnet run --project tools/Neo4jSmokeTest`.                     |

## Development environment

1. **Revit 2025** at `D:\Autodesk\Revit 2025\` (override via `RevitInstallPath2025`). Education licence is sufficient.
2. **.NET 8 SDK** — 8.0.403 (pinned by `global.json`).
3. **Neo4j Desktop** with a local 5.x instance reachable via `neo4j://`.
4. **Python** with `ifcopenshell` + `neo4j`, plus a local **ConMan2** checkout.

Add-in registration is automatic for Debug builds: an after-build target copies the DLL, `.addin`
manifest, and NuGet runtime dependencies into `%AppData%\Autodesk\Revit\Addins\2025\`. `RevitAPI` /
`RevitAPIUI` are referenced with `Private=false` — Revit loads them from its own install directory.

## Branches

- `feat/dev` — active development (current hybrid pipeline).
- `archive/v1-mvp` — preserved v1 MVP (pure-C# five-stage pipeline). Reference only; not for merge.
- `main` — rebuild milestones merged here via PR.

## Acknowledgements

This project draws on three open-source predecessors by their respective authors:

- **ConMan2** (Sebastian Esser) — Neo4j graph schema for IFC; reused directly as the Python importer.
- **SpaceTracker** (Sebastian Esser) — Revit `DocumentChanged` integration pattern.
- **IfcInfraToolKit** (TUM CMS) — IFC geometry export via GeometryGym.Ifc.

## Status

Student project (TUM Hiwi). Not for commercial use.
