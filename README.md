# RevitGraphPlugin

A Revit 2025 add-in that translates native Revit elements into IFC entities and persists them as a Neo4j property graph, enabling round-trip and version-diff workflows.

**Current state:** environment + Neo4j-connection skeleton. The IFC basic skeleton for an empty Revit project is written to Neo4j; per-element subgraphs (Wall, Window, …) are not yet emitted.

```
 Revit Document  →  IFC entity tree  →  Graph nodes/edges  →  Cypher MERGE
 (Revit API)       (GeometryGym)      (ConMan2 schema)     (Neo4j.Driver)
```

## Quick start

Assuming Revit 2025 + .NET 8 SDK + Neo4j Desktop are already installed:

```powershell
# 1. Set Neo4j password (User scope, so Revit launched from the Start menu inherits it).
[Environment]::SetEnvironmentVariable("NEO4J_PASSWORD", "<your-password>", "User")

# 2. Build. The Debug build auto-deploys the DLL + .addin to
#    %AppData%\Autodesk\Revit\Addins\2025\.
dotnet restore
dotnet build RevitGraphPlugin.sln -c Debug
```

Then start a Neo4j instance in Neo4j Desktop, launch Revit 2025 from the Start menu, open or create an Architectural project, and click **Sync current doc** on the `RevitGraphPlugin` ribbon. A TaskDialog reports the node and edge counts written to Neo4j.

## Stack

| Component   | Version                         |
| ----------- | ------------------------------- |
| Host        | Autodesk Revit 2025             |
| Runtime     | .NET 8                          |
| Graph DB    | Neo4j 5.x (local Neo4j Desktop) |
| IFC library | GeometryGym.Ifc (IFC4X3)        |
| Driver      | Neo4j.Driver (NuGet)            |

## Repository layout

```
RevitGraphPlugin/
├── src/
│   └── RevitGraphPlugin/
│       ├── RevitGraphApp.cs            # IExternalApplication: lifecycle + ribbon
│       ├── SyncCommand.cs              # IExternalCommand: button handler
│       ├── Neo4jConnector.cs           # IDriver factory from environment variables
│       ├── RevitGraphPlugin.addin      # Revit add-in manifest
│       └── RevitGraphPlugin.csproj     # .NET 8 / x64; references Revit API + NuGet
├── tools/
│   └── Neo4jSmokeTest/                 # Standalone console: verifies Neo4j env + connection outside Revit
├── doc/
│   └── spec/
│       └── related-work.md             # Lit review of reference projects
├── data/
│   └── samples/                        # IFC sample dataset for diff-driven discovery
│       ├── ifc/                        # IFC4 snapshots
│       ├── rvt/                        # Source Revit project for re-export
│       ├── cypher/                     # ConMan2 import results
│       └── README.md                   # what each snapshot represents and intended diffs
├── RevitGraphPlugin.sln
├── global.json                         # Pins .NET SDK 8.0.403
└── README.md
```

## Runtime flow

1. Revit 2025 loads `RevitGraphPlugin.addin` from `%AppData%\Autodesk\Revit\Addins\2025\`.
2. `RevitGraphApp.OnStartup` builds a Neo4j driver from `NEO4J_URI` / `NEO4J_USER` / `NEO4J_PASSWORD` and registers the "Sync current doc" ribbon button.
3. The button runs `SyncCommand`:
   - `BoilerplateBuilder` reads `ProjectInformation` + Levels and constructs an in-memory IFC4 tree via GeometryGym.Ifc (`IfcProject` + `IfcSite` + `IfcBuilding` + `IfcBuildingStorey × N` + `IfcUnitAssignment` + `IfcGeometricRepresentationContext` + 4 SubContexts + the `IfcOwnerHistory` chain).
   - `CypherEmitter` walks the tree, classifies entities by ConMan2's rules (`PrimaryNode` / `ConnectionNode` / `SecondaryNode`), and `MERGE`s nodes plus `[:rel {rel_type, list_index}]` edges into Neo4j off the UI thread (60 s timeout).
   - A `TaskDialog` reports node and edge counts.
4. Per-element subgraphs (Wall, Window, …) are not yet implemented — that is the next stage.

## Branches

- `feat/dev` — active rebuild (current skeleton).
- `archive/v1-mvp` — preserved v1 MVP (Revit → IFC → Graph → Neo4j five-stage pipeline). Kept for reference; not intended for merge.
- `main` — initial commit only; the first rebuild milestone will be merged here via PR.

## Development environment

Prerequisites:

1. **Revit 2025** — installed at `D:\Autodesk\Revit 2025\` (project default; adjust via `RevitInstallPath2025` env var if installed elsewhere). Education licence is sufficient.
2. **.NET 8 SDK** — 8.0.403 (pinned by `global.json`).
3. **Neo4j Desktop** with a local 5.x instance reachable via `neo4j://`.

