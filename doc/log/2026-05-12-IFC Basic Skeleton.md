# IFC Basic Skeleton (Empty-Project Boilerplate)

**Date**: 2026-05-12 · **Branch**: `feat/dev` · **Final commit**: `390189e`

Pressing the plugin's Sync button on an empty Revit Architectural project writes 38 nodes / 54 edges of ConMan2-schema IFC4 boilerplate into Neo4j. 17 of 22 entity types in the ground-truth baseline are reproduced exactly; the remaining 24-node gap is concentrated entirely in Revit-IFC-exporter application metadata (PropertySets + PostalAddress + InlineNode value wrappers).

## Goal

Produce the IFC4 basic skeleton (spatial breakdown, units, geometric context, subcontext, ownership chain) once per Revit project, in the same labelled-property-graph shape ConMan2 produces.

## Method — diff-driven discovery

Empty Revit project → IFC export → ConMan2 import → graph captured as `data/samples/cypher/00_empty_neo4j_query_table_data.json`. The catalogue and counts in `data/samples/cypher/BASELINE.md` are the ground truth the plugin must reproduce. ConMan2's Python (`D:/Hiwi/ConMan2/src/ifc_graph_interface/IfcGraphInterface.py`) was read as a spec; its schema rules were reimplemented in C# — ConMan2 is not a runtime dependency.

## Architecture (Design A)

```
Revit Document
   │  read ProjectInformation + Levels
   ▼
GeometryGym.Ifc DatabaseIfc        ← in-memory IFC4 tree
   │  walk + classify per ConMan2 rules
   ▼
Cypher MERGE (3 batches by NodeKind) + MERGE edges as :rel
   ▼
Neo4j
```

## Code

```
src/RevitGraphPlugin/
├── Ifc/
│   ├── IfcGuidConverter.cs       Revit UniqueId → IFC 22-char base64 via ggifc EncodeGuid
│   └── BoilerplateBuilder.cs     Revit Document → DatabaseIfc with the boilerplate
└── Cypher/
    ├── NodeClassifier.cs         IfcObjectDefinition/PropertyDefinition → Primary,
    │                              IfcRelationship → Connection, other → Secondary
    ├── EntityWalker.cs           Reflection walker with deny-lists for ggifc internals
    │                              and IFC4 inverse rels; encodes null → "$",
    │                              primitive list → "(a,b,c)", enum → uppercase
    └── CypherEmitter.cs          Walks DatabaseIfc, batches MERGE per NodeKind, then
                                   MERGE edges. Keyed on (p21_id, timestamp) — idempotent.
```

## Iteration history

1. **Assumed ggifc auto-builds units/contexts.** `new IfcProject(db, name)` only auto-creates the OwnerHistory chain. Plugin produced 26 nodes; missing all units + context entities.
2. **Tried `new DatabaseIfc(true, ReleaseVersion.IFC4)` to pick up `db.Project`.** `db.Project` was null → `NullReferenceException`. The boolean in that constructor is **not** "initialize project".
3. **Reverted and added explicit Units + GeometricContext construction.** `IfcUnitAssignment` + 3 `IfcSIUnit`, then `db.Factory.GeometricRepresentationContext(...)` + 4 `db.Factory.SubContext(...)`. Plugin output: **38 nodes / 54 edges**.

## Plugin vs ground truth

| Group                                                                                                                                                                                                                                                          | Ground truth | Plugin |       Δ |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -----------: | -----: | ------: |
| 17 entity types — Project, Site, Building, Storey × 2, RelAggregates × 3, UnitAssignment, SIUnit × 3, Context, SubContext × 4, Direction, LocalPlacement × 4, Axis2Placement3D × 5, Organization × 2, Person, Application, PersonAndOrganization, OwnerHistory |           33 |     33 |       0 |
| IfcCartesianPoint (plugin doesn't share world origin)                                                                                                                                                                                                          |            2 |      5 |      +3 |
| **PropertySet chain** — IfcPropertySet × 7 + IfcRelDefinesByProperties × 7 + IfcPropertySingleValue × 6                                                                                                                                                        |           20 |      0 |     −20 |
| IfcPostalAddress                                                                                                                                                                                                                                               |            1 |      0 |      −1 |
| **InlineNode value wrappers** — IfcIdentifier × 2 + IfcLogical × 2 + IfcBoolean + IfcInteger                                                                                                                                                                   |            6 |      0 |      −6 |
| **Total**                                                                                                                                                                                                                                                      |       **62** | **38** | **−24** |

The −24 gap is entirely Revit-IFC-exporter application metadata, none of which is required by the IFC4 schema for a "valid" boilerplate.

## ggifc API findings (recorded in `reference_ggifc_api.md`)

- `new DatabaseIfc(true, ReleaseVersion.IFC4)` does **not** auto-create an `IfcProject`; `db.Project` is null. Always construct explicitly.
- `new IfcProject(db, name)` auto-creates only the OwnerHistory chain. Units and contexts are not auto-created.
- `db.Factory.GeometricRepresentationContext(...)` / `.SubContext(...)` auto-register on `project.RepresentationContexts`.
- `ReleaseVersion.IFC4` is marked Retired by ggifc but matches Revit's `FILE_SCHEMA('IFC4')`, so it's the correct value despite the obsolete warning.
- ggifc has no marker attribute distinguishing forward IFC schema attributes from inverse relationships; `EntityWalker` uses a hand-maintained deny list lifted from the IFC4 EXPRESS schema.

## Verification

1. Close Revit → `dotnet build -c Debug` deploys the new DLL.
2. `MATCH (n) DETACH DELETE n;` in Neo4j Browser.
3. Open Revit → new Architectural project (no elements) → click Sync.
4. Compare `MATCH (n) RETURN n.EntityType, count(*) ORDER BY count(*) DESC;` with `BASELINE.md`.

Captured plugin output: `data/samples/cypher/plugin_test_neo4j_query_table_data_2026-5-12.json`.

## Status

The basic skeleton ( spatial breakdown, units, geometric context, subcontexts) is fully reproduced. The remaining gap is application-level metadata Revit's IFC exporter chooses to add, not part of the IFC4 schema's required boilerplate.

## Deferred

| Item                                                            | When                                                    |
| --------------------------------------------------------------- | ------------------------------------------------------- |
| PropertySet chain (Pset\_\*Common attached to spatial elements) | Stage 6 cleanup, or sooner if cross-validation requires |
| IfcPostalAddress, shared world-origin point                     | with above                                              |
| InlineNode emission (StepId == 0 →`:InlineNode:Node`)           | with above                                              |
| Element pattern library (`WallPattern`, etc.)                   | Next step                                               |
| Exact Revit-IFC-exporter GlobalId algorithm (XOR-by-ElementId)  | when multi-instance validation needs it                 |
