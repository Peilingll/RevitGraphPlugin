# RevitGraphPlugin

A Revit add-in (2025/2026, .NET 8) that mirrors a Revit document into a Neo4j property graph — in real
time — as IFC entities following the [ConMan2](https://github.com/seb-esser/ConMan2/)
schema, for change-tracking and version-diff workflows.

**Current state:** **live incremental sync** is the primary mode. Turning it on writes a
full baseline snapshot, then follows every committed Revit transaction (add / modify /
delete) and updates only the affected graphlet in Neo4j. The empty-project boilerplate
(spatial breakdown, units, geometric contexts, OwnerHistory chain, default property sets)
plus per-element subgraphs for Wall, Window, Door, Floor, Ceiling, Roof, Beam and Column
are written and matched against the ConMan2 baseline.

## Sync modes

The ribbon exposes three buttons; they share the converters and the graph schema, and
each writes under its own timestamp so they don't collide in Neo4j.

| Ribbon button     | Pipeline                                | Trigger                           | Timestamp       |
| ----------------- | --------------------------------------- | --------------------------------- | --------------- |
| **Live Sync**     | direct-write, pure C# (**primary**)     | baseline on ON, then every change | `plugin-live`   |
| **Sync (direct)** | direct-write, pure C#                   | one click = one full write        | `plugin-direct` |
| **Sync (bridge)** | temp`.ifc` → ConMan2 Python (reference) | one click = one full write        | `plugin-bridge` |

**Live Sync = the direct-write full snapshot as a baseline + keep the ggifc model as an
in-memory mirror + event-driven incremental updates.** See
[`doc/spec/livesync-architecture.md`](doc/spec/livesync-architecture.md) for the complete
per-file walkthrough.

## Architecture — live sync (C#)

Live sync does **not** go through Python / ConMan2 / ifcopenshell. It reuses the
direct-write pipeline (`Revit → ggifc tree → Cypher → Neo4j`, no temp `.ifc`) and adds
three things on top: an in-memory ggifc mirror kept alive after the baseline, a
`DocumentChanged` subscription, and an incremental rule engine.

```
C# (in the Revit process) — no Python, no temp .ifc

  Baseline (on ON)
    ModelAssembler.Build ──► ggifc tree ──► CypherEmitter.WriteAsync ──► Neo4j
                                 │
                                 └─ kept alive as the in-memory mirror

  Increment (per committed transaction)
    DocumentChanged
      └─ LiveSyncManager routes deletes → adds → modifies (hosts before hosted)
           └─ TryConvertOne ──► LiveRuleBuilder ──► GraphRule
                └─ CypherEmitter.ApplyRuleAsync ──► Neo4j   (only the graphlet)
```

- **Baseline** — `ModelAssembler.Build` (boilerplate + convert every element) →
  `CypherEmitter.WriteAsync` (full wipe + rewrite). Identical to a one-shot **Sync
  (direct)**; the only difference is the `IfcModelContext` is _kept_ as the mirror.
- **Increment** — a `StepIdWatermark` isolates exactly the entities one element's
  conversion created (ggifc allocates StepIds monotonically), so the increment walks only
  that graphlet — O(graphlet), not O(whole model). One Revit change becomes one
  `GraphRule`, applied by `CypherEmitter.ApplyRuleAsync` in a single all-or-nothing
  transaction.
- **Fail loud** — any exception in the event path disposes the session and flips the
  button OFF rather than desyncing silently. Diagnostics go to
  `%TEMP%\RevitGraphPlugin\live.log` (`TaskDialog` is forbidden inside `DocumentChanged`).

### The bridge mode (reference)

**Sync (bridge)** is the original path and is kept for cross-checking: it serialises the
ggifc tree to a temp `.ifc` and hands it to ConMan2's own importer, so the graph schema
matches the ConMan2 baseline with zero drift.

```
ggifc tree → db.WriteFile() → temp .ifc (STEP) → python snippet_to_cypher.py
                                                    → ifcopenshell parse
                                                    → ConMan2 IfcGraphInterface → Neo4j
```

Reusing ConMan2's importer is what originally validated the direct-write output. The
direct/live pipeline now emits the same node/edge shapes without the Python round-trip.
The bridge supports **CREATE only**: `snippet_to_cypher.py` raises `NotImplementedError`
for `DELETE` / `UPDATE`, so incremental changes go through the live pipeline.

## Quick start

Prerequisites: Revit (2025 by default — see *Building for another Revit version*),
.NET 8 SDK (8.0.403, pinned by `global.json`), a running Neo4j instance. The Python
environment is only needed for the **Sync (bridge)** button.

**Clone ConMan2 as a sibling of this repo** — paths are then resolved relatively, no
configuration needed (required only for the bridge button, but the schema reference is
useful either way):

```
<parent>/
├── RevitGraphPlugin/   (this repo)
└── ConMan2/            git clone https://github.com/seb-esser/ConMan2
```

```powershell
# 1. ConMan2 (sibling clone) + its Python environment — only for the bridge button.
git clone https://github.com/seb-esser/ConMan2 ..\ConMan2
py -m venv ..\ConMan2\venv
..\ConMan2\venv\Scripts\pip install -r ..\ConMan2\src\requirements.txt

# 2. Neo4j password (User scope, so Revit inherits it).
[Environment]::SetEnvironmentVariable("NEO4J_LOCAL_PASSWORD", "<your-password>", "User")

# 3. Build (Debug auto-deploys DLL + .addin to %AppData%\Autodesk\Revit\Addins\<version>\).
dotnet build RevitGraphPlugin.sln -c Debug
```

Then start a Neo4j instance, launch Revit, open/create an Architectural project, and click
**Live Sync** on the `RevitGraphPlugin` ribbon. The button flips to **ON**, the baseline is
written, and every subsequent change flows through automatically. Click again to turn it
OFF (disposes the session; the graph is left as-is).

## Building for another Revit version

The target version is an MSBuild property (`RevitVersion`, default `2025`) that drives
both the `RevitAPI.dll` lookup and the Addins deploy folder:

```powershell
dotnet build RevitGraphPlugin.sln -c Debug -p:RevitVersion=2026
```

The API assemblies are resolved in order: `RevitInstallPath` (explicit override) →
`RevitInstallPath2025` (legacy name) → `D:\Autodesk\Revit <version>\` →
`%ProgramFiles%\Autodesk\Revit <version>\` → the
[Nice3point.Revit.Api](https://github.com/Nice3point/RevitApi) NuGet packages. The NuGet
fallback means any machine can compile for any version without Revit installed — only
running the add-in requires the real Revit.

## Packaging for distribution

`package.ps1` produces a self-contained zip so the target machine needs neither the
.NET SDK nor this repo:

```powershell
.\package.ps1 -RevitVersion 2026     # → dist\RevitGraphPlugin-2026.zip
```

The zip contains the pre-built DLLs + `.addin`, `install.ps1`, `INSTALL.md`, and the
version-checkout tooling (`checkout.ps1` + `graph2ifc.py`). On the target machine:
unzip, run `install.ps1` (per-user, no admin), start a local Neo4j, launch Revit —
full steps in [deploy/INSTALL.md](deploy/INSTALL.md). Checkout/undo/replay works with
just Neo4j; only the optional `-Ifc` round-trip needs a ConMan2 clone.

## Environment variables

Path variables default to the sibling-clone layout (resolved relatively); set them only if
ConMan2 lives elsewhere. `NEO4J_LOCAL_PASSWORD` is the only one required for direct/live
sync.

| Variable                                       | Default                                     | Read by                   |
| ---------------------------------------------- | ------------------------------------------- | ------------------------- |
| `NEO4J_LOCAL_PASSWORD`                         | — (**required**)                            | C# (direct/live) + Python |
| `NEO4J_LOCAL_USERNAME` / `_HOSTNAME` / `_PORT` | `neo4j` / `localhost` / `7687`              | C# (direct/live) + Python |
| `CONMAN2_PATH`                                 | `<repo>/../ConMan2/src`                     | Python (bridge only)      |
| `PLUGIN_PYTHON`                                | `<repo>/../ConMan2/venv/Scripts/python.exe` | C# (bridge only)          |
| `PLUGIN_SNIPPET_SCRIPT`                        | `<repo>/tools/python/snippet_to_cypher.py`  | C# (bridge only)          |
| `RevitVersion` / `RevitInstallPath`            | `2025` / auto-resolved (see above)          | MSBuild (build time only) |

`Neo4jConfig.Resolve()` builds the bolt URI + credentials from `NEO4J_LOCAL_*` and forces
`localhost → 127.0.0.1` to match ConMan2. `tools/Neo4jSmokeTest` reads the same names
(falling back to legacy `NEO4J_*`) to verify connectivity:
`dotnet run --project tools/Neo4jSmokeTest`.

## Repository layout

```
src/RevitGraphPlugin/
├── RevitGraphApp.cs             # IExternalApplication: ribbon + DocumentChanged/Closing subscriptions
├── LiveSyncToggleCommand.cs     # Live Sync button → LiveSyncManager.Toggle
├── LiveSyncManager.cs           # static session holder; routes DocumentChanged; fail loud
├── LiveSyncSession.cs           # per-document mirror: baseline + Insert/Replace/Remove
├── LiveSyncLog.cs               # append-only %TEMP% diagnostics (no UI allowed in events)
├── Neo4jConfig.cs               # resolves bolt URI + credentials from NEO4J_LOCAL_*
├── SyncDirectCommand.cs         # Sync (direct) button → full direct write
├── SyncCommand.cs               # Sync (bridge) button → temp IFC → Python
├── Ifc/
│   ├── ModelAssembler.cs        # Phase A: boilerplate + convert-all → ggifc tree
│   ├── IfcModelContext.cs       # the live in-memory mirror (Db, OwnerByStepId, ConvertedElements)
│   ├── StepIdWatermark.cs       # highest-StepId watermark isolating one element's graphlet
│   ├── BoilerplateBuilder.cs    # Revit Document → IFC4 boilerplate skeleton
│   ├── Converters/              # per-element: Wall, Window, Door, Floor, Ceiling, Roof, Beam, Column
│   ├── Geometry/ · Hosting/     # B-rep bodies · openings (windows/doors in host walls)
│   ├── RevitOwnerHistory.cs     # OwnerHistory matching Revit's IFC exporter
│   └── IfcGuidConverter.cs      # Revit UniqueId → IFC GlobalId
├── Cypher/
│   ├── Direct/                  # pure-C# pipeline: EntityWalker, NodeClassifier, StepLineParser,
│   │                            #   GraphRule, LiveRuleBuilder, CypherEmitter (WriteAsync + ApplyRuleAsync)
│   └── IfcSnippetSink.cs        # bridge: STEP → spawns Python
├── RevitGraphPlugin.addin       # Revit add-in manifest
└── RevitGraphPlugin.csproj      # .NET 8 / x64; Revit API + GeometryGymIFC + Neo4j.Driver
tools/
├── python/snippet_to_cypher.py  # bridge: STEP → ifcopenshell → ConMan2 → Neo4j
└── Neo4jSmokeTest/              # standalone Neo4j connectivity check
tests/RevitGraphPlugin.Tests/    # xUnit: watermark/ownership, rule builder, ApplyRule integration, STEP parsing
data/samples/                    # IFC + Cypher baseline dataset
doc/spec/livesync-architecture.md # full per-file live-sync walkthrough
doc/log/                         # English research logs
```

## Verify

With **Live Sync** ON, make a change in Revit (draw a wall, delete a window) and re-run in
Neo4j Browser — the counts should track the model live:

```cypher
MATCH (n {timestamp: 'plugin-live'}) RETURN n.EntityType AS entity, count(*) AS n ORDER BY n DESC;
```

The live diagnostics log at `%TEMP%\RevitGraphPlugin\live.log` records every routed change
(deletes → adds → modifies) and any swallowed error.

## Troubleshooting

| Symptom                                   | Likely cause                                                                                    |
| ----------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Build fails finding`RevitAPI.dll`         | No local Revit and no NuGet access — set `RevitInstallPath`, or restore NuGet online once.      |
| Ribbon tab missing after launch           | Debug build not deployed (Release skips it) — check`%AppData%\Autodesk\Revit\Addins\<version>\`. |
| Live Sync flips itself OFF after a change | An exception fired in the event path (fail loud) — read`%TEMP%\RevitGraphPlugin\live.log`.      |
| Neo4j auth error on sync                  | `NEO4J_LOCAL_PASSWORD` wrong/unset — verify with `dotnet run --project tools/Neo4jSmokeTest`.   |
| "Python interpreter / ConMan2 not found"  | Bridge button only — ConMan2 not a sibling clone; set`PLUGIN_PYTHON` / `CONMAN2_PATH`, restart. |

## Branches

`feat/dev` active development · `archive/v1-mvp` preserved pure-C# v1 (reference only) ·
`main` milestones via PR.

## Acknowledgements

Builds on **ConMan2** (Sebastian Esser, reused as the Python importer / schema baseline),
**SpaceTracker** (Sebastian Esser, `DocumentChanged` pattern), and **IfcInfraToolKit**
(TUM CMS, IFC geometry export). Student project (TUM Hiwi); not for commercial use.
