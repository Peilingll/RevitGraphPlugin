# RevitGraphPlugin

A Revit add-in (2025 / 2026, .NET 8) that writes the open Revit document into Neo4j as an
IFC property graph in the [ConMan2](https://github.com/seb-esser/ConMan2) schema. It
tracks document changes as they are committed and records each one as a graph
transformation rule, so any recorded version can be restored and exported as IFC.

## Architecture

Live Sync writes a full snapshot of the document, keeps the converted IFC model in memory,
and then handles Revit's `DocumentChanged` event:

```
Revit transaction ──► added / modified / deleted element ids
    └─► re-convert the element (GeometryGym.Ifc) ──► one GraphRule
          └─► apply to the current-state graph + store in the :Rule chain   (one Neo4j transaction)
```

- **Baseline**: every element through its converter, written as a full snapshot.
- **Increment**: only the changed element's subgraph is rebuilt. Nodes that did not change
  stay in place; only the difference is applied and stored.
- **Rule chain**: `:Baseline` and `:Rule` nodes linked in order. Each rule holds copies of
  what it deleted and inserted, the value changes, and portable names for its context.
- **Checkout**: `checkout.ps1` (over the `rulechain` CLI) walks the chain forwards
  (replay) or backwards (undo), one transaction per step.

Schema, rule storage layout, configuration and a per-file reference:
[`doc/spec/livesync-architecture.md`](doc/spec/livesync-architecture.md).

## Installation

Requirements: Revit 2025 (2026: `-p:RevitVersion=2026`), .NET SDK 8.0.403 (pinned by
`global.json`), a local Neo4j instance.

```powershell
# Neo4j password, User scope so Revit inherits it
[Environment]::SetEnvironmentVariable("NEO4J_LOCAL_PASSWORD", "<password>", "User")

# Build; Debug deploys the add-in to %AppData%\Autodesk\Revit\Addins\2025\
dotnet build RevitGraphPlugin.sln -c Debug
```

ConMan2 is only needed for IFC export from the graph (`checkout.ps1 -Ifc`) and for the
bridge sync mode. Clone it beside this repo and create its environment:

```powershell
git clone https://github.com/seb-esser/ConMan2 ..\ConMan2
py -m venv ..\ConMan2\venv
..\ConMan2\venv\Scripts\pip install -r ..\ConMan2\src\requirements.txt
```

`package.ps1` builds a zip for machines without the SDK (see `deploy/INSTALL.md`).

## Usage

The `RevitGraphPlugin` ribbon tab has three buttons. Each writes under its own
`timestamp`, so they do not interfere with each other in Neo4j.

| Button            | What it does                                                | Timestamp       |
| ----------------- | ----------------------------------------------------------- | --------------- |
| **Live Sync**     | baseline on ON, then every committed change; rules stored   | `plugin-live`   |
| **Sync (direct)** | one full write of the current model                         | `plugin-direct` |
| **Sync (bridge)** | one full write through a temp `.ifc` and ConMan2's importer | `plugin-bridge` |

Start Neo4j, open a project in Revit, click **Live Sync**. The button shows **ON** once the
baseline is written; every change is now mirrored. Click again to stop.

Versions recorded while Live Sync was ON (turn it OFF first):

```powershell
.\checkout.ps1                  # list the chain and the current position
.\checkout.ps1 7                # move the graph to the state after rule 7
.\checkout.ps1 7 -Ifc v7.ifc    # ...and export that version as IFC
.\checkout.ps1 head             # back to the newest version
```

## Tests

```powershell
dotnet test tests\RevitGraphPlugin.Tests
```

Pure tests (converters, parsers, graphlet diff) always run. Graph tests (rule apply, store,
replay, checkout round trips) need Neo4j at `bolt://127.0.0.1:7687` and are reported as
skipped without it.

## Dependencies

| Package                                                                     | License        |
| --------------------------------------------------------------------------- | -------------- |
| [GeometryGymIFC](https://github.com/GeometryGym/GeometryGymIFC) 0.1.22      | MIT            |
| [Neo4j.Driver](https://github.com/neo4j/neo4j-dotnet-driver) 5.26           | Apache-2.0     |
| [Nice3point.Revit.Api](https://github.com/Nice3point/RevitApi) (build only) | MIT            |
| [xunit](https://github.com/xunit/xunit), Xunit.SkippableFact (tests)        | Apache-2.0     |
| [ConMan2](https://github.com/seb-esser/ConMan2) + IfcOpenShell (optional)   | MIT / LGPL-3.0 |

## Acknowledgements

Builds on **ConMan2** and **SpaceTracker** (Sebastian Esser) and **IfcInfraToolKit**
(TUM CMS). Student project at TUM; not for commercial use.
