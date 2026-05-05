# Stage 3 — Revit-to-IFC Converters

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 3`

## Goal

Wire the front of the pipeline: Revit `Wall` and window `FamilyInstance` → in-memory `GeometryGym.Ifc` entity tree, then re-use the Stage 2 mapper + writer to land everything in Neo4j. After Stage 3 the manual `Sync current doc` button does a real Revit-to-graph round-trip — the precondition for Stage 4 (`DocumentChanged`-driven incremental sync) and Stage 5 (window-on-wall scenario).

## What was delivered

| File | Role |
| --- | --- |
| `src/RevitGraphPlugin/Conversion/GeometryHelpers.cs` | Revit `Solid` → `IfcCartesianPointList3D` + `IfcIndexedPolygonalFace[]` → `IfcPolygonalFaceSet`. Centroid-translated per IfcInfraToolKit (related-work.md §4.1). Feet→mm constant `304.8`. |
| `src/RevitGraphPlugin/Conversion/WallConverter.cs` | `Wall` → `IfcWall` with `IfcLocalPlacement` and tessellated BRep. `Tag = wall.UniqueId`. |
| `src/RevitGraphPlugin/Conversion/WindowConverter.cs` | window `FamilyInstance` → `IfcWindow` with synthesised `IfcOpeningElement`, manual `IfcRelFillsElement` (GG.Ifc auto-creates `IfcRelVoidsElement` only — Stage 2 finding). |
| `src/RevitGraphPlugin/Conversion/RevitToIfcExporter.cs` | Walks the active `Document`: spatial hierarchy + two-pass element walk (walls first, windows hosted on already-converted walls). |
| `src/RevitGraphPlugin/Mapping/IfcGraphMapper.cs` | Added `Tag` to wall/window/opening property bags so Stage 4 can resolve Revit `ElementId` → graph node via `WHERE n.Tag = $revitUniqueId`. |
| `src/RevitGraphPlugin/SyncCommand.cs` | Replaced project-only path with `Export → MapAll → Schema.EnsureAsync → Writer.WriteAsync`. 60 s timeout (geometry tessellation can be slow). |

## Decisions and rationale

### `Tag` carries Revit `UniqueId`, GlobalId is auto-generated

`IfcGloballyUniqueId.ConvertToIfcGuid(...)` does not exist in GeometryGymIFC 0.1.22 — the obvious approach (Revit GUID → IFC compressed 22-char form) is blocked. Workarounds considered: (a) implement Type-2 base64 conversion locally, (b) substring + leave as 36-char string in GlobalId, (c) skip GlobalId override and use a custom property.

Picked (c) routed through IFC's `Tag` slot. `Tag` is an `IfcIdentifier` on `IfcElement` exactly intended for foreign-key correlation with the originating CAD system — semantically the right place. GG.Ifc auto-generates a valid IFC GlobalId, and Stage 4's deletion lookup becomes:

```cypher
MATCH (n:GenericNode {Tag: $revitUniqueId}) DETACH DELETE n
```

No Type-2 conversion needed; `Tag` is queryable and matches Revit's identity model 1:1.

### Two-pass walk: walls first, then windows

A `FamilyInstance(OST_Windows).Host` is the wall it sits on. The window converter needs the host's already-built `IfcWall` to pass as the opening's host (so GG.Ifc auto-creates `IfcRelVoidsElement`). Stage 3 keeps a `Dictionary<ElementId, IfcWall>` from the first pass and looks up by host id in the second. Windows whose host is not a `Wall` (floor-mounted etc.) are skipped — out of scope per spec.

### Centroid translation off the IfcLocalPlacement

Per IfcInfraToolKit pattern (`AddSolidGeometryAsBRep`), the Solid's vertex cloud is translated so the centroid sits at the origin and Z anchors to the bounding-box bottom; the original world position is restored via the `IfcLocalPlacement`. This keeps the BRep coordinates small and stable (helpful for floating-point precision and any future serialisation).

### Triangle-as-polygon for `IfcPolygonalFaceSet`

Spec asks for "simplified `IfcPolygonalFaceSet` BRep". Revit's `Face.Triangulate()` returns a `Mesh` of triangles; each triangle becomes a 3-vertex `IfcIndexedPolygonalFace`. This is schema-valid (IFC polygonal faces have no triangle restriction) and avoids needing planar-face detection or N-gon assembly. The downside is more faces than a hand-trimmed mesh would produce — acceptable trade-off for Stage 3.

### Per-wall conversion blocks the UI thread, write doesn't

`SyncCommand` runs the Revit→IFC conversion *on* the Revit UI thread (the API requires it — `wall.get_Geometry(...)` is not thread-safe). The Neo4j write runs on the threadpool with `Task.Wait(60s)` — same UI-thread-protection pattern as Stage 1. A heavy document → long pre-write pause; not great UX but correct. Stage 4 will need to either chunk this through `Application.Idling` or accept a noticeable freeze on first full sync.

## Verification — passed

User scenario: blank Revit project + 1 wall + 1 window. After clicking the ribbon button, Neo4j contains the expected five `Primary`/`Connection` nodes:

| EntityType | p21_id | Tag | kind |
| --- | --- | --- | --- |
| `IfcWall` | 70 | `f8006b7c-…-0004cc41` (Revit UniqueId) | Primary |
| `IfcOpeningElement` | 214 | `""` (synthetic, no Revit backing) | Primary |
| `IfcRelVoidsElement` | 215 | null | Connection |
| `IfcWindow` | 216 | `f8006b7c-…-0004cc87` (Revit UniqueId) | Primary |
| `IfcRelFillsElement` | 218 | null | Connection |

The five edges of the design.md §4 Stage 5 subgraph are present (wall ↔ relVoids ↔ opening ← relFills → window).

The Wall and Window `Tag` properties carry the Revit `UniqueId` verbatim, confirming the Stage 4 lookup path will work. `IfcRelVoidsElement` and `IfcRelFillsElement` carry `null` in `Tag` because the mapper's relationship handlers do not emit it (Tag is `IfcElement.Tag`, not `IfcRoot.Tag` — relationships have no equivalent slot).

## Why so many secondary nodes

A Revit wall expands to **~200 IFC entities** when serialised: every BRep vertex is its own `IfcCartesianPoint`, every triangle its own `IfcIndexedPolygonalFace`, every product its own `IfcLocalPlacement`/`IfcAxis2Placement3D`/`IfcCartesianPoint` triple, plus the spatial chain (`IfcProject` → `IfcSite` → `IfcBuilding` → `IfcBuildingStorey` + auto-created `IfcRelAggregates` and `IfcRelContainedInSpatialStructure`). All map to `Secondary` nodes via the Stage 2 default handler — they store `EntityType` + `p21_id` only. The query `WHERE n.kind IN ['Primary','Connection']` filters down to the five domain-meaningful nodes.

## Stage 3 simplifications (open issues for later)

- **Compound walls**: `LargestSolid` picks the highest-volume `Solid` only. Multi-layer walls collapse to one BRep. Stage 3 scope per spec is "minimal BRep acceptable" — defer layer separation.
- **Opening geometry**: the synthesised `IfcOpeningElement` reuses the window's solid as its representation rather than computing a wall-cut box. Stage 5's verification checks the relationship structure, not the void volume; revisit if downstream consumers need accurate void geometry.
- **Stage 2 fixture nodes still in DB**: the IfcWriteTest harness left 30 nodes from earlier verification. They share the database but not the p21_id space (different IFC databases can collide; mitigated by a separate `timestamp` per export — currently always 0). Pre-Stage-4: clear with `MATCH (n:GenericNode) DETACH DELETE n` between sessions.
- **`MapAll` walks the entire IFC tree**: fine for full-document sync; Stage 4's incremental path will need `Map(BaseClassIfc)` plus deliberate handling of newly-created `IfcRelXxx` relationships.

## Anti-patterns avoided

| # | Anti-pattern | Avoided by |
| --- | --- | --- |
| 4 | Hard-coded paths/credentials | `Conversion/*` writes nothing to disk; Neo4j credentials still env-var only. |
| 5 | Unsanitised single quotes in element names | Names ride through `UNWIND $rows` parameters all the way to Cypher — never concatenated. Worked first try with the wall named `Basic Wall: Wall Type` and the window named `Window 1830 x 1220mm`. |

## Next step

Stage 4 — incremental sync. `OnDocumentChanged` partitions the payload into added / modified / deleted `ElementId` sets and dispatches:
- **Added** → `RevitToIfcExporter.ExportElement(element)` → `IfcGraphMapper.Map(...)` → `Neo4jGraphWriter.WriteAsync(...)` (just the new subgraph)
- **Deleted** → resolve `Tag` to graph node, `DETACH DELETE`
- **Modified** → detach outgoing `[:rel]` edges of the matched `Tag`, then re-run the converter + mapper + writer for that one element

Open question for Stage 4: where does the IFC database live between events? Currently `RevitToIfcExporter` builds a fresh `DatabaseIfc` per `Sync`. Incremental sync needs either (a) a session-scoped `DatabaseIfc` mirroring the Revit document, or (b) a per-event throwaway database with each entity getting fresh p21_ids. (b) is simpler if the graph keys on `Tag` instead of `(p21_id, timestamp)` — minor schema iteration to consider.
