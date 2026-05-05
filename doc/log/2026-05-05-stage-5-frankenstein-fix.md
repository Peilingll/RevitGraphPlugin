# Stage 5 — Frankenstein Fix + End-to-End Validation

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 5`

## Goal

Close out the MVP. Stage 4 verified the four design.md §4 Stage 5 sub-scenarios but exposed a data-quality bug — a `:GenericNode` whose `EntityType` was changed by a later write while properties from the earlier write lingered (the "Frankenstein" node). Stage 5 replaces the brittle `(p21_id, timestamp)` MERGE key with a strategy-aware key per node kind, so a Revit element's graph identity is now stable across events.

## What was delivered

| File | Role |
| --- | --- |
| `doc/known-issues.md` | Single index of open issues / technical debt; tracks the Frankenstein bug as fixed and lists the remaining P2-P4 items. |
| `src/RevitGraphPlugin/Graph/GraphNode.cs` | Adds `MergeStrategy` enum (`ByTag`, `ByGlobalId`, `ByP21Id`) and a `Tag` accessor. The strategy is derived from `(Kind, Tag, GlobalId)` so the mapper does not need to set it explicitly. |
| `src/RevitGraphPlugin/Cypher/Neo4jSchema.cs` | Two new indexes: `(Tag, timestamp)` and `(GlobalId, timestamp)`. The original `(p21_id, timestamp)` index stays for Secondary nodes and edge endpoint lookups. |
| `src/RevitGraphPlugin/Cypher/Neo4jGraphWriter.cs` | Phase 1 split into three UNWIND queries — Tag, GlobalId, P21Id — executed in that order so primary p21_id updates land before any p21-keyed MERGE could collide. New phase 1.5 detaches outgoing `:rel` edges of Tag-keyed primaries before phase 3 re-emits them, satisfying the "modify must fully detach prior outgoing relationships" expectation from `related-work.md §3.4` item 6. |

## Decisions and rationale

### Tag for primaries, GlobalId for connections, p21_id only as fallback

`Tag` carries the Revit `UniqueId` (per Stage 3) and is stable for the lifetime of a Revit element. `MERGE (n:GenericNode {Tag: $tag, timestamp: $ts})` therefore returns the same Neo4j node every event for the same wall/window/opening — the new p21_id from this event's IFC database overwrites the old, but the node identity is preserved.

Connection nodes (`IfcRelVoidsElement`, `IfcRelFillsElement`) carry a fresh GG.Ifc-generated `GlobalId` per export, so MERGE-on-GlobalId effectively creates a new node each event. That's fine because Stage 4's window-modify and window-delete branches cascade-delete the previous instance before this write runs.

Secondary nodes (everything else — `IfcCartesianPoint`, `IfcLocalPlacement`, `IfcAxis2Placement3D`, etc.) keep `(p21_id, timestamp)` MERGE for backward compat with the Stage 2 fixture. They accumulate across events; orphan cleanup is parked as a P3 issue.

### Order of execution: Tag → GlobalId → P21Id

Within `WriteAsync`, the three phase-1 UNWINDs run sequentially. The Tag UNWIND runs first, which means it updates the wall's `p21_id` from (say) 50 to 70 before the P21Id UNWIND has a chance to MERGE on `p21_id=50`. Without this ordering, an `IfcIndexedPolygonalFace` whose new `p21_id=50` would land on the existing wall node — the original Frankenstein.

### Phase 1.5 detaches outgoing edges of Tag-keyed primaries

When MERGE finds an existing wall node, its old outgoing `:rel` edges (`ObjectPlacement` / `Representation`) point to stale Secondaries from a previous event. Phase 3 re-MERGEs new edges; without phase 1.5 the wall ends up with two `ObjectPlacement` edges pointing to two different `IfcLocalPlacement` nodes. Phase 1.5 deletes the old edges so phase 3's re-emit is clean.

Inbound edges (relationship → wall) are not detached by phase 1.5 — those are the relationship's outgoing edges, refreshed by the relationship's own cascade-delete-then-add cycle.

### Schema indexes added on Tag and GlobalId

MERGE-by-Tag and MERGE-by-GlobalId now run on every export; without dedicated indexes they would degrade to full label scans. Both indexes use `IF NOT EXISTS` so re-running `Neo4jSchema.EnsureAsync` is idempotent.

## Verification — passed

User repeated the four Stage 4 sub-scenarios on a fresh database. Snapshots in `data/mvp_test/stage_5-{1,2,3,4}_*.json`:

| Step | Action | IfcWall Neo4j identity | IfcWall p21_id | IfcWall GlobalId |
| --- | --- | --- | --- | --- |
| 5-1 | Draw wall + Sync | **42** | 50 | 1uspZkjg5FHeh4wvk81FdS |
| 5-2 | Place window | **42** | 70 | 0nW6XC0FH0Av7OQjLpflEX |
| 5-3 | Move window | **42** | 70 | 3amLjO8RjDk8PgaqttN5WF |
| 5-4 | Delete window | **42** | 70 | 3amLjO8RjDk8PgaqttN5WF |

The wall's Neo4j identity is **42 throughout** — Tag-keyed MERGE preserves identity exactly as designed. `p21_id` and `GlobalId` rotate from event to event because each `ExportElement` builds a fresh `DatabaseIfc` and GG.Ifc generates new GlobalIds, but the graph-side identity tracks Tag.

Connection nodes (RelVoidsElement, RelFillsElement) and the synthesised opening get fresh Neo4j identities each event because window-modify cascade-deletes them before re-add — exactly the design.md §4 Stage 5 step 4 semantics.

**Frankenstein audit:** in all four snapshots, no node carries a mismatched (EntityType, Tag) pair. Specifically, no Secondary node has a non-empty `Tag` property — confirming that Tag now exists exclusively on primaries and never bleeds onto secondaries.

## design.md §4 Stage 5 acceptance

| Step | Spec expectation | Stage 5 result |
| --- | --- | --- |
| 1 | Draw wall → `(:IfcWall {GlobalId})` node | ✓ |
| 2 | Insert window → wall + opening + window + IfcRelVoidsElement + IfcRelFillsElement subgraph with five `:rel` edges | ✓ |
| 3 | Delete window → window + opening + both IfcRel* gone, wall persists | ✓ |
| 4 | Resize window → window's OverallHeight / OverallWidth update | ✓ (delete-cascade-then-add semantics; final node carries the new values) |

The MVP closes here.

## Anti-patterns avoided

| # | Anti-pattern | Stage 5 contribution |
| --- | --- | --- |
| 6 | Stale-edge cleanup gaps on modify | Phase 1.5 explicitly detaches outgoing `:rel` edges of every Tag-keyed primary that MERGE matched. |

## Remaining issues (tracked in `doc/known-issues.md`)

- **P2** — Wall modify still no-op; the Stage 4 stub remains because exercising it on top of the new MERGE strategy is straightforward but unverified.
- **P2** — `Application.DocumentOpened` not wired; user must press Sync once per session before incremental events have state.
- **P3** — `OnDocumentChanged` failures swallowed silently; needs file-based logging.
- **P3** — Orphan Secondary nodes accumulate. Needs a sweep / GC pass that removes `kind = 'Secondary'` nodes with no incident `:rel` edges.
- **P4** — Several quality / coverage gaps (compound walls, opening geometry, GG.Ifc obsolete API).

## Next step

The MVP is functionally complete for the design.md §4 Stage 5 scenario. Beyond MVP, the obvious entry point is the P3 orphan-Secondary GC pass, then `Application.DocumentOpened` auto-baseline, then the wall-modify path (now unblocked by the Tag-keyed MERGE).
