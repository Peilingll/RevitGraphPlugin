# IFC Sample Dataset — Stage 1 (Diff-Driven Discovery)

These samples support Stage 1 of the v2 rebuild: empirically deriving the IFC boilerplate skeleton and per-element-type subgraphs by exporting Revit at different model states and diffing the resulting graphs in ConMan2.

## Source

All snapshots are exported from a **single Revit project** (`SampleProject_1.rvt`) by incremental edits. Maintaining one project keeps IFC `GlobalId` stable across snapshots, which is required for DPO patch derivation (`L → K → R`): modification and deletion patches only make sense when "the same element" persists across states.

## Tooling

- **Revit**: 2025
- **Project template**: Architectural (Imperial / Metric — record the actual one used)
- **IFC export preset**: `IFC 4 Reference View`
- **IFC version**: IFC4

All seven snapshots were exported in a single Revit session without restarting, to maximise ID stability.

## Snapshots

| File | Step | Cumulative model state |
| ---- | ---- | ----------------------- |
| `00_empty.ifc` | 0 | Empty project (Revit defaults: Level 1, Level 2; no elements) |
| `01_one_wall.ifc` | 1 | + Wall A (generic wall on Level 1) |
| `02_two_walls.ifc` | 2 | + Wall B |
| `03_wall_with_window.ifc` | 3 | + Window hosted on Wall A |
| `04_modified_wall.ifc` | 4 | Wall A length extended |
| `05_deleted_wall.ifc` | 5 | Wall B removed |
| `06_deleted_window.ifc` | 6 | Window removed |

## Intended diffs

| Pair | Reveals |
| ---- | ------- |
| `00 → 01` | The Wall subgraph (CREATE-only patch) |
| `01 → 02` | Whether the second wall reuses placement / representation context |
| `02 → 03` | Window subgraph + `IfcRelVoidsElement` / `IfcRelFillsElement` chain |
| `03 → 04` | Geometry modification patch (MATCH + CREATE + DELETE) |
| `04 → 05` | Wall deletion patch (MATCH + DELETE, the DPO `L \ K`) |
| `05 → 06` | Window deletion patch (MATCH + DELETE) |

Findings will be written up in `doc/log/2026-05-12-stage-1-ifc-boilerplate.md` (forthcoming).

## Re-export reproducibility

`SampleProject_1.rvt` is committed so any future stage can re-export the same model — for example, with a different IFC version, a different export preset, or to verify changes after a Revit update. To replay an intermediate state, open the project in Revit, undo back to that step, export, and discard the undo.

## What is NOT byte-stable

Two consecutive IFC exports of the same Revit project will differ in:

- STEP file header `timestamp`
- `IfcOwnerHistory.LastModifiedDate`
- P21 line numbers may re-order for some secondary entities

These are property-value or serialisation differences. The graph topology (entity `GlobalId`s + relationships) is stable. When diffing in ConMan2, ignore timestamp and `OwnerHistory` property changes.
