# Related Work — ConMan2, SpaceTracker, IfcInfraToolKit

## 1. Overview

The plugin under design targets a Revit 2026 → IFC → Neo4j pipeline: native Revit elements are translated into IFC entities and persisted as a property graph. Three prior projects each cover a distinct slice of this pipeline and serve as reference implementations.

| Project                     | Role                                                | Primary contribution                                                           |
| --------------------------- | --------------------------------------------------- | ------------------------------------------------------------------------------ |
| **ConMan2**          | IFC ↔ Neo4j round-trip, version diff and patch     | Neo4j graph schema design                                                      |
| **SpaceTracker**      | Revit add-in that mirrors Revit elements into Neo4j | Revit integration architecture (`IExternalApplication`, `DocumentChanged`) |
| **IfcInfraToolKit**  | Dynamo / Civil3D → IFC geometry exporter           | Concrete Revit-geometry → IFC entity mapping                                  |

The remainder of this chapter summarises each project's relevant internals and concludes with a comparative analysis that frames the design space for the new plugin.

---

## 2. ConMan2 — Neo4j schema reference

**Stack.** Python 3, `neomodel` (Neo4j ORM), `IfcOpenShell`. Top-level packages: `neo4j_core/`, `ifc_graph_interface/`, `graph_diff/`, `graph_patch/`.

### 2.1 Node taxonomy

```
GenericNode (abstract)
 ├── PrimaryNode      → IfcObjectDefinition / IfcPropertyDefinition  (carries GlobalId)
 ├── SecondaryNode    → other IFC entities                            (no GlobalId)
 ├── ConnectionNode   → IfcRelationship                               (carries GlobalId)
 └── InlineNode       → STEP entities with id == 0 (inline values)
```

Shared attributes: `EntityType` (required), `timestamp` (required), `p21_id`, and a type-conditional `GlobalId`.

### 2.2 Relationship model

A single polymorphic edge type is used:

- `[:rel {rel_type, list_index}]`
- `rel_type` carries the original IFC attribute name (e.g. `Representation`, `ObjectPlacement`, `IsDefinedBy`).
- `list_index` records position within an IFC list, allowing order-preserving reconstruction.
- Cross-version equivalence is tracked separately via `[:equivalent_to]`, generated during the diff phase by GlobalId pairing.

### 2.3 Key files

- `src/neo4j_core/neo4j_model.py:12-52` — `RelProperties`, `PrimaryNode`, `SecondaryNode`, `ConnectionNode` definitions.
- `src/ifc_graph_interface/IfcGraphInterface.py:241-252` — Cypher for `[:rel]` creation; `ifc_2_graph()` and `graph_2_ifc()` follow a three-phase write (create nodes → set properties → create relationships).
- `GraphDiff.py` — emits `[:equivalent_to]` edges.
- `GraphPatch.py` — semantic and topological patch logic; defines pushout nodes and unique paths used during reconstruction.

### 2.4 Implementation notes

- Property normalisation: `None → "$"`; lists are stringified as Python tuples (`(1,2,3)`); nested lists become stringified Python literals.
- Bulk write via `UNWIND` in batches of approximately 20,000.
- Required composite index: `(GenericNode.p21_id, GenericNode.timestamp)`.
- Patch reconstruction relies on a unique path string per node.

---

## 3. SpaceTracker — Revit add-in architecture reference

**Stack.** C# 9.0+ on .NET Framework 4.8, Revit 2022 add-in. Build output: `SpaceTracker.dll`. Dual backend: Neo4j 4.4.0 and SQLite (synchronously written from the same command stream).

### 3.1 Revit integration

- Entry point: `SpaceTrackerClass : IExternalApplication` (`SpaceTrackerClass.cs:16`).
- Event registration (`SpaceTrackerClass.cs:34-35`):

  ```csharp
  application.ControlledApplication.DocumentChanged
      += new EventHandler<DocumentChangedEventArgs>(documentChanged);
  ```
- Event branches:

  - `DocumentCreated` / `DocumentOpened` → full graph rebuild (preceded by `MATCH (n) DETACH DELETE n`).
  - `DocumentChanged` → incremental update; added/deleted/modified `ElementId`s are extracted and forwarded to `extractor.UpdateGraph(...)` (`SpaceTrackerClass.cs:65-74`).
- Tracked element types: `Level`, `Room`, `Wall`, `Door`.
- Wall-to-room boundaries are derived via Revit's `GetBoundarySegments()`.

### 3.2 Schema (named relationships)

Nodes: `Level`, `Room`, `Wall`, `Door`. Relationships are `MERGE`-style Cypher emitted from `SpaceExtractor.cs`:

- `Level-[:CONTAINS]->Room` (line 72)
- `Level-[:CONTAINS]->Wall-[:BOUNDS]->Room` (line 109)
- `Level-[:CONTAINS]->Door-[:CONTAINED_IN]->Wall` (line 157)

Edges carry no properties; semantics are encoded entirely in the relationship name.

### 3.3 Key files

| File                     | Role                                                               |
| ------------------------ | ------------------------------------------------------------------ |
| `SpaceTrackerClass.cs` | Lifecycle and event registration                                   |
| `SpaceExtractor.cs`    | `CreateInitialGraph` / `UpdateGraph` / `DeleteExistingGraph` |
| `Neo4jConnector.cs`    | Driver lifecycle, sync and async Cypher execution                  |
| `SQLiteConnector.cs`   | SQLite mirror (currently disabled via `_blockExecution=true`)    |
| `CommandManager.cs`    | `ObservableCollection`-based command queue                       |
| `SpaceTracker.addin`   | Revit manifest                                                     |

