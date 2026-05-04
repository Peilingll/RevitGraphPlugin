# Design Specification — Revit 2025 → IFC → Neo4j Plugin

## 1. Requirements

| Requirement | Value |
|---|---|
| Host platform | Revit 2025 (Autodesk SDK on .NET 8) |
| Graph schema | ConMan2-style polymorphic relations (`[:rel {rel_type, list_index}]`) |
| Trigger | Revit `DocumentChanged` (add / delete / modify) |
| Primary backend | Neo4j 5.x |
| Optional secondary view | RDF projection via Neosemantics (`n10s`), deferred |
| First validation case | "Place a window on a wall" |

The schema choice follows the rationale developed in `related-work.md §5`. The validation case is chosen because it exercises the three canonical IFC relation classes in one scenario (containment, voiding, filling), without requiring a large element-converter surface.

---

## 2. Knowledge-graph library evaluation

| Option | Nature | Fit |
|---|---|---|
| **RDFLib** | RDF / OWL triples, W3C semantic-web canonical stack | ★★★ Suitable for an IFC semantic layer (IfcOWL exists), but heavyweight to introduce up-front |
| **NetworkX** | In-memory Python graph, no persistence | ★ Analysis only; unfit as source-of-truth |
| **Kùzu / Memgraph** | Embedded graph DBs with Cypher compatibility | ★★ Only relevant if Neo4j is rejected |
| **Neosemantics (`n10s`)** | Official Neo4j plugin exposing an RDF view of a property graph | ★★★ **Recommended** — single store, RDF projection on demand |

**Decision.** Ship a single Neo4j backend first; layer `n10s` if and when IfcOWL semantic reasoning is needed. Reject premature dual-write: SpaceTracker's Neo4j + SQLite implementation has its SQLite path disabled (`_blockExecution = true`) precisely because the synchronisation cost outgrew the benefit.

---

## 3. System architecture

```
┌─────────────────── Revit 2026 ───────────────────┐
│                                                   │
│  IExternalApplication (RevitGraphPlugin)         │
│       │                                           │
│       ├── DocumentChanged event                  │
│       │       │                                   │
│       │       ▼                                   │
│       │   ChangeCollector                        │
│       │   (added / modified / deleted ElementIds)│
│       │                                           │
│       └── Element → IFC entity (in-memory)       │
│               │                                   │
│               ▼                                   │
│           IfcExporter (GeometryGym.Ifc)          │
│           (IfcWall, IfcWindow, IfcOpeningElement)│
│                                                   │
└──────────────────────┬────────────────────────────┘
                       │ in-memory IFC entity tree
                       ▼
            ┌────────────────────────┐
            │  IfcGraphMapper        │  ← port of ConMan2
            │  (3-phase ifc → graph) │     ifc_2_graph
            └──────────┬─────────────┘
                       │ parameterised Cypher
                       ▼
              ┌──────────────────┐
              │     Neo4j 5.x    │
              │  ConMan2 schema  │
              └──────────────────┘
                       │
                       ▼ (deferred)
              ┌──────────────────┐
              │ neosemantics/n10s│
              │  → RDF / OWL view│
              └──────────────────┘
```

Components:

- **`RevitGraphApp : IExternalApplication`** — owns the Revit lifecycle and the `Neo4jConnector` (singleton `IDriver`).
- **`ChangeCollector`** — partitions `DocumentChanged` payloads into added / modified / deleted `ElementId` sets.
- **`IfcExporter`** — Revit element → in-memory `GeometryGym.Ifc` entity tree.
- **`IfcGraphMapper`** — three-phase write that mirrors ConMan2's `ifc_2_graph`: create nodes → set properties → create `[:rel {rel_type, list_index}]` edges.

---

## 4. Implementation roadmap

### Stage 0 — Environment bootstrap (½ day)

