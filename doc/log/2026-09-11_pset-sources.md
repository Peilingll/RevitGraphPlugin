# Pset sources verified against a native Revit export

**Date:** 2026-09-11
**Follows:** the `TODO(verify)` inventory in the handover review (open-questions §3, local)
and `2026-06-09_wall-converter.md` (the only converter whose Pset sources had been checked).
**Goal:** the graph's `Pset_*Common.IsExternal` / `LoadBearing` must equal what Revit's own
IFC exporter writes. Seven of eight converters carried hard-coded defaults with a
`TODO(verify)`; nothing downstream can detect a wrong value — the rule chain records and
replays it faithfully — so this is a data-correctness question only a native export can
answer.

## Method

One Revit model with every element type, each with a case that contradicts the plugin's
default: five walls (exterior and partition types, one with Structural ticked), a floor with
type Function = Exterior and Structural ticked, an architectural and a structural column,
a beam, a ceiling, a roof on Level 1, two doors and two windows of one family each — one in
the exterior wall, one in the partition. Exported natively (IFC4) to `data/native.ifc`,
synced by the plugin, compared element by element on the Revit element id (IFC `Tag` ↔
`revit_element_id`) with the new **`tools/python/compare_psets.py`** (exit 1 on any
difference — the acceptance tool for this class of TODO from now on).

## What the export showed (before the fix: 5 wrong values)

| element | property | native | plugin before | Revit's source |
|---|---|---|---|---|
| floor | IsExternal | True | False | floor **type** `Function` (Interior / Exterior) |
| floor | LoadBearing | True | False | instance **Structural** checkbox (`FLOOR_PARAM_IS_STRUCTURAL`); the converter read `STRUCTURAL_FLOOR_ANALYZES_AS` |
| architectural column | LoadBearing | *absent* | True | written for structural columns only (`FamilyInstance.StructuralType == Column`) |
| door (both) | IsExternal | True | False | door **type** `Function` — an "ExtDbl" family says Exterior even in a partition wall |
| window in partition | IsExternal | False | True | window families define no `Function` → the **host wall's** IsExternal |

Walls, beams, ceilings and the roof itself matched; the storey containment of all eight
types matched (single-storey model, roof on Level 1); placement Z is not double-counted.
Two convention differences were logged, not fixed: the plugin puts the level elevation in
the element placement (storey placement at 0), Revit puts it in the storey placement
(element local Z = 0) — same world coordinates; and Revit decomposes a roof into
`IfcSlab` parts under an empty `IfcRoof`, where the plugin writes one `IfcRoof` with a BRep.

## What shipped

- **`Ifc/Converters/PsetSources.cs`** — one place for the exporter's rules:
  `TypeFunctionIsExterior` (type `Function`, null when the family has none),
  `IsExternal(HostObject)` (walls, floors), `IsExternal(FamilyInstance)` (type `Function`,
  else host wall, else internal), `IsLoadBearing(Wall)` / `(Floor)` (Structural checkbox),
  `ColumnLoadBearing` (true for structural columns, null = omit for architectural).
- Floor, Column, Door, Window converters use it; Wall delegates to it (unchanged
  behaviour); Beam, Ceiling, Roof keep their defaults with the TODO replaced by a
  "verified" note. `TODO(verify)` count 22 → 8; the remaining ones are level-source
  markers already correct on this model (a multi-storey model would close them) and the
  two convention items above.

## Verification

Model rebuilt from scratch with Live Sync ON throughout (so the values also went through
the incremental path), native export redone, `compare_psets.py data/native.ifc`:
**23 rows, 0 differences** — including the interior door family (Function = Interior →
both doors False), the partition-wall window (False), the architectural column (no
LoadBearing on either side), the exterior structural floor (True / True).
