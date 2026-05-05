# RevitGraphPlugin

A Revit 2025 add-in that translates native Revit elements into IFC entities and persists them as a Neo4j property graph. The graph schema follows the polymorphic `[:rel {rel_type, list_index}]` model from ConMan2, enabling round-trip and version-diff workflows over the IFC entity tree.

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
├── doc/
│   ├── spec/
│   │   ├── related-work.md   # ConMan2 / SpaceTracker / IfcInfraToolKit study
│   │   └── design.md         # System architecture and roadmap
│   └── log/                  # Development log (reserved)
└── README.md
```

Source code (`src/`, `*.csproj`) will be added during Stage 0 of the roadmap.

## Documentation

- [`doc/spec/related-work.md`](doc/spec/related-work.md) — comparative analysis of three reference projects: ConMan2 (Neo4j schema), SpaceTracker (Revit add-in architecture), IfcInfraToolKit (geometry export).
- [`doc/spec/design.md`](doc/spec/design.md) — design specification, five-stage implementation roadmap, validation case ("place a window on a wall").

## Development environment

Prerequisites:

1. **Revit 2025** — installed at `D:\Autodesk\Revit 2025\` (project default; adjust the `.csproj` reference paths if installed elsewhere). Education licence is sufficient.
2. **Visual Studio 2022** (17.8 or newer) with the .NET 8 SDK.
3. **Neo4j Desktop** with a local 5.x database, bolt URL accessible from the dev machine.

Add-in registration: drop a `.addin` manifest into `%AppData%\Autodesk\Revit\Addins\2025\` pointing to the build output DLL.

## Build

```powershell
dotnet restore
dotnet build RevitGraphPlugin.sln -c Debug
```

The Debug build runs a post-build target that copies `RevitGraphPlugin.dll`, the `.addin` manifest, and runtime NuGet dependencies (`Neo4j.Driver.dll`, `GeometryGymIFC.dll`, ...) into `%AppData%\Autodesk\Revit\Addins\2025\`. RevitAPI / RevitAPIUI are referenced with `Private=false` and **not** copied — Revit loads them from its own install directory.

Override the Revit install path if it is not at the project default:

```powershell
$env:RevitInstallPath2025 = "C:\Program Files\Autodesk\Revit 2025\"
dotnet build RevitGraphPlugin.sln -c Debug
```

### Neo4j credentials

The plugin reads three environment variables; only `NEO4J_PASSWORD` is required.

| Variable | Default | Notes |
| --- | --- | --- |
| `NEO4J_URI` | `neo4j://127.0.0.1:7687` | matches Neo4j Desktop 2026 default |
| `NEO4J_USER` | `neo4j` | |
| `NEO4J_PASSWORD` | — | required, no default |

**For the smoke test (process scope, this PowerShell only):**

```powershell
$env:NEO4J_PASSWORD = "<your-password>"
dotnet run --project tools/Neo4jSmokeTest
```

**For Revit (User scope, persistent — Revit is launched outside any shell):**

```powershell
[Environment]::SetEnvironmentVariable("NEO4J_PASSWORD", "<your-password>", "User")
```

Set this once per machine; Revit launched via Start menu / desktop shortcut inherits User-scope variables. Process-scope (`$env:`) is *not* visible to Revit. After setting, restart any already-open Revit / VS / terminal so they pick up the new value.

A successful smoke run prints `hello = 1` and exits with code `0`.

## Validation

The first end-to-end test case is "place a window on a wall" — see [`doc/spec/design.md` §4 Stage 5](doc/spec/design.md) for the expected Cypher subgraph and behavioural assertions.

## Acknowledgements

This project draws on three open-source predecessors:

- **ConMan2** by Sebastian Esser — Neo4j graph schema for IFC.
- **SpaceTracker** by Sebastian Esser — Revit `DocumentChanged` integration pattern.
- **IfcInfraToolKit** by TUM CMS — IFC geometry export via GeometryGym.Ifc.

## Status of this repository

Student project. Not for commercial use.
