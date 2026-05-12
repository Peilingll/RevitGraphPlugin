# RevitGraphPlugin

A Revit 2025 add-in that translates native Revit elements into IFC entities and persists them as a Neo4j property graph, enabling round-trip and version-diff workflows.

**Current state:** environment + Neo4j-connection skeleton. The IFC basic skeleton for an empty Revit project is written to Neo4j; per-element subgraphs (Wall, Window, …) are not yet emitted.

```
 Revit Document  →  IFC entity tree  →  Graph nodes/edges  →  Cypher MERGE
 (Revit API)       (GeometryGym)      (ConMan2 schema)     (Neo4j.Driver)
```

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

## Runtime flow (current skeleton)

1. Revit 2025 loads `RevitGraphPlugin.addin` from `%AppData%\Autodesk\Revit\Addins\2025\`.
2. `RevitGraphApp.OnStartup` builds a Neo4j driver from environment variables (`NEO4J_URI`, `NEO4J_USER`, `NEO4J_PASSWORD`) and registers a `RevitGraphPlugin` ribbon tab with a single button.
3. Pressing the button runs `SyncCommand`, which calls `VerifyConnectivityAsync` off the UI thread (10 s timeout) and shows a `Neo4j connection OK.` TaskDialog on success.
4. `OnShutdown` disposes the driver.

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

## Build

```powershell
dotnet restore
dotnet build RevitGraphPlugin.sln -c Debug
```

The Debug build runs a post-build target that copies `RevitGraphPlugin.dll`, its `.addin` manifest, and runtime NuGet dependencies (`Neo4j.Driver.dll`, `GeometryGymIFC.dll`, …) into `%AppData%\Autodesk\Revit\Addins\2025\`. `RevitAPI` / `RevitAPIUI` are referenced with `Private=false` — Revit loads them from its own install directory.

Override the Revit install path if it differs:

```powershell
$env:RevitInstallPath2025 = "C:\Program Files\Autodesk\Revit 2025\"
dotnet build RevitGraphPlugin.sln -c Debug
```

## Neo4j credentials

The plugin and `Neo4jSmokeTest` both read three environment variables; only `NEO4J_PASSWORD` is required.

| Variable         | Default                  | Notes                              |
| ---------------- | ------------------------ | ---------------------------------- |
| `NEO4J_URI`      | `neo4j://127.0.0.1:7687` | matches Neo4j Desktop 2026 default |
| `NEO4J_USER`     | `neo4j`                  |                                    |
| `NEO4J_PASSWORD` | —                        | required, no default               |

**Smoke test (process scope, current PowerShell only):**

```powershell
$env:NEO4J_PASSWORD = "<your-password>"
dotnet run --project tools/Neo4jSmokeTest
```

A successful run prints `hello = 1` and exits with code `0`. Use this to confirm Neo4j is reachable before launching Revit.

**Revit (User scope, persistent — Revit is launched outside any shell):**

```powershell
[Environment]::SetEnvironmentVariable("NEO4J_PASSWORD", "<your-password>", "User")
```

Set this once per machine; Revit launched via Start menu / desktop shortcut inherits User-scope variables. Process-scope (`$env:`) is _not_ visible to Revit. After setting, restart any already-open Revit / VS / terminal so they pick up the new value.

## Acknowledgements

This project draws on three open-source predecessors:

- **ConMan2** by Sebastian Esser — Neo4j graph schema for IFC.
- **SpaceTracker** by Sebastian Esser — Revit `DocumentChanged` integration pattern.
- **IfcInfraToolKit** by TUM CMS — IFC geometry export via GeometryGym.Ifc.

## Status of this repository

Student project. Not for commercial use.
