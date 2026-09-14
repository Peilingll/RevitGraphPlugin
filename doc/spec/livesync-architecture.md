# Live Sync Architecture

RevitGraphPlugin writes a Revit document into Neo4j as an IFC property graph in the
ConMan2 schema and keeps it up to date from Revit's `DocumentChanged` event. Every change
is applied as a graph transformation rule and stored in a rule chain beside the graph.
This document describes the pipeline, the graph schema, the rule storage, and the code.

Contents

1. [Overview](#1-overview)
2. [Graph schema](#2-graph-schema)
3. [Data flow](#3-data-flow)
4. [Rules](#4-rules)
5. [Version checkout](#5-version-checkout)
6. [Levels, undo and reconciliation](#6-levels-undo-and-reconciliation)
7. [Identity](#7-identity)
8. [Coverage and limits](#8-coverage-and-limits)
9. [Build and configuration](#9-build-and-configuration)
10. [Diagnostics](#10-diagnostics)
11. [Repository layout](#11-repository-layout)
12. [Per-file reference](#12-per-file-reference)

---

## 1. Overview

The ribbon has three buttons. All three build the same in-memory IFC model
(GeometryGym.Ifc, "ggifc") from the Revit document; they differ in how it reaches Neo4j.

| Button            | Pipeline                                            | Trigger                            | Timestamp       |
| ----------------- | --------------------------------------------------- | ---------------------------------- | --------------- |
| **Live Sync**     | direct write, C# only                               | baseline on ON, then every change  | `plugin-live`   |
| **Sync (direct)** | direct write, C# only                               | one click, one full write          | `plugin-direct` |
| **Sync (bridge)** | temp `.ifc`, then ConMan2's Python importer         | one click, one full write          | `plugin-bridge` |

Live Sync is the direct write used as a baseline, plus two things the one-click modes do
not have: the ggifc model is kept alive as an in-memory mirror of the document, and a
`DocumentChanged` subscription turns each committed transaction into a rule.

The bridge mode serialises the ggifc tree to a temp `.ifc` and hands it to ConMan2's own
`ifc_2_graph` (`tools/python/snippet_to_cypher.py`, via ifcopenshell). It exists so the
direct write can be compared against ConMan2's importer; it supports CREATE only.

---

## 2. Graph schema

The current-state graph (`timestamp = plugin-live`) follows ConMan2's schema
(`neo4j_core/neo4j_model.py`). The rule chain is an extension that lives under its own
timestamps and never touches the current-state graph.

| Layer               | Names                                                                                                                                                                                                       | Defined in                                                       |
| ------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------- |
| Current-state graph | Labels `Node` > `GenericNode` > `PrimaryNode` / `ConnectionNode` / `SecondaryNode`, plus `InlineNode`. One edge type `rel {rel_type, list_index}`. Node properties are the IFC attributes (`$` = unset) plus `EntityType`, `p21_id`, `timestamp`. | ConMan2; plugin side `Cypher/Direct/NodeClassifier.cs`, `EntityWalker.cs` |
| Ownership           | `revit_element_id` on every node an element's conversion created (inline nodes included). Shared context (containment and aggregation rels, boilerplate) carries none.                                    | `Ifc/Converters/ElementConverterRegistry.cs`                     |
| Rule chain          | Labels `:RuleChain`, `:Baseline`, `:Rule`, `:Change`, `:Glue`. Edges `HEAD`, `NEXT`, `DELETES`, `INSERTS`, `SETS`, `GLUE`.                                                                                  | `Cypher/Direct/RuleStore.cs`                                     |
| Operations          | `Insert`, `Remove`, `Modify`, `Replace`                                                                                                                                                                      | plugin names for Revit's added / deleted / modified              |

Node kinds (ConMan2): `PrimaryNode` = `IfcObjectDefinition` or `IfcPropertyDefinition`,
`ConnectionNode` = `IfcRelationship`, `SecondaryNode` = every other STEP entity,
`InlineNode` = a wrapped value with no STEP id of its own (e.g. a property's
`NominalValue`).

The rule chain maps onto ConMan2's patch files: `DELETES` / `INSERTS` copies correspond to
`Patch_Topo`, `SETS` rows to `Patch_Sema`, and `:Glue` rows to the unique-path context
references of `GraphPatch`.

---

## 3. Data flow

### Baseline (Live Sync ON, or Sync (direct))

```
LiveSyncToggleCommand ─► LiveSyncManager.Toggle ─► LiveSyncSession.Start
    ModelAssembler.Build(doc)                       build the whole ggifc tree
        BoilerplateBuilder                          project, units, contexts, site, building, storeys, default psets
        ElementConverterRegistry.ConvertAll         every supported element, ownership tagged
    CypherEmitter.WriteAsync                        wipe the timestamp, write nodes, edges, inline nodes
    RuleStore.RecordBaselineAsync                   (:Baseline) member appended to the chain
    IfcModelContext kept as the mirror
```

### Increment (every committed transaction)

```
ControlledApplication.DocumentChanged ─► LiveSyncManager.OnDocumentChanged
    match the session's document (Document.Equals, not reference equality)
    deletes (levels last) ─► adds ─► modifies (hosts before hosted; types expanded to instances)
    each: LiveSyncSession.ApplyRemoved / ApplyAdded / ApplyModified
        before = StepIdWatermark.Current
        ElementConverterRegistry.TryConvertOne      re-convert only this element
        after  = StepIdWatermark.Current
        LiveRuleBuilder.BuildUpsert / BuildRemove   graphlet = entities in (before, after] + spatial rel refresh
        CypherEmitter.ApplyRuleAsync                one transaction: capture L, diff, apply, persist
    ReconcileVanished (every event), ReconcileAll (undo / redo / rollback)
    any exception: session disposed, button OFF, reason in live.log
```

`StepIdWatermark` is what makes the increment cheap: ggifc allocates STEP ids
monotonically, so the entities one converter call created are exactly those in
`(before, after]`, and only that range is walked.

---

## 4. Rules

One Revit element change is one `GraphRule` (`Cypher/Direct/GraphRule.cs`):

| Field           | Content                                                                                   |
| --------------- | ----------------------------------------------------------------------------------------- |
| `Op`            | `Insert`, `Remove`, `Replace`                                                              |
| `RevitElementId`| ownership key                                                                              |
| `Graphlet`      | walked entities to insert: the element's own plus any new shared context it created       |
| `SharedRefresh` | containment / aggregation rels whose edge set is rewritten from a fresh walk              |
| `SharedDelete`  | p21 ids of shared rels that became memberless (a fresh export would not contain them)     |
| `BeforeGraphlet`| the L side, read from the graph inside the transaction before anything is deleted         |
| `ContextRefs`   | portable names for every node the rule references but does not own                        |
| `Diff`          | how L and R were aligned                                                                  |

### Apply

`CypherEmitter.ApplyRuleAsync` runs everything in one Neo4j transaction:

1. Capture L (`GraphletReader.ReadOwnedAsync`): the owned nodes, their outgoing edges, and
   the incoming glue from context.
2. Align L and R (`GraphletDiff.Compare`). Matching seeds on GlobalIds present on both
   sides and propagates along edges whose `(rel_type, list_index)` is unique; list members
   pair by position, as in ConMan2's `run_diff`. Matched nodes form the interface I and stay
   in the graph; the rest is the pushout.
3. Aligned: delete the L pushout, merge the R pushout, SET the changed values on I,
   renumber I's `p21_id` to the fresh conversion's ids (the mirror now holds those; p21 is
   a file-local number, not an identity). Not aligned: delete the whole owned graphlet by
   `revit_element_id`, merge the new one.
4. Refresh `SharedRefresh` rels (edge set replaced, `list_index` renumbered), drop
   `SharedDelete` nodes.
5. `RuleStore.PersistAsync`.

### Storage

Every stored node lives under a rule timestamp, so the current-state graph stays pure
ConMan2 schema and a re-baseline wipe cannot reach the chain.

```
(:RuleChain {target_ts, next_seq, checked_out_seq}) -[:HEAD]-> newest member
(:Baseline {seq}) -[:NEXT]-> (:Rule {seq, op, revit_element_id, aligned, renumber_from, renumber_to}) -[:NEXT]-> ...
(:Rule) -[:DELETES]-> copies of L \ I      namespace "<target>-rule-<seq>-L"
(:Rule) -[:INSERTS]-> copies of R \ I      namespace "<target>-rule-<seq>-R"
(:Rule) -[:SETS]->    (:Change {path, path_after, key, list_index, before, after, inline})
(:Rule) -[:GLUE]->    (:Glue {context, rel_type, list_index, local_p21, side, direction})
```

| Stored op | Revit event                | Payload                                                                    |
| --------- | -------------------------- | -------------------------------------------------------------------------- |
| `Insert`  | added                      | R copies, glue                                                             |
| `Remove`  | deleted                    | L copies, glue, shared rels dropped                                        |
| `Modify`  | modified, values only      | `:Change` rows, renumber map                                               |
| `Replace` | modified, structure changed| pushout copies on both sides, changes, glue, renumber map (`aligned: true`); or both whole graphlets when L and R could not be aligned (`aligned: false`) |

A Replace whose diff is `NoChange` stores nothing (Revit reports modifies for changes the
converters do not read).

### Context references

A rule references nodes it does not own: the storey's containment rel, the host wall of a
window, `IfcOwnerHistory`, placements. Inside the rule p21 is a local name; the stored rule
names such nodes portably (`ContextRef`, ConMan2's `create_unique_path_mappings`): the
GlobalId of an IfcRoot anchor plus the path from it, each step keyed by
`rel_type / list_index / EntityType`. `ContextResolver` only accepts anchors and paths
through unowned nodes (boilerplate, never re-converted) or nodes of the rule's own
element, never through another element, so a name survives later edits of other
elements; a node reachable only through another element gets no name and is stored as
a raw p21.

---

## 5. Version checkout

`RuleReplayer` walks the chain from `checked_out_seq` to the requested
member, one rule per transaction: forwards applies a rule (delete L pushout by copy p21s,
merge R copies, glue, SET, renumber), backwards inverts it. Each step commits together with
the new `checked_out_seq`, so a failure leaves the graph at a real version. Below the newest
`:Baseline` the graph was rebuilt, so checkout stops there.

```powershell
.\checkout.ps1                  # list the chain and the current position
.\checkout.ps1 7                # move to the state after rule 7
.\checkout.ps1 7 -Ifc v7.ifc    # ...and export that version as IFC (ConMan2 graph_2_ifc)
.\checkout.ps1 head             # newest version
```

`checkout.ps1` wraps the `rulechain` CLI (`tools/RuleChainCli`), which exposes the
replayer directly:

```
rulechain list                   [--target TS]
rulechain checkout <seq|head>    [--target TS] [--onto TS]
rulechain undo [--count N] [--below SEQ]
rulechain replay
rulechain pingpong [--rounds N]  # bounce baseline <-> head N times, report any drift
```

Run either with Live Sync OFF; turning Live Sync ON again re-baselines. Exports land in
`data/out/`.

---

## 6. Levels, undo and reconciliation

**Levels** are elements too. `LevelConverter` (registered first) turns a level added
while live into an `IfcBuildingStorey` (Insert). A renamed or moved level updates the
storey in place (Modify): a storey is never rebuilt, because its containment rel and every
element on it point at it. A deleted level is a Remove routed after the elements Revit
deletes with it. Storey, pset and rel are tagged with the level id; the shared pset values
are not.

**Element types**: a modified `ElementType` is reported without its instances, so
`LiveSyncManager` replaces each type id by the instances using it.

**Undo, redo, rollback**: Revit reports these with empty id sets. After every event the
session removes tracked elements that no longer exist (`ReconcileVanished`); when the
operation is not a plain commit it also re-converts every tracked element (unchanged ones
diff to `NoChange` and store nothing) and inserts supported elements it does not track
(`ReconcileAll`).

**Shared rel revival**: a containment rel dropped when its storey emptied is re-joined by
the next element placed there (ggifc reuses the object). It arrives as a refresh of a node
the graph no longer holds and is recorded as inserted, so the chain can rebuild it.

---

## 7. Identity

| Entity                                                        | GlobalId                                                        |
| ------------------------------------------------------------- | --------------------------------------------------------------- |
| Product (wall, window, storey, ...)                           | Revit's own export GUID (`ExportUtils.GetExportId`), equal to the element's IfcGUID parameter |
| Pset, `IfcRelDefinesByProperties`, containment, aggregation, void / fill, opening | deterministic, seeded from the owner's GlobalId plus a role (`Ifc/StableIds.cs`) |
| Site, Building                                                | seeded from the project                                         |
| Everything else (placements, geometry, `IfcOwnerHistory`)     | none; reached by a path from an anchor                          |

`p21_id` is renumbered on every re-conversion and is masked by the diff. GlobalIds and
paths are what identify a node across versions.

---

## 8. Coverage and limits

Supported elements: Level, Wall, Floor, Column, Beam, Ceiling, Roof, Door, Window,
including the opening chain of hosted inserts (`IfcOpeningElement`, `IfcRelVoidsElement`,
`IfcRelFillsElement`). Each converter writes the product, its placement, a tessellated
BRep body, the `Pset_*Common` and storey containment. Property set sources
(`IsExternal`, `LoadBearing`) and storey containment match Revit's native IFC export
(`tools/python/compare_psets.py`).

Not emitted: element types (`IfcWallType` etc.), materials, quantities, native extrusion
geometry, surface styles.

Known limits:

- `ReconcileAll` re-converts every tracked element on each undo / rollback.
- Elevation is stored in the element placement (native: in the storey placement); a roof
  is one BRep (native: `IfcRoof` aggregating `IfcSlab` parts). Same world coordinates,
  different IFC structure.
- A hosted insert carries its full family geometry, copied on every Insert and Remove.
- The `IfcGeometricRepresentationSubContext` an element's geometry points at has no
  GlobalId and no path from an unowned anchor, so its glue is stored as a raw p21: valid
  in the same database, not on another host.
- The bridge mode supports CREATE only.

---

## 9. Build and configuration

### Revit version

`RevitVersion` (default `2025`) selects the Revit API assemblies and the deploy folder:

```powershell
dotnet build RevitGraphPlugin.sln -c Debug -p:RevitVersion=2026
```

Assemblies are resolved in order: `RevitInstallPath`, `D:\Autodesk\Revit <version>\`,
`%ProgramFiles%\Autodesk\Revit <version>\`, then the
[Nice3point.Revit.Api](https://github.com/Nice3point/RevitApi) NuGet packages, so any
machine can compile for any version. A Debug build copies the DLL and `.addin` to
`%AppData%\Autodesk\Revit\Addins\<version>\`.

### Packaging

```powershell
.\package.ps1 -RevitVersion 2026     # dist\RevitGraphPlugin-2026.zip
```

The zip holds the built DLLs, `.addin`, `install.ps1`, `INSTALL.md`, `checkout.ps1`, the
published `rulechain` CLI and
`graph2ifc.py`. The target machine needs Neo4j; ConMan2 only for `-Ifc`.

### Environment variables

| Variable                                       | Default                                     | Read by                     |
| ---------------------------------------------- | ------------------------------------------- | --------------------------- |
| `NEO4J_LOCAL_PASSWORD`                         | (required)                                  | C# (direct / live), Python  |
| `NEO4J_LOCAL_USERNAME` / `_HOSTNAME` / `_PORT` | `neo4j` / `localhost` / `7687`              | C# (direct / live), Python  |
| `CONMAN2_PATH`                                 | `<repo>/../ConMan2/src`                     | Python (bridge, graph2ifc)  |
| `PLUGIN_PYTHON`                                | `<repo>/../ConMan2/venv/Scripts/python.exe` | C# (bridge)                 |
| `PLUGIN_SNIPPET_SCRIPT`                        | `<repo>/tools/python/snippet_to_cypher.py`  | C# (bridge)                 |
| `RevitVersion` / `RevitInstallPath`            | `2025` / auto-resolved                      | MSBuild                     |

`Neo4jConfig.Resolve()` builds the bolt URI from `NEO4J_LOCAL_*` and maps `localhost` to
`127.0.0.1`, as ConMan2 does. Set the password at User scope so Revit inherits it.
`dotnet run --project tools/Neo4jSmokeTest` checks the connection.

---

## 10. Diagnostics

Revit forbids UI inside `DocumentChanged`, so the live path writes an append-only log:
`%LOCALAPPDATA%\Temp\<session GUID>\RevitGraphPlugin\live.log` (Revit runs with a
per-session TEMP; take the newest folder). It records every routed change, every stored
rule, and any exception that turned Live Sync off.

Node counts should track the model while Live Sync is ON:

```cypher
MATCH (n {timestamp: 'plugin-live'}) RETURN n.EntityType AS entity, count(*) AS n ORDER BY n DESC;
```

| Symptom                                    | Cause                                                                                              |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| Build cannot find `RevitAPI.dll`           | No local Revit and no NuGet access. Set `RevitInstallPath` or restore NuGet once.                  |
| Ribbon tab missing                         | Debug build not deployed (Release does not deploy). Check the Addins folder.                       |
| Live Sync turns itself OFF                 | An exception in the event path. Read the newest `live.log`.                                         |
| Neo4j auth error                           | `NEO4J_LOCAL_PASSWORD` wrong or unset. Verify with `tools/Neo4jSmokeTest`.                         |
| "Python interpreter / ConMan2 not found"   | Bridge or `-Ifc` only. ConMan2 is not a sibling clone; set `PLUGIN_PYTHON` / `CONMAN2_PATH`.       |

Tests: `dotnet test tests\RevitGraphPlugin.Tests`. Pure tests (converters, parsers, diff)
always run; graph tests need Neo4j at `bolt://127.0.0.1:7687` and are reported as skipped
without it. `rulechain pingpong` (section 5) exercises replay and undo against a real chain.

---

## 11. Repository layout

```
src/RevitGraphPlugin/
├── RevitGraphApp.cs             ribbon, DocumentChanged / DocumentClosing subscriptions
├── LiveSyncToggleCommand.cs     Live Sync button
├── LiveSyncManager.cs           session holder, event routing
├── LiveSyncSession.cs           baseline, increment, levels, reconciliation
├── LiveSyncLog.cs               live.log
├── SyncDirectCommand.cs         Sync (direct) button
├── SyncCommand.cs               Sync (bridge) button
├── Neo4jConfig.cs               NEO4J_LOCAL_* to bolt URI
├── Ifc/                         Revit to ggifc
│   ├── ModelAssembler.cs        boilerplate + every element
│   ├── BoilerplateBuilder.cs    project, units, contexts, site, building, storeys, default psets
│   ├── IfcModelContext.cs       the ggifc database and the ownership map (the mirror)
│   ├── StepIdWatermark.cs       (before, after] range of one conversion
│   ├── IfcGuidConverter.cs      Revit export GUID; seeded GlobalIds
│   ├── StableIds.cs             deterministic GlobalIds for synthetic IfcRoot entities
│   ├── RevitOwnerHistory.cs     OwnerHistory as Revit's exporter writes it
│   ├── Ifc4Schema.cs            IFC4 attribute order per entity (from data/schema)
│   ├── Converters/              one per element category, ElementConverterRegistry, PsetSources
│   ├── Geometry/                BRepBodyBuilder
│   └── Hosting/                 OpeningBuilder
├── Cypher/
│   ├── IfcSnippetSink.cs        bridge: temp .ifc, Python
│   └── Direct/                  ggifc to Neo4j, rule engine
│       ├── EntityWalker.cs, NodeClassifier.cs, StepLineParser.cs, P21Id.cs
│       ├── CypherEmitter.cs     WriteAsync (snapshot), ApplyRuleAsync (rule)
│       ├── GraphRule.cs, LiveRuleBuilder.cs
│       ├── GraphletReader.cs, GraphletDiff.cs
│       ├── ContextRef.cs, ContextResolver.cs
│       ├── RuleStore.cs         rule chain persistence
│       └── RuleReplayer.cs      replay, undo, checkout
├── RevitGraphPlugin.addin
└── RevitGraphPlugin.csproj
tests/RevitGraphPlugin.Tests/    xUnit
tools/python/                    compare_psets.py, graph2ifc.py, snippet_to_cypher.py
tools/RuleChainCli/              rulechain CLI (list, checkout, undo, replay, pingpong)
tools/Neo4jSmokeTest/            connection check
data/schema/ifc4_attributes.json IFC4 attribute order, embedded at build time
checkout.ps1, package.ps1, deploy/   version checkout; distribution zip and installer
RevitGraphPlugin.sln, global.json     solution (plugin, tests, Neo4jSmokeTest); .NET SDK 8.0.403
```

---

## 12. Per-file reference

### Startup and session

**`RevitGraphApp.cs`**: `IExternalApplication`. `OnStartup` builds the three buttons and
subscribes `DocumentChanged` / `DocumentClosing` to `LiveSyncManager`.

**`LiveSyncToggleCommand.cs`**: the button. Calls `LiveSyncManager.Toggle` and shows the
result; the only place UI is shown.

**`LiveSyncManager.cs`**: static holder of the single `LiveSyncSession`.
`OnDocumentChanged` matches the document with `Equals` (Revit does not guarantee the same
managed reference), routes deletes (levels last), adds, modifies (hosts before hosted,
types expanded to instances), then reconciles. Any exception disposes the session and
turns the button OFF.

**`LiveSyncSession.cs`**: one per document. `Start` = `ModelAssembler.Build`,
`CypherEmitter.WriteAsync`, `RuleStore.RecordBaselineAsync`; keeps `IfcModelContext`.
`ApplyAdded` / `ApplyModified` / `ApplyRemoved` build and apply one rule each;
`ApplyModified` also re-syncs hosted inserts of a re-converted host. `ModifyLevel` /
`RemoveLevel` handle storeys in place. `ReconcileVanished` / `ReconcileAll` handle events
without ids. Neo4j calls block through `Task.Run` (a direct await on Revit's UI thread
deadlocks).

**`LiveSyncLog.cs`**: append-only log, never throws.

**`Neo4jConfig.cs`**: `Resolve()` returns `(bolt URI, user, password)`.

### Revit to ggifc

**`Ifc/ModelAssembler.cs`**: `Build(doc)` = `BoilerplateBuilder.Build` +
`ElementConverterRegistry.ConvertAll`.

**`Ifc/BoilerplateBuilder.cs`**: the empty-project skeleton laid out like Revit's native
export; storeys, their pset and rel are tagged with the level id.

**`Ifc/IfcModelContext.cs`**: `Db`, `Building`, `BodyContext`, `StoreyByLevel`,
`ConvertedElements` (Revit id to principal `IfcElement`), `OwnerByStepId` (STEP id to
Revit id).

**`Ifc/StepIdWatermark.cs`**: `Current(db)` = highest allocated STEP id.

**`Ifc/Converters/ElementConverterRegistry.cs`**: converter list in priority order
(Level first, hosts before hosted). `ConvertAll`, `TryConvertOne`; `ConvertOne` tags every
new STEP id with the element (skipping `GraphRule.SharedResourceTypes`) and registers the
principal product in `ConvertedElements`.

**`Ifc/Converters/*Converter.cs`**: one per category. Each writes placement, BRep body,
`Pset_*Common`, storey containment, and stamps stable GlobalIds. `WindowConverter` /
`DoorConverter` add the opening chain through `Hosting/OpeningBuilder.cs`.
`LevelConverter` builds storeys for levels added live and updates them in place.
`PsetSources.cs` holds the Revit parameters behind `IsExternal` / `LoadBearing`.

**`Ifc/Geometry/BRepBodyBuilder.cs`**: solids to `IfcPolygonalFaceSet`, vertices in mm
relative to the element's placement origin.

**`Ifc/IfcGuidConverter.cs`**: `ForElement` (Revit's export GUID), `FromRevitUniqueId`
(string re-implementation for code without a `Document`), `FromSeed`.

**`Ifc/StableIds.cs`**: seeded GlobalIds for psets, `RelDefines`, containment,
aggregation, void / fill.

**`Ifc/RevitOwnerHistory.cs`**: overwrites ggifc's OwnerHistory defaults with the values
Revit's exporter writes (sources cited per constant from Autodesk/revit-ifc).

**`Ifc/Ifc4Schema.cs`**: IFC4 forward attributes per entity in EXPRESS order, from the
embedded `data/schema/ifc4_attributes.json`.

### ggifc to Neo4j

**`Cypher/Direct/EntityWalker.cs`**: one entity to `EntityData` (properties from the
STEP line, edges and inline children by reflection). **`StepLineParser.cs`**: Part 21
tokenizer and Python-compatible value formatting. **`NodeClassifier.cs`**: node kind and
label expression. **`P21Id.cs`**: `#123` conversions.

**`Cypher/Direct/CypherEmitter.cs`**: `WriteAsync` (wipe + snapshot), `ApplyRuleAsync`
(section 4), `WalkAll` / `WalkOwned`, and the `UNWIND ... MERGE / CREATE` writers shared
by snapshot, rule apply, rule storage and replay.

### Rule engine

**`Cypher/Direct/GraphRule.cs`**: `RuleOp`, `GraphRule`, `SharedResourceTypes`,
`GraphletExtractor` (`WalkNew`, `SpatialChanges`).

**`Cypher/Direct/LiveRuleBuilder.cs`**: `BuildUpsert`, `BuildLevelModify`, `BuildRemove`,
and the mirror-side `DetachFromContainment`, `DetachStorey`, `ForgetOwnership`. Revit-free.

**`Cypher/Direct/GraphletReader.cs`**: `GraphletCapture` (nodes, incoming glue, shared
deleted) read from the graph by `revit_element_id`, by p21, or by timestamp.

**`Cypher/Direct/GraphletDiff.cs`**: `Compare(L, R)` to `GraphletDiffOutcome` (`NoChange`,
`PropertyOnly`, `Partial`, `Structural`; match, pushouts, `PropertyChange` rows named on
both sides).

**`Cypher/Direct/ContextRef.cs`**, **`ContextResolver.cs`**: portable names in ConMan2's
path format; `ResolveAsync` (p21 to name, before the rule mutates anything) and
`FindAsync` (name to p21 in any graph).

**`Cypher/Direct/RuleStore.cs`**: `PersistAsync` (section 4, storage), `RecordBaselineAsync`,
the chain node and its counter.

**`Cypher/Direct/RuleReplayer.cs`**: `ReplayAsync`, `UndoAsync`, `CheckoutAsync`; one
transaction per rule; context resolved through the stored names.

### Bridge

**`Cypher/IfcSnippetSink.cs`**: writes the ggifc tree to a temp `.ifc` and runs
`tools/python/snippet_to_cypher.py` (ifcopenshell, ConMan2 `IfcGraphInterface`).
