# Handover — RevitGraphPlugin

Last updated 2026-09-13, branch `feat/dev`. Read this first, then the README, then
`doc/spec/livesync-architecture.md`.

## 1. Introduction

The plugin is a Revit add-in that mirrors a Revit document into Neo4j as an IFC property
graph in the [ConMan2](https://github.com/seb-esser/ConMan2) schema, live. Every committed
Revit transaction becomes a graph transformation rule (Esser, Vilgertshofer & Borrmann
2022, §3.4: L / I / R with context and glue). The rule is applied to the current-state
graph and stored in a `:Rule` chain in the same database. `checkout.ps1` moves the graph
to any recorded version, and any version can be exported as IFC. This is the paper's
option 1 (API callbacks, §3.3); ConMan2 implements option 2 (diff of two exports).

## 2. Vocabulary

| Layer | Names | Defined in |
|---|---|---|
| Current-state graph | Node labels `Node` ⊃ `GenericNode` ⊃ `PrimaryNode` / `ConnectionNode` / `SecondaryNode`, and `InlineNode`. One edge type `rel {rel_type, list_index}`. Node properties are the IFC attributes (`$` = unset) plus `EntityType`, `p21_id`, `timestamp`. | ConMan2 `neo4j_core/neo4j_model.py`; paper Fig. 13. Plugin side: `Cypher/Direct/NodeClassifier.cs`, `EntityWalker.cs` |
| Ownership | `revit_element_id` on every node an element's conversion created | Plugin. `ElementConverterRegistry.ConvertOne` |
| Rule chain | Labels `:Rule`, `:Baseline`, `:RuleChain`, `:Change`, `:Glue`. Edges `NEXT`, `HEAD`, `INSERTS`, `DELETES`, `SETS`, `GLUE`. | Plugin. `Cypher/Direct/RuleStore.cs`. Same content as ConMan2's `Patch_Topo` / `Patch_Sema` and the paper's L / I / R, stored in the graph instead of in files |
| Operations | `Insert`, `Remove`, `Modify`, `Replace` | Plugin names for Revit's added / deleted / modified. Paper: structural vs property modification |
| Identity | `GlobalId` from Revit's own export GUID (`ExportUtils.GetExportId`); synthetic nodes get deterministic ids from their owner (`Ifc/StableIds.cs`); `p21_id` is a file-local number, not an identity | Paper §3.3, §5.2, §3.6 |

## 3. Running

- Build and deploy: README, *Quick start*. `dotnet build -c Debug` deploys the add-in
  for Revit 2025.
- Tests: `dotnet test tests\RevitGraphPlugin.Tests`. 110 tests. The Neo4j-backed ones are
  reported as Skipped when `bolt://127.0.0.1:7687` is not reachable.
- Live sync: Neo4j running, `NEO4J_LOCAL_PASSWORD` set at User scope, open a model, click
  **Live Sync**. Log: `%LOCALAPPDATA%\Temp\<session GUID>\RevitGraphPlugin\live.log`
  (newest folder).
- Versions: `.\checkout.ps1` lists the chain, `.\checkout.ps1 <seq>` moves the graph,
  `.\checkout.ps1 head` returns to the newest version, `-Ifc file.ifc` exports (needs the
  ConMan2 venv as a sibling clone, see README).
- Check against Revit: `python tools\python\compare_psets.py <native.ifc> --storeys`
  compares property sets and storey containment with a native Revit export of the same
  model.

## 4. Status

Verified in Revit 2025 with Neo4j 2026.04:

- Baseline and incremental sync for Level, Wall, Floor, Column, Beam, Ceiling, Roof, Door,
  Window, including openings (void / fill chain).
- Rules stored for insert, remove, modify and replace. A replace keeps the unchanged part
  of the element in place and stores only what changed (`GraphletDiff.cs`).
- Checkout runs one transaction per step and records its position on the chain. IFC
  exported at any version validates. Repeated head ↔ baseline round trips show no drift.
- Product GlobalIds equal Revit's IfcGUID parameter; ids of property sets, relationships,
  containment, aggregation and openings are stable across re-conversions.
- Property set values (IsExternal, LoadBearing) and storey containment match Revit's
  native export on a single-storey and a three-storey model.
- Revit undo and rollback events are reconciled against the document.

Known limits: non-ASCII strings in STEP (`\X2\` escapes) are not parsed; the undo
reconciliation re-converts every tracked element; elevation is stored in the element
placement rather than the storey placement; a roof is one BRep rather than an aggregate
of slabs; a window carries its full family geometry on every insert and remove.
