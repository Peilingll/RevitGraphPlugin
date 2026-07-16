# Direct pipeline round-trip failure — root-cause diagnosis

**Date:** 2026-07-12
**Context:** Professor wants the C#-primary (direct) pipeline as the main path. The known blocker: after `SyncDirectCommand` writes the graph to Neo4j, ConMan2 `graph_2_ifc` cannot reconstruct a valid IFC file (viewer refused to open it). The exact error was never recorded — this session reproduced and root-caused it.

## Setup

- Neo4j Desktop 2026.04, instance `RevitGraphPlugin` (started headless via `~/.Neo4jDesktop2/Data/dbmss/dbms-63bcc082…/bin/neo4j.ps1 console` with the Desktop-bundled zulu21 JDK).
- Graph still contained both syncs of the same sample model: `plugin-bridge` (92 nodes) and `plugin-direct` (92 nodes) — a perfect A/B pair.
- ConMan2 `IfcGraphInterface.graph_2_ifc(path, timestamp)` run from `D:/Hiwi/ConMan2/venv` for both timestamps.

## Result

| timestamp | graph_2_ifc | ifcopenshell.validate |
|---|---|---|
| `plugin-bridge` | ✅ 5 791 bytes | 0 issues |
| `plugin-direct` | ❌ `OverflowError: setArgumentAsInt` | (n/a — crashes) |

With a tolerant monkeypatch (log-and-continue per attribute), `plugin-direct` produced **22 attribute-level failures** in 5 distinct classes, plus 9 downstream validation errors.

## Root causes (all in `Cypher/Direct/EntityWalker.cs` emission)

1. **ggifc unset-date sentinel leaks as int64.** `IfcOwnerHistory.LastModifiedDate = -62135596800` — that is `DateTime.MinValue` (0001-01-01) as epoch seconds, ggifc's representation of "unset". It exceeds INT32 range → `OverflowError`, killing the whole reconstruction. ConMan2 baseline stores `"$"`. **This is THE crash.**
2. **ggifc C# enum defaults that don't exist in the IFC4 schema.** `IfcOwnerHistory.State = "NOTDEFINED"` (IfcStateEnum has no NOTDEFINED), `IfcSIUnit.Prefix = "NONE"` (IfcSIPrefix has no NONE), `IfcPostalAddress.Purpose = "NOTDEFINED"` (IfcAddressTypeEnum has no NOTDEFINED). These are ggifc's own default enum members for "unset" → `Unable to find keyword in schema`. Baseline stores `"$"`.
3. **Number formatting in stringified lists.** Direct emits `(0,0,0)`; C# double→string drops the decimal point for whole numbers. ConMan2 round-trips lists via `ast.literal_eval` → tuple of **ints** → ifcopenshell rejects (`AGGREGATE OF DOUBLE` requires every element to be float). Verified: `(0.0, 0.0, 0.0)` OK, `(0, 0, 0)` and even `(-4213.26, -195.63, 0)` (one int element) fail. Baseline stores Python `str(tuple)` = `(0.0, 0.0, 0.0)`. Affects every `IfcCartesianPoint.Coordinates`, `IfcDirection.DirectionRatios`, `IfcCartesianPointList3D.CoordList`.
4. **String-list elements not quoted.** `IfcPostalAddress.AddressLines = "(Enter address here)"` → `ast.literal_eval` SyntaxError. Must be `('Enter address here',)` (Python repr style).
5. **Derived attributes emitted with computed values.** `IfcGeometricRepresentationSubContext.CoordinateSpaceDimension = 0` / `Precision = 1e-08` are `DERIVED` (`*`) in IFC4 → ifcopenshell refuses assignment. Baseline stores `"$"`. Same family: `IfcPolygonalFaceSet.Closed = False` where baseline has `"$"` (no crash, but semantic drift: unset ≠ FALSE).

Residual after all crashes are bypassed: `#28=IfcOrganization($,…)` fails validation because `Name` is **mandatory** — the direct walker collapsed `""` to `"$"`. This is the known ggifc lossy-getter problem (2026-05-28) showing up as a concrete validation error; baseline has `''`.

## Implication

Every failure is in the C# emission layer (`EntityWalker`), not in ConMan2 and not unfixable: sentinel/enum/derived cases need explicit "$" mapping, list emission needs Python-repr formatting (floats with decimal point, quoted strings). The lossy-getter `""` vs `"$"` distinction remains the one architectural item to decide (per-attribute mandatory-string handling vs backing-field access).

Diagnostic scripts: session scratchpad `graph2ifc_diag.py`, `graph2ifc_full_diag.py` (tolerant mode + validate).
