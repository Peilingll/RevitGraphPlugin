# Stage 2 — IfcGraphMapper Core

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 2`

## Goal

Port the ConMan2 graph schema and write pipeline from Python to C#. The deliverable is the middle of the Revit→IFC→Neo4j pipeline: given an in-memory `GeometryGym.Ifc` entity tree, produce a `GraphBatch` (nodes + edges) and persist it to Neo4j with a three-phase write. Stage 3 will feed real IFC entities from the Revit converters; Stage 4 will trigger this from `DocumentChanged`.

## What was delivered

| File | Role |
| --- | --- |
| `src/RevitGraphPlugin/Graph/{GraphNode, GraphEdge, GraphBatch}.cs` | Pure-C# data structures mirroring ConMan2's `GenericNode` + polymorphic edge. |
| `src/RevitGraphPlugin/Mapping/PropertyNormaliser.cs` | `None → "$"`, list-of-primitives → tuple-string (related-work.md §2.4). |
| `src/RevitGraphPlugin/Mapping/IfcGraphMapper.cs` | Hand-coded handlers for `IfcWall`, `IfcWindow`, `IfcOpeningElement`, `IfcRelVoidsElement`, `IfcRelFillsElement`. Anything else falls through to a `Secondary` emit. |
| `src/RevitGraphPlugin/Cypher/Neo4jSchema.cs` | Idempotent `CREATE INDEX IF NOT EXISTS` for `(:GenericNode {p21_id, timestamp})`. |
| `src/RevitGraphPlugin/Cypher/Neo4jGraphWriter.cs` | Three-phase async write: MERGE nodes → SET properties → MERGE `[:rel]` edges. All parameterised, batched via `UNWIND`. |
| `tools/IfcWriteTest/` | Console harness — builds the Stage 5 "window on wall" scenario in-memory via GeometryGym.Ifc, runs the mapper + writer, prints the resulting batch. |

## Decisions and rationale

### Hand-coded entity handlers, not reflection

Spec is unambiguous: *"Initial scope: IfcWall, IfcWindow, IfcOpeningElement. Do not generalise across the full IFC entity set in this stage."* A reflection-based walker would be tempting but actively harmful here: GeometryGym.Ifc exposes inverse-relationship `SET<T>` properties (e.g. `IfcElement.HasOpenings`, `IfcWall.FillsVoids`) on the C# surface, and walking them would double-count edges. STEP-21's forward-attribute model — which ConMan2 follows — is what the schema captures. Hand-coding makes which properties become node properties and which become edges entirely explicit.

### Edges are STEP-21-forward only

`IfcRelVoidsElement` emits two outgoing edges:
- `[:rel {rel_type:"RelatingBuildingElement"}]` → `IfcWall`
- `[:rel {rel_type:"RelatedOpeningElement"}]` → `IfcOpeningElement`

`IfcRelFillsElement` emits the symmetrical two. The inverse `IfcWall.HasOpenings` shown in `design.md §4 Stage 5` step 2 is *not* materialised — it is one Cypher hop away via the relation node and storing it would require either reflection over inverse `SET<T>` properties or a second hand-coded pass. Materialised inverses can be added later if Stage 5 verification queries find the round-trip painful.

### Three-phase write keeps `MERGE` order-free

Phase 1 MERGEs every node identity by `(p21_id, timestamp)`. Phase 2 SETs the property bags. Phase 3 MERGEs every edge by matching both endpoints — guaranteed to exist after Phase 1, regardless of source-side traversal order. Splitting properties from identity also means re-running the writer on an updated entity replaces properties without churning edges (Stage 4's modify path will lean on this).

### `:GenericNode` label only; type and kind are properties

Phase 1 labels every row `:GenericNode`. Per-type labels (`:IfcWall`) and per-kind labels (`:PrimaryNode`) are not materialised because Cypher cannot construct labels from parameters without APOC. `EntityType` and `kind` go on the property bag instead — queries filter via `WHERE n.EntityType = 'IfcWall'`. APOC integration is deferred until needed.

### Composite index is the only required index

`CREATE INDEX generic_node_p21_timestamp IF NOT EXISTS FOR (n:GenericNode) ON (n.p21_id, n.timestamp)`. Every Phase 1/2/3 MATCH or MERGE goes through this key — no other index would be on a hot path until query-driven access patterns appear.

### Cypher is parameterised end-to-end

Every value enters Cypher via `$rows` / `$timestamp`. No string concatenation anywhere in the writer. Avoids `related-work.md §3.4` item 2 (SpaceTracker's injection class).

## Verification — passed

The harness `tools/IfcWriteTest` builds an in-memory IFC scenario:

```
Project → Site → Building → Storey
                            └ IfcWall
                                ├ (auto) IfcRelVoidsElement
                                │   └ IfcOpeningElement
                                │       └ (manual) IfcRelFillsElement
                                │           └ IfcWindow
                                └ ... auxiliary placement / aggregation entities