Add-in registration is automatic for Debug builds (see Build below).

## Neo4j credentials

The plugin reads three environment variables; only `NEO4J_PASSWORD` is required.

| Variable         | Default                  | Notes                              |
| ---------------- | ------------------------ | ---------------------------------- |
| `NEO4J_URI`      | `neo4j://127.0.0.1:7687` | matches Neo4j Desktop 2026 default |
| `NEO4J_USER`     | `neo4j`                  |                                    |
| `NEO4J_PASSWORD` | —                        | required, no default               |

## Build notes

The `dotnet build` Debug command in the Quick start runs an after-build target that copies the DLL, `.addin` manifest, and NuGet runtime dependencies (`Neo4j.Driver.dll`, `GeometryGymIFC.dll`, …) into `%AppData%\Autodesk\Revit\Addins\2025\`. `RevitAPI` / `RevitAPIUI` are referenced with `Private=false` — Revit loads them from its own install directory.

Override the Revit install path if it differs from `D:\Autodesk\Revit 2025\`:

```powershell
$env:RevitInstallPath2025 = "C:\Program Files\Autodesk\Revit 2025\"
dotnet build RevitGraphPlugin.sln -c Debug
```

## Verify and troubleshoot

After clicking **Sync current doc**, the TaskDialog reports the node and edge counts. Verify the write in Neo4j Browser:

```cypher
MATCH (n) RETURN n.EntityType AS entity, count(*) AS n ORDER BY n DESC;
```

Expect `IfcProject`, `IfcSite`, `IfcBuilding`, `IfcBuildingStorey`, `IfcUnitAssignment`, `IfcSIUnit`, `IfcGeometricRepresentationContext`, `IfcGeometricRepresentationSubContext`, plus the placement and ownership chain. Compare counts against [`data/samples/cypher/BASELINE.md`](data/samples/cypher/BASELINE.md).

### Troubleshooting

| Symptom                                      | Likely cause                                                                                                                                                             |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Build fails finding `RevitAPI.dll`           | Revit not at `D:\Autodesk\Revit 2025\` — set `$env:RevitInstallPath2025` before `dotnet build`.                                                                          |
| Ribbon tab missing after launch              | Debug build did not deploy (Release build skips the deploy target); check `%AppData%\Autodesk\Revit\Addins\2025\` for `RevitGraphPlugin.addin` + `RevitGraphPlugin.dll`. |
| TaskDialog "Neo4j connector not initialised" | `NEO4J_PASSWORD` not set, or set as process-scope only — re-set with `[Environment]::SetEnvironmentVariable(..., "User")` and restart Revit.                             |
| TaskDialog "Neo4j connectivity failed"       | Instance not running, password wrong, or backend still warming up — wait 10–20 s after starting the instance.                                                            |

## Acknowledgements

This project draws on three open-source predecessors:

- **ConMan2** by Sebastian Esser — Neo4j graph schema for IFC.
- **SpaceTracker** by Sebastian Esser — Revit `DocumentChanged` integration pattern.
- **IfcInfraToolKit** by TUM CMS — IFC geometry export via GeometryGym.Ifc.

## Status of this repository

Student project. Not for commercial use.
