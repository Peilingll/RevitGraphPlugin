# Known Issues / Technical Debt

Open issues across stages, ordered by severity. Updated when stages close or issues land.

## Severity P1 — data quality

### `p21_id` collision corrupts Secondary nodes (Frankenstein)

**Status:** fixed in Stage 5 by switching primary nodes to Tag-keyed MERGE and connection nodes to GlobalId-keyed MERGE; only Secondary nodes still merge on `(p21_id, timestamp)`.

**Original symptom:** stage_4-2 row 1 — a single Neo4j node carried `EntityType: "IfcIndexedPolygonalFace"` (from the second export) plus leftover `Tag` / `Name` / `GlobalId` from the first wall write. See `doc/log/2026-05-05-stage-4-incremental-sync.md` §"Known issue".

**Root cause:** `(p21_id, timestamp)` is not a stable identifier for a Revit element across exports — adding a window before the wall in `ExportElement(window)` shifts every subsequent p21_id allocation. MERGE therefore matched the *wrong* node from a previous event and overlaid mismatched properties.

## Severity P2 — incomplete spec coverage

### Wall modify is a no-op

`IncrementalSync` skips Modified-Wall events. The user must press the Sync ribbon button to refresh wall geometry after editing in Revit. Conditional on the Tag-keyed MERGE (P1 above) being in place.

### `Application.DocumentOpened` not handled

Opening an existing Revit project does not auto-populate `ElementSyncState`. Until the user presses Sync once, subsequent DocumentChanged delete events silently no-op (state map can't resolve the ElementId). Workaround documented; auto-baseline on document open is the obvious fix.

### Inverse-attribute edges (`HasOpenings` etc.) not materialised

`design.md §4 Stage 5` step 2 shows `(IfcWall)-[:rel {rel_type:"HasOpenings"}]->(IfcRelVoidsElement)`; we only materialise the STEP-21 forward attribute (`RelatingBuildingElement`) in the opposite direction. Verification queries in our research logs filter by either direction and pass, but the spec wording disagrees with the implementation.

## Severity P3 — UX / observability

### `OnDocumentChanged` failures are silent

Errors thrown during incremental sync are swallowed because TaskDialogs and exceptions inside Revit's transaction broadcast can destabilise the document. Users notice failure only by query results not changing. A file-based log (e.g. `%AppData%\Autodesk\Revit\Addins\2025\RevitGraphPlugin.log`) would surface root causes without UI calls.

### Stage 4 sync runs synchronously on the UI thread

A heavy Revit transaction (importing a model, group instance edits) runs the converter + Neo4j write per element on the main thread. UI freezes per element up to 30 s. `Application.Idling` worker queue is the standard remedy.

### Orphan Secondary node accumulation

Each `ExportElement` event creates fresh Secondary nodes (IfcCartesianPoint, IfcLocalPlacement, etc.). Tag-keyed MERGE for primaries no longer corrupts them, but stale Secondaries from past events linger. Graph grows monotonically until a sweep/GC pass cleans nodes with `kind = 'Secondary'` AND no incident `:rel` edges.

## Severity P4 — quality / nice-to-have

### NU1701 warnings on GeometryGymIFC and Newtonsoft.Json

GeometryGymIFC 0.1.22 (no .NET 8 target, only .NET Framework 4.x) and its transitive Newtonsoft.Json 6.0.3. Working in practice on .NET 8 via assembly compatibility. A newer GG.Ifc fork or local rebuild for net8.0-windows would silence the warnings.

### `DatabaseIfc(bool, ReleaseVersion)` ctor obsolete

GG.Ifc deprecates this ctor; we still use it. Replace with the recommended factory at the same time as any GG.Ifc version bump.

### Compound walls collapse to largest Solid

`WallConverter` picks the largest Solid only. Multi-layer walls become single-layer in IFC. Sufficient for spec but a real conversion needs per-layer handling.

### Opening geometry approximates the window's solid

Synthesised `IfcOpeningElement` reuses the window's solid as its representation rather than computing a wall-cut box. `design.md §4 Stage 5` only checks the relationship structure, so this is invisible to verification, but downstream consumers expecting actual void volume would be wrong.

### Test fixture (`tools/IfcWriteTest`) leaks state across runs

Stage 2's console harness writes 30 nodes per run with no `MATCH ... DETACH DELETE` between sessions. Operator must clear manually before re-running for clean verification.

---

Last reviewed: 2026-05-05 (Stage 5 entry).
