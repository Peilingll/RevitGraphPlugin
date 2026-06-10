# 2026-06-09 — WallConverter (Step A + B): first per-element converter

**Branch:** `feat/dev` · **Commits:** `978afbc` (Step A), `b90adaa` (Step B)

Stage 2 of the professor's methodology: the first per-element converter. Satisfies
the getting-started deliverable — "newly added walls with BRep geometry and global
positioning" (IfcWall + IfcLocalPlacement + BRep + placement).

## Architecture — extensible converter library
```
IfcModelContext            db + Body sub-context + Level.Id→IfcBuildingStorey map
IElementConverter          { BuiltInCategory Category; Convert(Element, ctx) }
ElementConverterRegistry   dispatch: collect by category → Convert
WallConverter              first implementation (OST_Walls)
```
`BoilerplateBuilder.Build` now returns `IfcModelContext` (was `DatabaseIfc`).
`SyncCommand`: stage 1 boilerplate → `registry.ConvertAll(doc, ctx)` → Python bridge.
New element types = add one `IElementConverter` (the professor's "dedicated library").

## Step A — identity + placement + containment + Pset (no geometry)
- `IfcWall(storey, placement, null)` — host=storey auto-creates `IfcRelContainedInSpatialStructure`.
- GlobalId ← `wall.UniqueId`; Name `Basic Wall:<type>:<id>`; ObjectType `Basic Wall:<type>`; Tag ← ElementId.
- Placement: `IfcLocalPlacement(storey.ObjectPlacement, Axis2Placement3D(wall-origin in mm))`.
- `Pset_WallCommon` (IsExternal from WallType Function, LoadBearing) via `IfcRelDefinesByProperties`.

## Step B — BRep body geometry
- `wall.get_Geometry()` → Solid(s) → per Face `Triangulate()` → triangles.
- Vertices deduplicated, emitted in **mm, local to the wall origin**, **1-based** indices.
- `IfcCartesianPointList3D` + `IfcIndexedPolygonalFace` → `IfcPolygonalFaceSet`
  → `IfcShapeRepresentation(Body, Tessellation)` → `IfcProductDefinitionShape` → `wall.Representation`.

## Verification (test wall: 7.4 m × 290 mm × 8 m)
Wall node + 4 edges: OwnerHistory, ObjectPlacement, ContainedInSpatialStructure, DefinesByProperties.
Geometry chain intact:
```
IfcWall ─Representation→ IfcProductDefinitionShape ─Representations→ IfcShapeRepresentation
        ─Items→ IfcPolygonalFaceSet ─Coordinates→ IfcCartesianPointList3D
                                     ─Faces→ IfcIndexedPolygonalFace ×12
```
Box geometry correct: 8 vertices, 12 triangles; x 0→7400, y ±145 (290 thick), z 0→8000.

## Difference vs ConMan2 baseline (`01_one_wall`) — by design, not a bug
Node-level diff: REF=192, CAND=92; **12 node types identical, no schema mismatch**. Every
difference falls into three deliberate buckets:

| Bucket | Content |
|--------|---------|
| **Geometry representation** | We emit BRep (`IfcPolygonalFaceSet`+`IndexedPolygonalFace`); baseline is SweptSolid (`IfcExtrudedAreaSolid`×4, `ArbitraryClosedProfileDef`, `IndexedPolyCurve`, `CartesianPointList2D`, `ShapeAspect`). |
| **Deferred Tier 3** | materials, quantities (Qto), surface styles, IfcWallType, extra units, extra Psets, presentation layer (~100 nodes not yet emitted). |
| **Test/known** | different test wall (dims/id/GlobalId) + empty-model residuals (Building/Org/Person…). |

Node count ≈ entities the converter `new`s; the gap is "not built yet" + representation choice,
not a construction error. The output routes through ConMan2, so it follows ConMan2's **schema**
(labels/properties/edge format) by construction.

## Open decision (for the professor)
"follow ConMan2 structure" (schema) + "BRep geometry" + a **SweptSolid** baseline are in tension:
BRep ≠ SweptSolid baseline, permanently. BRep was chosen (professor's explicit word; natural from
Revit's raw API geometry; generalises to other element types). Whether to instead reconstruct a
SweptSolid to match the baseline is a scope decision to confirm with the professor before investing
in Tier 3.

## Next
- Clarify BRep vs SweptSolid with the professor.
- Tier 3 (materials / quantities / type / styles) — optional "enhanced functions", pending above.
- Shared geometry-point dedup, incremental `DocumentChanged` emission — later.
