# RevitGraphPlugin

A Revit 2025 add-in that translates Revit elements into IFC entities and persists them as a Neo4j property graph following the [ConMan2](https://github.com/seb-esser/ConMan2/) schema, for change-tracking and version-diff workflows.

**Current state:** the empty-project IFC boilerplate (spatial breakdown, units, geometric contexts, OwnerHistory chain, default property sets) is written to Neo4j and matches the ConMan2 baseline. Per-element subgraphs (Wall, Window, …) are the next stage.

## Architecture — C# / Python

The pipeline splits at the IFC STEP text boundary: C# does what only .NET can (read Revit, build an IFC tree with GeometryGym), Python does what it does best (parse with ifcopenshell, write Neo4j via ConMan2). The handoff is an ISO-10303-21 STEP file — lossless to pass.

```
 ┌── C# (in Revit process) ──────────────────┐   ┌── Python (subprocess) ───────────────┐
 │ Revit Document                             │   │                                       │
 │   → BoilerplateBuilder → ggifc tree        │   │                                       │
 │   → db.WriteFile() → temp .ifc (STEP) ─────┼───┼─► ifcopenshell parse                  │
 │                                            │   │   → ConMan2 IfcGraphInterface         │
 │                                            │   │   → Neo4j (nodes + edges)             │
 └────────────────────────────────────────────┘   └───────────────────────────────────────┘
```

Reusing ConMan2's own importer guarantees the graph schema matches the baseline with zero drift. Neo4j is written **only** by the Python side; the C# add-in no longer opens a Neo4j driver.

| Stage | Script                       | Input                                          | Output                        |
| ----- | ---------------------------- | ---------------------------------------------- | ----------------------------- |
| 1     | `BoilerplateBuilder.Build()` | Revit `Document` (ProjectInformation + Levels) | `DatabaseIfc` (ggifc tree)    |
| 2     | `IfcSnippetSink.Run()`       | `DatabaseIfc`                                  | temp `.ifc` + spawns `python` |
| 3     | `snippet_to_cypher.py`       | `.ifc` path + `--action` + `--timestamp`       | Neo4j graph (via ConMan2)     |

## Quick start

Prerequisites: Revit 2025 (at `D:\Autodesk\Revit 2025\`, override via `RevitInstallPath2025`), .NET 8 SDK (8.0.403, pinned by `global.json`), Neo4j Desktop (running 5.x instance).

**Clone ConMan2 as a sibling of this repo** — paths are then resolved relatively, no configuration needed:

```
<parent>/
├── RevitGraphPlugin/   (this repo)
└── ConMan2/            git clone https://github.com/seb-esser/ConMan2
```

```powershell
# 1. ConMan2 (sibling clone) + its Python environment.
git clone https://github.com/seb-esser/ConMan2 ..\ConMan2
py -m venv ..\ConMan2\venv
..\ConMan2\venv\Scripts\pip install -r ..\ConMan2\src\requirements.txt

# 2. Neo4j password (User scope, so Revit inherits it).
[Environment]::SetEnvironmentVariable("NEO4J_LOCAL_PASSWORD", "<your-password>", "User")

# 3. Build (Debug auto-deploys DLL + .addin to %AppData%\Autodesk\Revit\Addins\2025\).
dotnet build RevitGraphPlugin.sln -c Debug
```

Then start a Neo4j instance, launch Revit, open/create an Architectural project, and click **Sync current doc** on the `RevitGraphPlugin` ribbon. A TaskDialog reports the temp IFC path and the script output.

## Environment variables

Path variables default to the sibling-clone layout (resolved relatively); set them only if ConMan2 lives elsewhere. `NEO4J_LOCAL_PASSWORD` is the only required one.

| Variable                                       | Default                                     | Read by               |
| ---------------------------------------------- | ------------------------------------------- | --------------------- |
| `NEO4J_LOCAL_PASSWORD`                         | — (**required**)                            | Python                |
| `NEO4J_LOCAL_USERNAME` / `_HOSTNAME` / `_PORT` | `neo4j` / `localhost` / `7687`              | Python                |
| `CONMAN2_PATH`                                 | `<repo>/../ConMan2/src`                     | Python                |
| `PLUGIN_PYTHON`                                | `<repo>/../ConMan2/venv/Scripts/python.exe` | C# (`IfcSnippetSink`) |
| `PLUGIN_SNIPPET_SCRIPT`                        | `<repo>/tools/python/snippet_to_cypher.py`  | C# (`IfcSnippetSink`) |

`tools/Neo4jSmokeTest` reads the same `NEO4J_LOCAL_*` names (falling back to legacy `NEO4J_*`) to verify connectivity with `dotnet run --project tools/Neo4jSmokeTest`.

## How ConMan2 is used

`snippet_to_cypher.py` imports ConMan2 (does not copy or reimplement it); the full "parse → classify by schema → write Cypher" path runs inside ConMan2's `ifc_2_graph()`:

```python
sys.path.insert(0, conman2_src)   # CONMAN2_PATH, or the sibling-clone default
from ifc_graph_interface.IfcGraphInterface import IfcGraphInterface
IfcGraphInterface().ifc_2_graph(ifc_path, timestamp)
```

It is referenced from a sibling clone (always upstream's latest), not vendored in. Vendoring a pinned revision into `tools/python/` is a later option if the plugin must be self-contained for distribution.

## Repository layout

```
src/RevitGraphPlugin/
├── RevitGraphApp.cs            # IExternalApplication: lifecycle + ribbon
├── SyncCommand.cs              # button → BoilerplateBuilder → IfcSnippetSink
├── Ifc/
│   ├── BoilerplateBuilder.cs   # Revit Document → in-memory IFC4 boilerplate tree
│   ├── RevitOwnerHistory.cs    # overrides ggifc OwnerHistory to match Revit's exporter
│   └── IfcGuidConverter.cs     # Revit UniqueId → IFC GlobalId
├── Cypher/IfcSnippetSink.cs    # serialises ggifc tree to STEP, spawns the Python bridge
├── RevitGraphPlugin.addin      # Revit add-in manifest
└── RevitGraphPlugin.csproj     # .NET 8 / x64; Revit API + GeometryGymIFC
tools/
├── python/snippet_to_cypher.py # Python bridge: STEP → ifcopenshell → ConMan2 → Neo4j
└── Neo4jSmokeTest/             # standalone Neo4j connectivity check
tests/RevitGraphPlugin.Tests/   # xUnit: RevitOwnerHistory regression tests
data/samples/                   # IFC + Cypher baseline dataset (00_empty … 06_deleted_window)
doc/log/                        # English research logs   ·   doc_process/  working notes (繁中)
```

## Verify

After **Sync current doc**, inspect the temp IFC (`%TEMP%\RevitGraphPlugin_last_sync.ifc`) and check Neo4j Browser:

```cypher
MATCH (n) RETURN n.EntityType AS entity, count(*) AS n ORDER BY n DESC;
```

## Troubleshooting

| Symptom                                  | Likely cause                                                                                    |
| ---------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Build fails finding `RevitAPI.dll`       | Revit not at `D:\Autodesk\Revit 2025\` — set `$env:RevitInstallPath2025` before building.       |
| Ribbon tab missing after launch          | Debug build not deployed (Release skips it) — check `%AppData%\Autodesk\Revit\Addins\2025\`.    |
| "Python interpreter / ConMan2 not found" | ConMan2 not a sibling clone — set `PLUGIN_PYTHON` / `CONMAN2_PATH` (User scope), restart Revit. |
| Python exits with a Neo4j auth error     | `NEO4J_LOCAL_PASSWORD` wrong/unset — verify with `dotnet run --project tools/Neo4jSmokeTest`.   |

## Branches

`feat/dev` active development · `archive/v1-mvp` preserved pure-C# v1 (reference only) · `main` milestones via PR.

## Acknowledgements

Builds on **ConMan2** (Sebastian Esser, reused as the Python importer), **SpaceTracker** (Sebastian Esser, `DocumentChanged` pattern), and **IfcInfraToolKit** (TUM CMS, IFC geometry export). Student project (TUM Hiwi); not for commercial use.