1. Create `D:\Hiwi\RevitGraphPlugin` as the project root (already in place).
2. In Visual Studio, create a Class Library and reference:
   - `RevitAPI.dll`, `RevitAPIUI.dll` from the Revit 2025 install path (project default: `D:\Autodesk\Revit 2025\`)
   - `Neo4j.Driver` (NuGet)
   - `GeometryGym.Ifc` (NuGet)
3. Place a `.addin` manifest under `%AppData%\Autodesk\Revit\Addins\2025\`.
4. Provision a local Neo4j Desktop database; verify connectivity with a hello-world Cypher round-trip.

> Note: Revit 2025 targets **.NET 8**, not .NET Framework 4.8. Confirm against the installed Autodesk 2025 SDK before configuring the project file.

### Stage 1 — Plugin skeleton (1 day)

- `class RevitGraphApp : IExternalApplication`.
- `OnStartup` — register `DocumentChanged`; initialise `Neo4jConnector` with a **singleton `IDriver`** (avoiding the per-query construction pattern documented in `related-work.md §3.4`).
- `OnShutdown` — dispose the driver.
- A single ribbon button that triggers a manual "Sync current document" — keeps the test loop short before incremental sync exists.
- **Verification.** Open Revit, click the ribbon button, observe a `Project` node in Neo4j.

### Stage 2 — `IfcGraphMapper` core (2–3 days)

This stage ports the ConMan2 design from Python to C#:

- Node taxonomy: `PrimaryNode` / `SecondaryNode` / `ConnectionNode` / `InlineNode`.
- Three-phase write: nodes → properties → `[:rel {rel_type, list_index}]` edges.
- Property normalisation: `None → "$"`; lists serialised as tuple strings.
- **Mandatory composite index:** `CREATE INDEX ON :GenericNode(p21_id, timestamp)`.
- **Parameterised Cypher only:** `MATCH (n {p21_id: $pid})`. String concatenation is prohibited (avoid the SpaceTracker injection class).
- Initial scope: `IfcWall`, `IfcWindow`, `IfcOpeningElement`. Do not generalise across the full IFC entity set in this stage.

### Stage 3 — Revit-to-IFC converters (2–3 days)

- Initial element scope: `Wall` and `Window`.
- `WallConverter.cs` — `Autodesk.Revit.DB.Wall` → `IfcWall` with `IfcLocalPlacement` and a simplified `IfcPolygonalFaceSet` BRep.
- `WindowConverter.cs` — `FamilyInstance(OST_Windows)` → `IfcWindow`, with the host wall yielding `IfcOpeningElement` plus the relationships `IfcRelVoidsElement` and `IfcRelFillsElement`.
- Geometry follows IfcInfraToolKit's `AddSolidGeometryAsBRep` pattern (centroid translation, shared `IfcCartesianPointList3D`).

### Stage 4 — Incremental synchronisation (2 days)

`DocumentChanged` is partitioned into three branches:

- **Added** — run converter → mapper → `MERGE` into the graph.
- **Deleted** — resolve `ElementId` to its `PrimaryNode.GlobalId`; perform `DETACH DELETE` to remove the node and all incident `[:rel]` edges.
- **Modified** — detach outgoing `[:rel]` edges first, then re-run the mapper.

Temporal versioning (ConMan2's `timestamp` mechanism beyond the literal `0`) is **deferred**. The single-version path must be stable before snapshotting is layered on.

### Stage 5 — End-to-end validation: window on wall (½ day)

Concrete validation script:

1. Open Revit on a blank document; draw a wall. **Expected:** a `(:IfcWall {GlobalId})` node in Neo4j.
2. Insert a window in the wall. **Expected** edges:

   ```
   (IfcWall)-[:rel {rel_type:"HasOpenings"}]->(IfcRelVoidsElement)
            -[:rel {rel_type:"RelatedOpeningElement"}]->(IfcOpeningElement)
   (IfcOpeningElement)<-[:rel {rel_type:"RelatingOpeningElement"}]-(IfcRelFillsElement)
            -[:rel {rel_type:"RelatedBuildingElement"}]->(IfcWindow)
   ```

3. Delete the window. **Expected:** `IfcWindow`, `IfcOpeningElement`, and both `IfcRel*` nodes are removed; the `IfcWall` node persists.
4. Resize the window. **Expected:** the `IfcWindow`'s `OverallHeight` / `OverallWidth` properties update in place.

---

## 5. First-week task table

| Day | Task |
|---|---|
| 1 | Stage 0 — environment bootstrap; verify Revit 2026 .NET runtime |
| 2 | Stage 1 — skeleton + ribbon button + Neo4j hello-world |
| 3–4 | Stage 2 — `IfcGraphMapper` driving a single `IfcWall` end-to-end through three-phase write |
| 5 | Stage 3 — `WallConverter` (minimal BRep acceptable) |
| Weekend | `IfcWindow` plus the opening / void / fill relation triple |

---

## 6. Accepted design decisions

1. **Pure polymorphic schema.** No hybrid named edges in v1. Apply YAGNI; revisit only if a measured spatial-query performance bottleneck appears.
2. **Defer RDF.** RDFLib is out of scope. Layer `n10s` later if IfcOWL semantic reasoning becomes a requirement.
3. **`timestamp` field reserved from day one.** Persist a literal `0` initially. Reserving the field at schema-creation time avoids a future migration when temporal versioning is introduced.
4. **Validation-case rationale.** "Window on wall" is selected because it exercises three canonical IFC relation classes in one scenario:
   - spatial containment (wall contains opening),
   - aggregation / voiding (opening voids wall),
   - filling (window fills opening).
   Other element types are largely variants of the same patterns.

---

## 7. Out of scope (explicitly deferred)

- Temporal versioning and snapshotting beyond a literal `timestamp = 0`.
- Dual backends (Neo4j + RDFLib, Neo4j + SQLite).
- Full IFC entity coverage beyond `IfcWall` / `IfcWindow` / `IfcOpeningElement` and their immediate relations.
- Diff / patch UI on top of the graph.
- Authentication and permission management.
- Ribbon UI beyond the manual-sync placeholder button.