```

GeometryGym.Ifc auto-creates `IfcRelVoidsElement` when `IfcOpeningElement` is constructed with an `IfcElement` host, but **does not** auto-create `IfcRelFillsElement` for `IfcWindow`. The harness adds the latter explicitly. This is a quirk of the library, not of the IFC schema.

Run output (truncated):

```
Built batch: 30 nodes, 5 edges
  node #22  Primary    IfcWall                     GlobalId=1zrnUKZZH8u86$S3VYl9xL
  node #24  Primary    IfcOpeningElement           GlobalId=0rplHemAP5pgv1eCpIWtn8
  node #28  Connection IfcRelVoidsElement          GlobalId=2pTQJGytf2nwtYpWqyp9mA
  node #29  Primary    IfcWindow                   GlobalId=0eMGQktYrEjwPGThheIUJs
  node #30  Connection IfcRelFillsElement          GlobalId=37aAGABXDCkQSdMvftHupH
  edge #28 -[RelatingBuildingElement]-> #22
  edge #28 -[RelatedOpeningElement] -> #24
  edge #30 -[RelatingOpeningElement]-> #24
  edge #30 -[RelatedBuildingElement]-> #29
  edge #24 -[ObjectPlacement]      -> #27
```

Neo4j Browser confirmed the five Primary/Connection nodes and the five edges via:

```cypher
MATCH (n:GenericNode) WHERE n.kind IN ['Primary','Connection']
RETURN n.EntityType, n.p21_id, n.GlobalId, n.kind ORDER BY n.p21_id
MATCH (a)-[r:rel]->(b) RETURN a.EntityType, r.rel_type, b.EntityType
```

Composite index `generic_node_p21_timestamp` exists on `(:GenericNode {p21_id, timestamp})` after `Neo4jSchema.EnsureAsync` runs.

## What this is NOT

Stage 2 verifies the *middle* of the pipeline only. The test harness's IFC entities are constructed by C# code — they do not come from Revit. Stage 3 (Revit→IFC converters) and Stage 4 (`DocumentChanged` trigger) are still pending, after which Stage 5 is the actual end-to-end check.

## Anti-patterns avoided

| # | Anti-pattern | Avoided by |
| --- | --- | --- |
| 2 | Cypher injection | Every query parameterised via `UNWIND $rows`. |
| 3 | Per-query driver | Writer takes an `IDriver` injected by the caller (the singleton in Stage 1). |

## Open issues parked for later

- Inverse-attribute edges (e.g. `IfcWall -[:rel {HasOpenings}]-> IfcRelVoidsElement`) are not materialised. design.md §4 Stage 5 step 2 implies they are; the discrepancy is intentional for Stage 2. Decide before Stage 5.
- Per-type/per-kind labels need APOC. Considered when Stage 4's query patterns demand it.
- Properties on `Secondary` fallback nodes are empty — name and other primitive attributes of `IfcLocalPlacement`, `IfcProductDefinitionShape`, etc. are not extracted. Out of scope per "Do not generalise"; either stage 3 hand-adds them when needed, or a generic STEP-21 attribute walker is introduced when scope grows.
- Test harness builds against `Release` configuration to skip the `RevitGraphPlugin` post-build deploy target (which fails when Revit is open). Working as intended for now; a future improvement could split the deploy target into a separate MSBuild target invoked by Debug only.

## Next step

Stage 3 — Revit-to-IFC converters: `WallConverter` (`Autodesk.Revit.DB.Wall` → `IfcWall` with `IfcLocalPlacement` and a simplified `IfcPolygonalFaceSet`) and `WindowConverter` (`FamilyInstance(OST_Windows)` → `IfcWindow` with the surrounding `IfcOpeningElement` + `IfcRelVoidsElement` + `IfcRelFillsElement`). Geometry follows IfcInfraToolKit's `AddSolidGeometryAsBRep` pattern (related-work.md §4.1).
