# Handover — RevitGraphPlugin

Last updated 2026-09-13. Branch `feat/dev`. Read this first, then the README, then
`doc/spec/livesync-architecture.md`; the research logs in `doc/log/` record why each
decision was made.

## 1. What it is

A Revit add-in that mirrors a Revit document into Neo4j as an IFC property graph in the
[ConMan2](https://github.com/seb-esser/ConMan2) schema, **live**: every committed Revit
transaction becomes a graph transformation rule (Esser, Vilgertshofer & Borrmann 2022,
*Graph-based version control for asynchronous BIM collaboration*, §3.4) that is applied
to the current-state graph and **stored** in a `:Rule` chain in the same database. The
chain can be walked in both directions (`checkout.ps1`), and any version can be exported
as IFC. This is the paper's "option 1" (API callbacks, §3.3) — the alternative to
ConMan2's file-based diff of two exports.

## 2. What follows ConMan2 / the paper, and what the plugin adds

| Layer | Vocabulary | Source of truth |
|---|---|---|
| Current-state graph | labels `Node` ⊃ `GenericNode` ⊃ `PrimaryNode` / `ConnectionNode` / `SecondaryNode`, `InlineNode`; one edge type `rel {rel_type, list_index}`; node properties = IFC attributes (`$` unset) + `EntityType`, `p21_id`, `timestamp` | ConMan2 `neo4j_core/neo4j_model.py`; paper Fig. 13. Plugin side: `Cypher/Direct/NodeClassifier.cs`, `EntityWalker.cs` |
| Ownership | `revit_element_id` on every node an element's conversion created | plugin-only (ConMan2 ignores it); `ElementConverterRegistry.ConvertOne` |
| Rule chain | `:Rule`, `:Baseline`, `:RuleChain`, `:Change`, `:Glue`; edges `NEXT`, `HEAD`, `INSERTS`, `DELETES`, `SETS`, `GLUE` | plugin; `Cypher/Direct/RuleStore.cs`. Maps 1:1 onto ConMan2's `Patch_Topo` / `Patch_Sema` and the paper's L / I / R |
| Ops | `Insert`, `Remove`, `Modify`, `Replace` | plugin names for Revit's added / deleted / modified; paper: structural vs property modification |
| Identity | `GlobalId` from Revit's own export GUID (`ExportUtils.GetExportId`); synthetic nodes seeded from their owner (`Ifc/StableIds.cs`); `p21_id` is not an identity | paper §3.3 / §5.2 (matching needs stable identifiers), §3.6 (file ids are local) |

## 3. How to run

- Build / deploy / package: README (Quick start, Building for another Revit version,
  Packaging). `dotnet build -c Debug` deploys the add-in for Revit 2025 on this machine.
- Tests: `dotnet test tests\RevitGraphPlugin.Tests`. 110 tests; the Neo4j-backed ones
  are reported **Skipped** when `bolt://127.0.0.1:7687` is unreachable (a green run with
  17 skips has not exercised the graph). `ManualChainTools` is an opt-in harness
  (`CHAIN_TOOL=undo|replay|checkout|pingpong`).
- Live sync: Neo4j running, `NEO4J_LOCAL_PASSWORD` set (User scope), open a model, click
  **Live Sync**. Diagnostics: `%LOCALAPPDATA%\Temp\<session GUID>\RevitGraphPlugin\live.log`
  (newest folder).
- Versions: `.\checkout.ps1` (list) · `.\checkout.ps1 <seq>` · `.\checkout.ps1 head` ·
  `-Ifc file.ifc` exports (needs the ConMan2 venv as a sibling clone, see README).
- Acceptance against Revit: `python tools\python\compare_psets.py <native.ifc> --storeys`
  compares Pset values and storey containment of every element with a native Revit
  export of the same model. Fixtures: `data/samples/rvt/native*.rvt` + `data/samples/ifc/native*.ifc`.

## 4. State on handover (all verified in Revit 2025, Neo4j 2026.04)

Done and verified:
- Baseline + incremental sync for Wall, Floor, Column, Beam, Ceiling, Roof, Door, Window
  and **Level**; hosted openings (void / fill chain).
- Rule chain: insert / remove / modify / replace stored; **partial replace** — L and R
  are aligned, the interface I stays in place, only the pushout is copied and applied
  (`GraphletDiff.cs`). A window placed / moved / deleted: 8 rules, 745 nodes (was 11 / 1677).
- Checkout: one transaction per step, bookmark on the chain; IFC export at any version
  validates (ifcopenshell) on every chain built during handover; head ↔ baseline round
  trips 10× with no drift (`PingPongTests`).
- Identity: product GlobalIds equal Revit's IfcGUID parameter; pset / rel / containment /
  aggregation / opening ids are stable across re-conversions (`StableIds`).
- Pset sources (IsExternal / LoadBearing) and storey containment match Revit's native
  export on a single- and a three-storey model (0 differences).
- Revit undo / rollback (events without ids) reconciled; a storey emptied and re-used
  stays replayable.

## 5. Known gaps (ranked)

1. **STEP string escapes** `\X2\ … \X0\` (non-ASCII names) are not parsed —
   `StepLineParser.cs` fails loudly. First real problem with German or Chinese names.
2. **`ReconcileAll` is O(model)** per undo / rollback (every tracked element re-converted).
   Fine at demo scale; limit it to elements touched by the last few rules for big models.
3. **Conventions that differ from Revit's export** (same world geometry, not 1:1 aligned):
   elevation lives in the element placement (Revit: storey placement); a roof is one BRep
   (Revit: `IfcRoof` aggregating `IfcSlab` parts).
4. **Hosted insert geometry**: a window is 225 nodes (208 tessellated faces) copied on
   every Insert / Remove. Bounding-box representation would cut it to ~30 — a fidelity
   vs chain-size decision for the professor.
5. A change of edges between two interface nodes falls back to the whole-graphlet
   replace (correct, just fat); not seen on any real chain.
6. A level's Pset `Reference` (level type name) is not updated on a type change.
7. Cross-host portability: the first geometry-bearing element's
   `IfcGeometricRepresentationSubContext` glue is a raw `#p21`; fine in one database.
8. Bridge mode (`snippet_to_cypher.py`) supports CREATE only; it is a reference path.

## 6. Open questions for the professor

1. Should rules be exportable as ConMan2 patch files (`Patch_Topo` / `Patch_Sema`) so
   `apply_patch` can consume them? The stored shape maps 1:1; the adapter is ~half a day.
2. Hosted inserts: full family geometry or bounding box (gap 4)?
3. Align the two conventions in gap 3, or accept them?

## 7. Suggested order for whoever continues

1. Read the README, this file, the spec; run the tests with Neo4j up; build a small model
   with Live Sync ON and walk it with `checkout.ps1 -Ifc`.
2. Gap 1 (string escapes) — small, isolated, a real correctness issue.
3. Gap 2 (reconcile cost) — before any large model.
4. The professor's answers to §6 decide gaps 3 and 4.

## 8. Research logs (doc/log/)

2026-05 … 08: hybrid architecture, wall converter, STEP-line round trip, live incremental
sync, rule persistence steps 1–5, portable build. 2026-09-11 … 13: repeated
reversibility evidence, stable GlobalIds, Revit export GUID, partial replace, pset
sources, levels in live sync, long-chain findings (revival / undo). Each log states goal,
what shipped, verification, and what stayed open.
