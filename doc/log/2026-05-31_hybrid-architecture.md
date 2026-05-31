# Step 2 closeout — Hybrid C# / Python architecture

**Date:** 2026-05-31
**Branch:** `feat/dev`
**Plan:** [`doc_process/2026-05-29-architecture-revisit-ifc-snippets.md`](../../doc_process/2026-05-29-architecture-revisit-ifc-snippets.md)
**Previous logs:**
- [`2026-05-12_IFC Basic Skeleton.md`](2026.05.12_IFC%20Basic%20Skeleton.md)
- [`2026-05-19_schema-whitelist.md`](2026-05-19_schema-whitelist.md)
- [`2026-05-20_owner-history.md`](2026-05-20_owner-history.md)

## Why

After several Step-2 commits trying to align the OwnerHistory chain (`9abd694`, `e575c53`, `a592e24`, `11627a6`, `efebedd`, `085dcc4`), one issue refused to close cleanly: ggifc's .NET property getter for nullable string fields collapses `null`, the literal `"$"` sentinel, and the literal `""` all into `""` on read. The STEP serializer keeps the distinction (writes `$` vs `''` correctly) but the getter does not. A C# walker reading via the property API therefore could not reproduce baseline's `IfcOrganization #16.Name = ""` (literal empty string) — it could only produce `"UNKNOWN"` (pre-Fix B) or `"$"` (post-Fix B), never the true literal empty string.

This blocked Step 2 indefinitely until we re-read the professor's task description:

> directly produces IFC snippets … and stores them to Neo4j. The Neo4j Graph shall follow the structure that is produced by ConMan2.

ConMan2 reads IFC STEP via `ifcopenshell` (C++ via Python binding), which is canonical and lossless. The plugin's path went C# → ggifc tree → C# EntityWalker → Neo4j, reimplementing ConMan2's traversal in C# and inheriting ggifc's lossy getter along the way. The Step 2 problem was the architecture, not a missing edge case.

## Approach

Split the pipeline at the IFC STEP text boundary:

```
C# side (in Revit process, .NET-only territory):
  Revit Document  →  RevitOwnerHistory + element converters  →  ggifc tree
                                                              ↓ db.WriteFile()
                                                          IFC STEP text (temp .ifc)
                                                              ↓
Python subprocess (Python-only territory):
  ifc snippet  →  ifcopenshell parse  →  ConMan2 IfcGraphInterface  →  Neo4j
```

- The .NET side only does what only .NET can: read Revit's `Document`, build the ggifc tree, serialise it to STEP. ggifc's STEP output is correct (national/international IFC standard); only its property *getter* was lossy.
- The Python side uses `ifcopenshell` for parsing — the same parser ConMan2 uses for baseline generation, so schema-level alignment is by construction.
- The boundary is a temp .ifc file (debug-friendly choice; future iteration can swap to stdin/stdout pipe).
- ConMan2's `IfcGraphInterface.ifc_2_graph(...)` is imported (not exec'd via CLI). `CONMAN2_PATH` env var lets the import path move between machines; long-term plan vendors the file into the plugin repo.

The pure-C# `EntityWalker` / `CypherEmitter` / `Ifc4Schema` are now obsolete (they will be removed in a follow-up cleanup commit).

## Implementation

| Commit | Subject | What it did |
|---|---|---|
| TBA | Python bridge script | New `tools/python/snippet_to_cypher.py` — imports ConMan2's `IfcGraphInterface`, dispatches `CREATE` / `DELETE` / `UPDATE` actions; DELETE/UPDATE reserved for Step 3+ |
| TBA | C# `IfcSnippetSink` + sync wiring | New `src/RevitGraphPlugin/Cypher/IfcSnippetSink.cs` (spawns python.exe with temp .ifc); `SyncCommand.cs` swaps `CypherEmitter` for `IfcSnippetSink` |
| TBA | BoilerplateBuilder enrichment | `BoilerplateBuilder.cs` adds `IfcPostalAddress`, 6 deduplicated `IfcPropertySingleValue`, 7 `IfcPropertySet`, 7 `IfcRelDefinesByProperties` to match the default Pset_*Common layout Revit emits |

`CONMAN2_PATH` env var (default `D:\Hiwi\ConMan2\src`) and `NEO4J_LOCAL_PASSWORD` are read at runtime; both must be set as user-level env vars (not just per-shell) for Revit to inherit them.

## Result

Compared against `data/samples/cypher/00_empty_neo4j_query_table_data.json` (the ConMan2 baseline) via `MATCH (n)-[r]->(m) RETURN n,r,m` and de-duplication by `identity`:

