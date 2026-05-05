# Stage 4 — Incremental Synchronisation

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 4`

## Goal

Hook the Revit `DocumentChanged` event so the graph stays in lockstep with the document as the user edits, without further button clicks. The four design.md §4 Stage 5 sub-scenarios (draw wall → add window → move window → delete window) become observable directly in Neo4j after each Revit transaction commits.

## What was delivered

| File | Role |
| --- | --- |
| `src/RevitGraphPlugin/Sync/ElementKind.cs` | Stage-4-scope discriminator (Wall, Window). |
| `src/RevitGraphPlugin/Sync/ElementSyncState.cs` | Session-scoped `Dictionary<long, (string UniqueId, ElementKind)>`. Needed because `doc.GetElement(id)` returns null for already-deleted ids — we must remember the UniqueId before deletion. |
| `src/RevitGraphPlugin/Sync/IncrementalSync.cs` | Partitions `DocumentChangedEventArgs` into added / modified / deleted, dispatches each branch with type-specific cascade rules. |
| `src/RevitGraphPlugin/Cypher/Neo4jGraphWriter.cs` | Added `DeleteWallByTagAsync` (single-node DETACH DELETE) and `DeleteWindowCascadeAsync` (window + opening + RelVoids + RelFills traversal). |
| `src/RevitGraphPlugin/Conversion/RevitToIfcExporter.cs` | Added `ExportElement(Document, Element)` — exports the spatial hierarchy plus exactly one wall, or one wall + one window. Same construction order as `Export(doc)` so MERGE on `(p21_id, timestamp)` *intends* to be idempotent. |
| `src/RevitGraphPlugin/RevitGraphApp.cs` | `OnDocumentChanged` now dispatches into `IncrementalSync.Handle`. Singleton `ElementSyncState` lives on the app. |
| `src/RevitGraphPlugin/SyncCommand.cs` | After full export, walks the document and populates the state map so subsequent delete events can resolve. |

## Decisions and rationale

### Add / Modify / Delete branches with type-specific cascades

`IncrementalSync.Handle` walks the event's three id sets in order: deletes first (so a delete-then-recreate-with-same-tag scenario doesn't lose the new write), then adds, then modifies.

| Branch | Wall | Window |
| --- | --- | --- |
| **Added** | export wall + write | export host wall + window + opening + RelVoids + RelFills + write |
| **Modified** | skip (Stage 4 minimum) | DELETE-cascade through opening + RelVoids + RelFills, then re-add fresh |
| **Deleted** | DETACH DELETE wall by Tag (no cascade — Revit fires separate delete events for hosted family instances) | DELETE-cascade through opening + RelVoids + RelFills (wall persists) |

Wall modify is parked because its stable graph-side identity needs Tag-keyed MERGE rather than `(p21_id, timestamp)` MERGE; revisit when fixing the p21_id collision (below).

### Why a state map (not graph lookup) for delete resolution

`DocumentChanged` exposes `GetDeletedElementIds()` after the elements are gone. We need their `UniqueId` to find the right `Tag` in the graph. Three options were on the table:

1. **State map** — populate on add/modify, consult on delete. Simple, in-memory, doc-scoped.
2. **Graph reverse-lookup by `ElementId`** — would require storing `ElementId.Value` on graph nodes. Currently we only store `UniqueId` in `Tag`; tying the schema to Revit `ElementId` (which is local-to-doc and not stable across Revit sessions) is worse.
3. **Parse `ElementId` out of `UniqueId`** — Revit `UniqueId` is `<guid>-<8-hex element id>`. Cypher `WHERE n.Tag ENDS WITH $hex` works but risks false positives across docs and across the int↔long ElementId migration in Revit 2024+.

Picked option 1.

### Silent error handling in the event handler

`DocumentChanged` runs on the Revit UI thread inside an event-broadcast loop. Throwing from an event handler (or popping a `TaskDialog`) can destabilise the document. The handler swallows exceptions and keeps the timeout `Task.Wait` short (30 s). When a write fails, the user notices because the next graph query doesn't reflect their change — a worse UX than a TaskDialog but a safer integration. Logging will be added once a sink is decided.

### State map populated by the manual Sync button too

`SyncCommand` walks the document's walls and windows after a full export and seeds the state map. Without this, the first `DocumentChanged` after Revit launch can't resolve any delete (state is empty). The full-Sync-as-baseline pattern is consistent with how Stage 5 expects the user to begin a session.

## Verification — passed (with one known issue)

User ran the four design.md §4 Stage 5 sub-scenarios incrementally:

| Step | Action | Snapshot | Primary/Connection in graph |
| --- | --- | --- | --- |
| 1 | Draw wall + click Sync | `data/mvp_test/stage_4-1_*.json` | 1 IfcWall (p21_id=50) |
| 2 | Place window on wall | `stage_4-2_*.json` | Wall + Opening + Window + RelVoids + RelFills (5 nodes, 5 forward edges) |
| 3 | Move the window | `stage_4-3_*.json` | Same 5 nodes; opening / RelVoids / RelFills got fresh Neo4j identities, wall identity stable |
| 4 | Delete the window | `stage_4-4_*.json` | Only IfcWall remains; window cascade removed opening + both rels |

The DocumentChanged → partition → dispatch → write pipeline runs without further intervention.

## Known issue: p21_id collision corrupts a Secondary node across exports

`stage_4-2_*.json` row 1 shows a Frankenstein node:

```json
identity 39
EntityType: "IfcIndexedPolygonalFace"  // from Stage 4 add-window write
kind: "Secondary"
Tag: "69a168f4-...-0004cd3a"           // leftover from baseline IfcWall
p21_id: 50
Name: "Wall-Ext_..."                   // leftover
GlobalId: "35MFcj4HzDthqB6zZOiubS"     // leftover
```

The baseline export gave the wall `p21_id=50`. The subsequent window-add export rebuilt the IFC database with the same construction order *plus* the window, which shifted p21_id allocation: `p21_id=50` was now assigned to an `IfcIndexedPolygonalFace`. Phase 1 MERGE on `(p21_id=50, timestamp=0)` matched the existing baseline node and `SET` the new `EntityType` / `kind`, but the old wall's `Tag` / `Name` / `GlobalId` weren't part of the new property bag (Secondary nodes have empty property bags) so they linger as ghosts.

Corollary: the Primary/Connection nodes the spec verifies were *not* affected — they're always handled via delete-then-add, so their identities turn over cleanly each event. The corruption is restricted to Secondary nodes.

The earlier assumption — *"fixed construction order ⇒ stable p21_ids per Revit element"* — only holds when the exported subset is constant. As soon as `ExportElement(window)` adds opening/window/rels ahead of the wall's BRep, p21_id assignment shifts and prior Secondary writes become stranded.

**Fix paths (deferred):**
- Per-kind MERGE strategy: Primary by `Tag`, Connection by some derived key (e.g. `(rel_type, fromTag, toTag)`), Secondary by either always-fresh `(GlobalId)` or no MERGE + accept duplicate orphans cleaned up by a sweep.
- Tag-rooted Secondary purge: before each `ExportElement`-driven write, DELETE every Secondary reachable from the affected Tag, then re-MERGE.

## Anti-patterns avoided

| # | Anti-pattern | Avoided by |
| --- | --- | --- |
| 1 | Wipe-and-recreate everything on every event (SpaceTracker on Document opened) | Element-level partitioning; existing `:Project` and other-wall data untouched. |
| 6 | Stale-edge cleanup gaps on modify | Window modify uses explicit cascade DELETE before re-add; old IfcOpeningElement / IfcRelVoidsElement / IfcRelFillsElement edges fully gone before new ones MERGE in. |

## Open issues parked for Stage 5+

- Wall modify (geometry change) is currently a no-op. Either Tag-keyed MERGE or the orphan-Secondary cleanup will unblock it.
- The Frankenstein Secondary node bug above.
- No logging yet; failures in `OnDocumentChanged` are silent.
- The `IncrementalSync` runs synchronously on the UI thread. Heavy Revit transactions (e.g. importing a full model) would freeze the UI per element. `Application.Idling`-driven worker queue is the obvious next move.
- `OnDocumentOpened` is not handled — opening an existing project does not auto-populate the state map. User must press Sync once per session.

## Next step

Stage 5 — formal end-to-end validation script that exercises all four sub-scenarios in Revit, captures the expected graph at each step, and either resolves or quarantines the Frankenstein-Secondary issue before declaring the MVP done.
