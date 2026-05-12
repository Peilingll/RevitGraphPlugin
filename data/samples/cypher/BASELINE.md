# ConMan2 Import Baseline

Authoritative node and edge counts produced by importing the sample IFC files via ConMan2. These are the ground truth that the plugin's Cypher output is validated against (modulo known divergences listed below).

## Command

Run from `D:\Hiwi\ConMan2\src` with the local Python venv active:

```powershell
python conman2.py debugging_add -p "<path-to-ifc>" -t <timestamp>
```

`-t` is the ConMan2 timestamp (its version label). Each import keeps its own snapshot in the database.

## Project identity

Both imports below share the **same `IfcProject.GlobalId`** because both IFC files were exported from the same Revit project (`data/samples/rvt/SampleProject_1.rvt`). This is the identity anchor our `IfcGuidConverter` must reproduce.

```
IfcProject.GlobalId = 0dYwQmURH4PBK1viLNYMjR
```

Plugin verification: feed `SampleProject_1.rvt`'s `ProjectInformation.UniqueId` through `IfcGuidConverter.FromRevitUniqueId(...)` and check the output matches the string above. If it does not, the plugin's project node will not align with Revit-IFC-exported / ConMan2-imported graphs (the well-known Revit-IFC-exporter XOR-by-ElementId discrepancy).

## State 00 — empty project (`00_empty.ifc`, timestamp = "1")

| Node kind        | Count |
| ---------------- | ----: |
| PrimaryNode      |    12 |
| ConnectionNode   |    10 |
| SecondaryNode    |    46 |
| **Total nodes**  |    **68** |
| InlineNode       |     6 |
| **Relationships**|    **89** |

## State 01 — same project + one wall (`01_one_wall.ifc`, timestamp = "2")

| Node kind        | Count |
| ---------------- | ----: |
| PrimaryNode      |    19 |
| ConnectionNode   |    17 |
| SecondaryNode    |   144 |
| **Total nodes**  |   **180** |
| InlineNode       |    21 |
| **Relationships**|   **241** |

### Delta (one Revit Wall adds)

| Node kind       | Delta |
| --------------- | ----: |
| PrimaryNode     |    +7 |
| ConnectionNode  |    +7 |
| SecondaryNode   |   +98 |
| InlineNode      |   +15 |
| Relationships   |  +152 |

These deltas characterise the "Wall subgraph" that a `WallPattern` in the plugin must reproduce. Same single Revit element causes a ~127-node footprint in the graph (mostly geometry, material, and quantity chains).

## Known plugin-vs-baseline divergences

The current plugin (commit `fffdbe5`) is expected to undercount relative to this baseline for these reasons:

1. **InlineNode patterns are not emitted yet.** ConMan2 creates dedicated `:InlineNode:Node` for IFC value wrappers with `StepId == 0` (e.g., `IfcLogical(.T.)` inside `IfcPropertySingleValue.NominalValue`). Plugin's `CypherEmitter` skips entities with `StepId <= 0`. Gap: 6 nodes + their edges in empty state, 21 in one-wall state.

2. **`IfcPropertySet` chain (Pset_BuildingCommon, Pset_BuildingStoreyCommon, etc.) is not built.** Revit's IFC export auto-attaches several default property sets to spatial elements via `IfcRelDefinesByProperties`. `BoilerplateBuilder` does not replicate this yet. Gap: roughly 7 PrimaryNodes + 7 ConnectionNodes + many SecondaryNodes in empty state.

3. **GeometryGym.Ifc defaults may diverge structurally from Revit IFC export.** ggifc's `new IfcProject(db, name)` auto-creates an `IfcUnitAssignment`, geometric context tree, and `IfcOwnerHistory` chain, but their precise composition (number of `IfcSIUnit`s, subcontext count, conversion-based units) may not match Revit IFC export byte-for-byte. The plugin output is therefore expected to be schema-shaped but quantitatively close, not identical.

## Verifying plugin output

After clicking the plugin's Sync button, in Neo4j Browser:

```cypher
MATCH (n)
RETURN n.EntityType AS entity, count(*) AS n
ORDER BY n DESC;
```

Compare entity counts to the JSON dumps in this folder. Differences are expected per the divergences above; the goal at this stage is "plugin produces ConMan2-compatible **schema** (labels + property names + edge form)", not "byte-identical node counts".
