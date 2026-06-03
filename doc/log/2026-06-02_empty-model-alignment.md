# 2026-06-02 — Empty-model alignment (Revit→ggifc verified against native)

## Goal
Verify the **Revit→ggifc** step by diffing the plugin's ggifc-authored IFC against
Revit's own native export of the same RVT (`00_empty`). Because ConMan2's
`ifc_2_graph` importer is **shared** on both sides, this equals aligning the
plugin's Neo4j graph with the ConMan2 baseline.

## Method — two-layer comparison (new tools)
- `tools/python/compare_ifc.py` — semantic IFC diff. Masks identity noise
  (GlobalId, OwnerHistory timestamps, `#id` numbering); reduces entity refs to
  type; keeps wrapped values. Multiset compare per EntityType.
- `tools/python/compare_neo4j.py` — graph-level diff of two Neo4j query-table-data
  JSON exports. Node multiset by EntityType (mask `p21_id`/`timestamp`/`GlobalId`)
  + relationship triples `(startType, rel_type, endType, list_index)`.
- In-DB cross-check: load baseline + plugin into one DB, told apart by the
  `timestamp` property (`baseline` vs `plugin-2`); no need for two databases.

Capture: plugin writes `%TEMP%\<guid>\RevitGraphPlugin_last_sync.ifc` (kept after
sync); the GUID folder changes per Revit session, so grab the newest by name.

## Fixes (all in `Ifc/BoilerplateBuilder.cs`)
| Tag | Change |
|-----|--------|
| P1  | Length unit `METRE`→`MILLI.METRE`; elevation `Meters`→`Millimeters`; context `Precision = 0.01` |
| P2  | `CompositionType=ELEMENT` on Site/Building/Storey; Storey `ObjectType`("Level:"+type) & `LongName`; Site `RefElevation=0` |
| P3  | `IfcProject.Name ← Revit Number`, `LongName ← Name`; Site Name `"Default Site"`→`"Default"` |
| P2b | Site `RefLatitude`/`RefLongitude` from `doc.SiteLocation` (radians→compound angle, **truncate** μs to match Revit) |

## Result
**15 node types identical, all 95 relationships identical.** 8 remaining diffs are
all known-acceptable — none is data loss:
- ggifc cannot serialise `""` (writes `$`): Building Name/LongName, PostalCode.
- Native orphan boilerplate: 10 axis `IfcDirection`, 1 `IfcClassification` (unreferenced).
- Harmless float tail: Storey elevation `3999.999…` vs clean `4000.0`.
- `IfcCartesianPoint` origin not deduplicated (plugin 5 vs native 3) — **deferred**.
- P4 OwnerHistory cosmetics: Org `Unknown` vs `''`, Person field placement.

## Key learnings
- **ConMan2 does not deduplicate** identical entities — it preserves IFC instance
  identity so it can round-trip faithfully. The CartesianPoint 5-vs-3 gap is a
  **ggifc authoring** trait (a fresh point per placement), not a ConMan2 issue.
- Revit stores internally in **imperial (feet)**; IFC output is **metric (mm)** —
  hence the unit conversion and the native elevation float crud.
- Differences split into *same value, different encoding* (float, `''`) vs
  *genuine choice* (names, placement strategy). Only the latter is worth chasing.
- `(n)-[r]->(m)` exports drop orphan nodes, so the Neo4j diff = IFC diff − orphans.

## Next
- `01_one_wall`: real wall geometry (BoilerplateBuilder currently builds only the
  empty skeleton).
- Design a **shared-geometry-point dedup** strategy together with wall geometry
  (resolves the CartesianPoint gap holistically rather than special-casing).
