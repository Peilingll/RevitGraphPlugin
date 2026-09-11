# Levels in live sync — add, modify, remove as rules

**Date:** 2026-09-11
**Follows:** `2026-09-11_pset-sources.md` (the three-storey run that exposed the gap).
**Goal:** a level created, renamed, moved or deleted while Live Sync is ON must reach the
graph like any element change. Before: levels were `category='Levels' supported=False`,
so elements placed on a new level found no storey, their converter skipped them, and the
session still logged `applied=True` and stored empty Insert rules.

## Design

- **`LevelConverter`** (registered first, so a storey exists before anything contained in
  it converts): `Level` → `IfcBuildingStorey` under `IfcModelContext.Building`, GlobalId
  from the level, aggregation rel seeded by `StableIds`, its own
  `Pset_BuildingStoreyCommon`. Baseline storeys are still built by `BoilerplateBuilder`
  (with Revit's deduplicated Pset value layout — `LevelConverter.CreateStorey` is shared),
  and the converter is a no-op for levels that already have a storey.
- **Ownership**: baseline storeys, their Pset and the rel between them are now tagged with
  the level's id (`OwnerByStepId`); the shared Pset values stay untagged. That is what lets
  a live modify / remove capture the storey's L side by `revit_element_id`.
- **Add** → `Insert`: the storey graphlet, plus a shared refresh of the building's
  `IfcRelAggregates` (`GraphletExtractor.SpatialChanges` = containment rels +
  aggregation rel; `IfcRelAggregates` joined `SharedResourceTypes`, so a first-storey
  rel rides along untagged exactly like a first-element containment rel).
- **Modify** → the storey object is updated **in place** (`LevelConverter.UpdateStorey`:
  Name, LongName, Elevation, ObjectType) and its owned entities re-walked with their
  unchanged p21s (`LiveRuleBuilder.BuildLevelModify`); the partial-replace diff aligns
  everything and stores a `Modify`. A storey is never rebuilt — its containment rel and
  every element on it point at the object. Elements Revit moves with the level arrive as
  their own modifies.
- **Remove** → `Remove`: `LiveRuleBuilder.DetachStorey` takes the storey out of the
  aggregation and hands back its containment rel p21s for `SharedDelete`; the aggregation
  rel is refreshed, or dropped when the last storey goes. `LiveSyncManager` routes level
  deletions **last** so the elements Revit deletes with the level empty the containment
  rel first.
- **Loud failure**: a converter that produces no entities now logs
  `!! <op> <id> produced no entities — not applied`, returns `applied=False`, and stores
  nothing (`LiveSyncSession.Upsert`).

## Verification

- Unit (`LiveRuleBuilderTests`, no Revit / Neo4j): a level insert owns storey + pset but
  never the aggregation rel and refreshes it with members renumbered; a level modify
  re-walks exactly the owned p21s with the new values; a level remove drops the storey's
  containment rel, refreshes the aggregation, and drops the aggregation itself when the
  last storey goes. Suite: 107 passed, 1 skipped (opt-in harness).
- **Revit**, Live Sync ON throughout: add Level 3 → wall on it → move the level → rename
  it → delete it (Revit deletes the wall with it). Chain: Level `Insert` (8 copies: storey,
  pset, rel, placement chain, two values) → `Modify` Elevation → wall `Insert` → `Modify`
  Elevation → wall `Modify` (its Z followed the level, 12 changes) → `Modify` Name +
  LongName → wall `Remove` → level `Remove` (aggregation refreshed, containment rel in
  `shared_delete`). Graph back to the 65-node baseline.
- `checkout.ps1` 9 → 4 → 7 → 9 → 1 → head → 2 → 6 → head: storey names, aggregation
  edge counts and wall counts correct at every stop; IFC exported at 4, 7 and 9 validates
  with 0 issues.

## Open

- A level's Pset `Reference` (the level type name) is not updated on a type change.
- Deleting a level while Live Sync is OFF, then turning it ON, is covered by the
  re-baseline as before.