| Entity type | Baseline | Plugin | Match |
|---|---|---|---|
| IfcApplication | 1 | 1 | ✓ |
| IfcAxis2Placement3D | 5 | 5 | ✓ |
| IfcBoolean | 1 | 1 | ✓ |
| IfcBuilding | 1 | 1 | ✓ |
| IfcBuildingStorey | 2 | 2 | ✓ |
| **IfcCartesianPoint** | **2** | **5** | **+3** |
| IfcDirection | 1 | 1 | ✓ |
| IfcGeometricRepresentationContext | 1 | 1 | ✓ |
| IfcGeometricRepresentationSubContext | 4 | 4 | ✓ |
| IfcIdentifier | 2 | 2 | ✓ |
| IfcInteger | 1 | 1 | ✓ |
| IfcLocalPlacement | 4 | 4 | ✓ |
| IfcLogical | 2 | 2 | ✓ |
| IfcOrganization | 2 | 2 | ✓ |
| IfcOwnerHistory | 1 | 1 | ✓ |
| IfcPerson | 1 | 1 | ✓ |
| IfcPersonAndOrganization | 1 | 1 | ✓ |
| IfcPostalAddress | 1 | 1 | ✓ |
| IfcProject | 1 | 1 | ✓ |
| IfcPropertySet | 7 | 7 | ✓ |
| IfcPropertySingleValue | 6 | 6 | ✓ |
| IfcRelAggregates | 3 | 3 | ✓ |
| IfcRelDefinesByProperties | 7 | 7 | ✓ |
| IfcSIUnit | 3 | 3 | ✓ |
| IfcSite | 1 | 1 | ✓ |
| IfcUnitAssignment | 1 | 1 | ✓ |
| **Relationships** | **95** | **95** | ✓ |

25 of 26 entity types align exactly. All 95 relationships align.

Property-level checks confirm the previously stuck values now produce baseline-correct output:

| Field | Pre-architecture (C# walker) | Post-architecture (this commit) | Baseline |
|---|---|---|---|
| `IfcOrganization #16.Name` (user org) | `"UNKNOWN"` | `""` | `""` |
| `IfcOrganization #16.Description` | `"UNKNOWN"` | `""` | `""` |
| `IfcPerson.FamilyName` | `"$"` | `""` | `""` |
| `IfcOwnerHistory.LastModifiedDate` | `-62135596800` | `"$"` | `"$"` |
| `IfcOwnerHistory.State` | `"NOTDEFINED"` | `"$"` | `"$"` |

## Known limitation — IfcCartesianPoint duplication

Plugin produces 5 `IfcCartesianPoint` entities versus baseline's 2 because ggifc auto-creates a fresh `IfcCartesianPoint` for every `IfcLocalPlacement` origin. Baseline reuses a single `(0,0,0)` point across Site / Building / Storey 1 / Context placements. Distinct values (Storey 2 elevation `(0,0,4000)`) are unique in both.

The graph remains structurally compatible:
- Relationship count matches exactly (95 = 95)
- All edges connect to a CartesianPoint with the correct coordinates
- Difference is purely a node-sharing pattern, not a property/value difference

Fixing this requires bypassing ggifc's auto-placement behaviour (manually constructing `IfcAxis2Placement3D` + shared origin). Tracked as a follow-up; not a blocker for Step 2 closeout.

## Performance

End-to-end sync (Sync button click → Neo4j updated) takes ~3–5 seconds:
- Python process cold start: ~1–2 s
- `ifcopenshell` + ConMan2 module imports: ~0.5–1 s
- IFC parse + traverse + bulk Neo4j writes: ~0.5–2 s

Acceptable for Step 2 (one-shot boilerplate). Step 3+ incremental sync (per element change) will need either a persistent Python daemon (socket IPC) or acceptance of the per-change latency. Decision deferred until Step 3 reveals actual workload patterns.

## What this unlocks

- Step 3 ("getting-started problem" — adding walls with BRep geometry) can now be built on top of the hybrid pipeline. Each Revit `DocumentChanged` event maps to a small IFC snippet emitted via the same `IfcSnippetSink` machinery; the Python script extends with `DELETE` / `UPDATE` dispatch.
- The C# side becomes solely a "Revit-to-IFC translation library" — the dedicated extensibility library the professor's task description asked for.
- `EntityWalker`, `CypherEmitter`, `Ifc4Schema`, `OwnerHistoryDiagnostic` can be removed in a cleanup pass (out of scope for this log).
