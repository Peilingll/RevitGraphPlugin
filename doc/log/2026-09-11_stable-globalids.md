# Stable GlobalIds for synthetic IfcRoot nodes — the paper's precondition, met

**Date:** 2026-09-11
**Follows:** `2026-09-11_repeated-reversibility-and-identity-evidence.md` (why identity
churn matters) and `2026-08-10_rule-replay-and-undo.md` (the dual-name workaround).
**Goal:** Esser 2022 seeds its node matching on "unique identifiers assigned to each
primary node" (§3.3) and names unstable identifiers as the method's central domain
limitation (§5.2). The plugin met that precondition only for products, storeys, the
project / site / building and openings; every property set, `IfcRelDefinesByProperties`,
storey containment, aggregation and void / fill relationship got a ggifc-random GlobalId on
each conversion. Give all of them a deterministic identity.

## The bug that made this urgent

A probe written while answering "does the id switch affect checkout?" turned into a real
finding: **two consecutive property-only modifies on the same element, then checkout below
the second one, throws `context not found`.**

```
context not found in 'test-probe-2mod':
  [primary_node=1SMw0g6A99c87Q_BRShk3K]|[EntityType=IfcPropertySingleValue,list_index=0,rel_type=HasProperties]
  (nor [primary_node=2FIEMUS2v94vM1TGXYxXeU]|[...], nor #23)
```

Mechanism: a stored property change is addressed by a path anchored on the pset's
GlobalId (the value node has no id of its own and is not reachable forward from the
product). Modify k records the pset's L name, R name and R p21. The shallow apply of
Modify k+1 rebuilds the graphlet with a *third* random GlobalId and fresh p21s, so all
three of k's names point at nodes that no longer exist. Undo of k+1 works (its after-name
matches the live graph); undo of k fails. A change on the product itself (its Name) never
tripped this — the product's GlobalId is Revit-derived — which is why the 08-10 real-chain
run and every loop test passed: each modified an element exactly once.

## What shipped

- **`Ifc/StableIds.cs`** — `Seed(owner, role)` = `IfcGuidConverter.FromSeed(owner.GlobalId
  + ":" + role)`; the owner's GlobalId is itself derived from the Revit UniqueId, so the
  result is the same across conversions, sessions and machines. Same scheme
  `BoilerplateBuilder` already used for Site / Building and `OpeningBuilder` for openings.

  | entity | seeded from | role |
  |---|---|---|
  | `IfcPropertySet` | product / site / building / storey | `<pset name>` |
  | `IfcRelDefinesByProperties` | same owner | `RelDefines:<pset name>` |
  | `IfcRelContainedInSpatialStructure` | storey | `ContainsElements` (one rel per storey; every element stamps the same value) |
  | `IfcRelAggregates` | parent (project / site / building) | `Aggregates` |
  | `IfcRelVoidsElement` | opening | `RelVoids` |
  | `IfcRelFillsElement` | opening | `RelFills` |

- **8 converters**: `new IfcPropertySet` + `new IfcRelDefinesByProperties` replaced by
  `StableIds.AttachPset(product, name, props…)`; `StableIds.StampContainment(product)`
  right after the product's own GlobalId is set. **`BoilerplateBuilder`**: the 6 default
  psets the same way, `StampAggregates` on building and each storey, the explicit
  project→site `IfcRelAggregates` seeded inline. **`OpeningBuilder`**: fills rel seeded at
  creation, `StampVoids` for the auto-created voids rel.
- **Tests**: `StableIdsTests` (pure ggifc — same owner ⇒ same ids across rebuilds even when
  the value changes, different owners / roles ⇒ different ids, containment / aggregation
  seeded from the parent, opening rels reproducible across databases).
  `ConsecutiveModifyCheckoutTests` — the probe above, now built through `StableIds`,
  kept as the acceptance test: insert → toggle IsExternal → toggle again → checkout to
  seq 1, 4, 2.

## Verification

- `dotnet test` with Neo4j **up**: 99 passed, 0 failed, 0 skipped. Before the change the
  acceptance test failed exactly as quoted above; after it, checkout walks all four steps
  in both directions.
- Not yet verified in Revit: a live modify of a real wall's Function (IsExternal) twice,
  then `checkout.ps1` below the second change. Also to observe there: the **one-time
  migration** — the first modify of any element already in `plugin-live` deletes its
  random-id pset / rel nodes and rebuilds them with the seeded ids; stored rules are
  unaffected (their `P21Before` fallback still resolves).

## Gotcha caught on the way

ggifc does **not** maintain the `IfcOpeningElement.HasFillings` inverse: after
`new IfcRelFillsElement(opening, filler)` it stays empty, so a stamp that walks the
inverse silently does nothing. The fills rel is now seeded in the object initializer at
creation. `ContainedInStructure`, `Decomposes`, `IsDefinedBy` and `VoidsElement` are
maintained. Setting `GlobalId = ""` is ignored by ggifc, so the "owner without GlobalId"
guard in `Seed` is unreachable in practice.

## Where this leaves the rule chain

- `PropertyChange`'s four-name fallback and `P21Before` / `P21After` are now redundant
  for freshly recorded rules (L name == R name), but still needed to replay rules stored
  before this change. Leave them; they are correct either way.
- `GraphletDiff` still masks rel / pset GlobalIds when comparing L to R. With seeded ids
  the mask could go, and the pingpong signature could drop `GlobalId` from its masked
  columns — both are follow-ups, not blockers.
- Deep apply for `Modify` (`SET` in place instead of delete + rebuild, Esser Fig. 15) is
  the next step and now has its precondition: the node to `SET` is addressable by a
  path that never changes.
