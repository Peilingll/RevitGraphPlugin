# Live incremental sync — DocumentChanged → GraphRule → current-state graph

**Date:** 2026-07-19
**Plan:** `doc_process/2026-07-18-plan-live-incremental-sync.md` (substep 1–5).
**Goal:** the direct pipeline updates Neo4j immediately on each Revit edit — add a wall
and it appears, modify it and it updates in place, delete it and it disappears — with the
node count tracking the model, not growing per edit. This is Esser 2022 §3.3 option 1
(API callback), the shared "rule generation" core that option 2 (rule persistence) will
build on. Rules are generated and applied to a current-state graph now; persisting them
is the next stage.

## What shipped

- **Ownership tagging** (substep 1): each converter call is bracketed by a StepId watermark
  (`StepIdWatermark`), so every entity it creates is tagged with its Revit element id
  (`IfcModelContext.OwnerByStepId` → the graph's `revit_element_id` node property, inline
  nodes included). Shared boilerplate (storeys, units, contexts, owner history) is created
  before any converter and stays untagged.
- **Incremental primitives + rule model** (substep 2): `GraphRule` (Insert/Remove/Replace,
  graphlet + shared-context refresh + shared-delete — the option-2-ready payload),
  `GraphletExtractor`, and `CypherEmitter.ApplyRuleAsync` (one transaction: delete owned
  nodes → MERGE graphlet → replace shared containment edge set → drop emptied rels). The
  storey's `IfcRelContainedInSpatialStructure` is shared: it is excluded from ownership
  tagging and its membership is refreshed wholesale so list_index matches a fresh snapshot.
- **Session + rule builder** (substep 3): `LiveSyncSession` (baseline-then-increment, per
  document) and the Revit-free `LiveRuleBuilder` (change → rule).
- **Event wiring** (substep 4): third ribbon button (Live Sync toggle), `LiveSyncManager`
  routing `DocumentChanged` (deletes → adds → modifies; hosts before hosted).

## Two real bugs, both caught by verification (not by reading code)

1. **DocumentChanged document identity.** The first live test wrote nothing and raised no
   error. A file log (Revit forbids TaskDialog inside DocumentChanged — a swallowed
   exception looks like "nothing happened") showed `refMatch=False eqMatch=True`: Revit does
   not return the same managed `Document` reference from `DocumentChangedEventArgs.GetDocument()`
   as from `ActiveUIDocument.Document`. `ReferenceEquals` filtering dropped every change.
   Fixed by comparing with `.Equals`. (Memory: reference-revit-documentchanged-identity.)
2. **GlobalId collision.** With two walls, `graph_2_ifc` validation flagged IfcRoot.UR1:
   both walls shared one GlobalId. `IfcGuidConverter.FromRevitUniqueId` used only the 36-char
   episode GUID, which is document-wide; the element-distinguishing 8-hex suffix was dropped.
   Fixed by XORing the suffix into the GUID's trailing bytes (Revit's own scheme). Pre-existing
   — it hit the bridge/direct snapshot too, but every prior test had a single wall, and
   compare_neo4j masks GlobalId, so only the round-trip's uniqueness rule exposed it.

## Verification (empty project, live vs a fresh full snapshot)

| Operation              | Node count | Checks                                                                                                   |
| ---------------------- | ---------- | -------------------------------------------------------------------------------------------------------- |
| baseline (empty)       | 65         | boilerplate only, 0 owned, 0 containment                                                                 |
| draw wall              | 65 → 92    | wall graphlet (26 nodes) tagged; containment rel created, unowned, member idx 0                          |
| shorten wall           | 92 → 92    | **no growth**; 1 IfcWall (old graphlet gone); GlobalId stable; 0 orphans                                 |
| delete wall            | 92 → 65    | back to baseline; containment rel deleted (last member) via SharedDelete; 0 orphans                      |
| 2 walls + Sync direct  | 118 = 118  | **plugin-live ≡ plugin-direct**: 33 types, 159/159 relationships (incl. 2-member containment list_index) |
| round-trip plugin-live | —          | `graph_2_ifc` 7288 bytes, `ifcopenshell.validate` **0 issues**, 2 walls                                  |

The equivalence invariant is the core proof: an incrementally-maintained graph is
byte-identical (bar masked identity) to a from-scratch snapshot of the same model. The
round-trip catching the GlobalId bug that every graph-equivalence check masked is the
reminder that graph equality ≠ IFC validity.

## Deliberately deferred

- Rule persistence (option 2 proper — the `GraphRule` shape is ready for it).
- Property-level SET (modify is graphlet replacement for now).
- Modify-reorders-containment edge: detach+re-add moves a member to the list tail, so a
  modified element among mixed-type members could reorder list_index vs a fresh snapshot;
  `RelatedElements` is an unordered SET in EXPRESS, so it is semantically harmless — revisit
  the comparison's index handling only if a real case trips it.
- `LiveSyncLog` (diagnostic file log) is kept — cheap, swallows its own errors, and live
  features benefit from field diagnostics.