### 3.4 Technical debt to avoid

The following anti-patterns are documented for the reverse purpose — to be deliberately avoided in the new plugin:

1. **No temporal versioning.** Each update wipes the previous graph; only `neo4jcmds.txt` retains a command-level audit trail.
2. **Cypher-injection class of bug.** Cypher is assembled by string concatenation; no parameterised queries.
3. **Non-singleton `IDriver`.** A driver is constructed per query, equivalent to a memory leak under load.
4. **Hard-coded credentials and paths** (`neo4j/password`, `C:\sqlite_tmp\`).
5. **Unsanitised single quotes** in element names break Cypher syntax.
6. **Stale-edge cleanup gaps.** On modify, prior outgoing relationships are not fully detached before re-write.

---

## 4. IfcInfraToolKit — geometry export reference

**IFC library.** GeometryGym.Ifc v0.1.22, an OOP wrapper over the IFC4X3 schema. Length unit: millimetre (`IfcUnitAssignment.Length.Millimetre`).

### 4.1 Solids → BRep (`DynamoGeometryExporter.cs::AddSolidGeometryAsBRep`)

1. Extract vertices, edges, faces.
2. Translate coordinates so the centroid sits at the origin; offset Z to the bounding-box bottom.
3. Emit:
   - `IfcCartesianPointList3D` (shared vertex list)
   - one `IfcIndexedPolygonalFace` per face
   - aggregated into an `IfcPolygonalFaceSet`.

### 4.2 Mesh → triangulated set (`AddMeshGeometryAsBRep`)

- Mesh triangle indices are reused directly (0-based shifted to 1-based) and emitted as `IfcTriangulatedFaceSet`.
- Vertices stored in `IfcCartesianPointList3D`; index triples held in `Tuple<int,int,int>`.
- TODO carried over from the original implementation: meshes do not undergo centroid translation.

### 4.3 Civil3D TIN → `IfcTriangulatedIrregularNetwork`, attached to `IfcGeographicElement`.

### 4.4 Per-element IFC entity hierarchy

```
IfcProductDefinitionShape
 └─ IfcShapeRepresentation  (RepresentationType = "Tessellation" or "B-Rep")
     └─ IfcPolygonalFaceSet | IfcTriangulatedFaceSet
         └─ IfcCartesianPointList3D
IfcLocalPlacement → IfcAxis2Placement3D  (attached under site.ObjectPlacement)
```

### 4.5 Coordinate system and rotation

- Solid points are pre-translated to their centroid; the original origin is restored via `IfcLocalPlacement` anchored at site.
- Civil3D ingestion applies a −90° rotation to convert C3D's clockwise Y to IFC's counter-clockwise X.

### 4.6 Project layout

- `IfcInfraToolKit_Common` — core services (`ProjectSetupService`, `PropertyService`, `ElementService`, `IfcBaseService`).
- `IfcInfraToolKit_DynamoCore` — Dynamo nodes, including `DynamoGeometryExporter`.
- `IfcInfraToolKit_DynamoC3D` — Civil3D-specific nodes.
- `Alignment.cs` — horizontal (tangent / arc / clothoid) and vertical (constant / circular / parabolic) segments mapped to `IfcAlignmentHorizontal` / `IfcAlignmentVertical`.
- `ElementService.cs` — factory dictionary for 60+ IFC element types (Beam, Column, Wall, Door, Roof, and infrastructure-specific elements).

---

## 5. Comparative analysis

| Dimension          | ConMan2                                              | SpaceTracker                                                      |
| ------------------ | ---------------------------------------------------- | ----------------------------------------------------------------- |
| Relationship model | Single polymorphic `[:rel {rel_type, list_index}]` | Named edges (`[:CONTAINS]`, `[:BOUNDS]`, `[:CONTAINED_IN]`) |
| Semantic carrier   | Edge property `rel_type`                           | Relationship name                                                 |
| List ordering      | Preserved via `list_index`                         | Not tracked (schema is intentionally flat)                        |
| Primary use case   | IFC ↔ Neo4j round-trip plus diff / patch            | Revit document-change tracking                                    |
| Domain coverage    | Full IFC graph (60+ entity types)                    | Spatial hierarchy: 4 element types                                |
| Trigger model      | STEP-file traversal                                  | Revit `DocumentChanged` event                                   |

The new plugin must commit to a point in the design space delimited by these two extremes:

- **Polymorphic schema (ConMan2-style)** — favours generality, reversibility, and diffability at the cost of query ergonomics: every traversal goes through `WHERE r.rel_type = '…'`.
- **Named-edge schema (SpaceTracker-style)** — favours query ergonomics and BI / spatial use cases, but encodes domain knowledge in DDL rather than data, complicating evolution.
- **Hybrid** — adopt the ConMan2 three-tier node taxonomy and polymorphic edge as the canonical store, then materialise an additional named-edge layer (`CONTAINS`, `BOUNDS`) as an index for the hottest queries.

The geometry pipeline can adopt IfcInfraToolKit verbatim regardless of which schema choice is made: GeometryGym.Ifc, `IfcPolygonalFaceSet` for solids, `IfcTriangulatedFaceSet` for meshes, and `IfcLocalPlacement` rooted at site.
