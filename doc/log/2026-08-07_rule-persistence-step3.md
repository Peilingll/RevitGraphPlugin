# Rule persistence step 3 — the :Rule chain, and Modify as a Replace shortcut

**Date:** 2026-08-07
**Plan:** `doc_process/2026-08-02-plan-rule-persistence.md` (step 3 of 5; steps 1–2 in
`doc/log/2026-08-02_dpo-l-side-and-portable-context.md`).
**Commits:** `6a39b75` (GraphletDiff), `3b82ad4` (RuleStore), `dd23b65` (observability);
prerequisite fix `3d8d3ce` (type-parameter propagation, 2026-08-05).
**Goal:** stop throwing rules away. Every applied `GraphRule` is now written into a
`:Rule` chain in the same Neo4j database — the professor's "produces … graph
transformation rules **and stores them** to Neo4j", the option-2 deliverable proper.
Property-only modifies are stored as a small `Modify` (ConMan2's semantic-patch shape)
instead of two full graphlet copies; the applied behavior is unchanged (shallow option:
the current-state graph is still delete+rebuild).

## What shipped

- **`GraphletDiff`** (`6a39b75`): pure C#, classifies a Replace by comparing the L
  capture with the fresh R walk — `NoChange` / `PropertyOnly(changes)` / `Structural`.
  Matching is seeded on stable GlobalIds (the product's derives from the Revit UniqueId)
  and propagated forward along `(rel_type, list_index)` (unique per source — an EXPRESS
  attribute) and backward where `(rel_type, list_index, source EntityType)` is unique —
  the only way to reach nodes like `IfcRelDefinesByProperties` that point AT the product
  and are pointed at by nothing. One global edge-multiset check settles structure.
  Masked: `p21_id`, `timestamp`, `revit_element_id`, `GlobalId` (ggifc regenerates
  rel/pset GlobalIds every conversion — same churn compare_neo4j masks), plus numeric
  normalization (Neo4j returns long where the walker produced int). Changed nodes are
  named by within-graphlet unique paths reusing `ContextRef`, same total order as
  `ContextResolver`. Anything that fails to align 1:1 → `Structural`: Modify is a
  shortcut, never a requirement.
- **`RuleStore.PersistAsync`** (`3b82ad4`): runs INSIDE `ApplyRuleAsync`'s transaction —
  apply and persist commit or fail together. Op-dependent payload:
  Insert → R copies; Remove → L copies; Replace → diff first: `PropertyOnly` → a
  `Modify` rule (op + `:Change` rows, no copies), `NoChange` → **not stored at all**
  (Revit fires modify events for changes no converter reads — a provably empty rule is
  noise), else the full Replace (L+R copies). Copies reuse the verified snapshot writers
  with only the timestamp swapped.
- **Schema as frozen, with three implementation refinements** (recorded in the plan):
  per-target namespaces `<target>-rule-<seq>` (per-target seq; test/multi-document
  isolation; cleanup via `STARTS WITH`), `-L`/`-R` sub-namespaces per DPO side (no p21
  coincidence can ever MERGE the two sides together), and `:Glue` nodes instead of
  string-array properties (a `ContextRef` serialization contains `|`, and nodes are
  queryable: "which rules glue to this wall?").

## The three-way boundary of a stored graphlet

Working out what `:Glue` must hold clarified a taxonomy the plan only implied. For a
stored copy, every edge is exactly one of:

1. **internal** — both ends in the graphlet: stored as a copy edge (BulkMergeEdges'
   two-sided MATCH makes boundary edges drop out for free);
2. **glue** — one end inside, the external end named by `ContextRef` in a `:Glue` row
   (`side` L/R, `direction` in/out, `local_p21` the inside end);
3. **shared-context internals** — edges of SharedRefresh rels between context nodes:
   not the rule's to store at all.

The integration test caught a real bug here on first run: **the first element on a
storey carries the freshly created containment rel inside its own graphlet**, so the
rel's `RelatedElements` edge to the wall is *internal* — the draft had recorded it as
glue, where it could get no portable name (own nodes are deliberately unnameable).
Glue is now defined as *one end inside, one end outside*, not "edges from SharedRefresh".

## Live verification (Revit, Project1 — the full lifecycle)

draw wall → set WallType Function to Interior → back to Exterior → delete wall:

| seq | op | payload | notes |
| --- | --- | ------- | ----- |
| 1 | Insert | 25 R copies + glue | full graphlet incl. BRep body |
| 2 | Modify | 1 `:Change` | `NominalValue: True→False`, path anchored on the pset GlobalId — the type-parameter scenario end-to-end |
| 3 | Modify | 1 `:Change` | `False→True` — a ready-made undo pair |
| 4 | Remove | 24 L copies | geometry included; 24 vs 25 = the shared containment rel, correctly NOT owned — it appears as portable in-glue instead |

Current-state graph back to the 65-node baseline, zero nodes left for the element,
chain of 4 intact — and `MATCH (n {timestamp:'plugin-live'})` returns exactly what it
returned before persistence existed (zero pollution), so `graph_2_ifc` round-trips are
byte-for-byte unaffected.

## Known limitation (found live, documented in the plan)

The **first geometry-bearing element in a model** cannot get a portable name for its
`IfcGeometricRepresentationSubContext` glue (stored as a raw p21 fallback, e.g. `#16`).
The measurement that sized `MaxPathDepth` found the subcontext reachable at depth 3 —
but every such path runs through *another element's* shape nodes, and the first element
has no other element to borrow; all routes lead through its own graphlet, which paths
must not enter. Replay/undo are unaffected (boilerplate p21s are stable within a
session); only cross-host portability of that one ref is degraded. Possible future fix:
a property-anchored `ContextRef` variant for boilerplate singletons
(`ContextIdentifier='Body'`).

## Deliberately deferred

- `(:RuleChain)-[:HEAD]` pointer, `[:NEXT]` links, `(:Baseline)` re-baseline anchors —
  step 4 (seq is currently `max(seq)+1` per target, fine single-threaded).
- Replay / undo closed loops — step 5, the final acceptance. Note for the comparison:
  a Modify-stored rule was still applied as delete+rebuild, so replay preserves original
  p21s where the live graph renumbered — the equivalence check must be structural with
  p21 masked (recorded in the plan since step-3 planning).
- Deep apply (`SET` in place instead of delete+rebuild for Modify) — optimization,
  option (b) in the plan.
- ConMan2 patch-file export adapter (`Patch_Sema`/`Patch_Topo` JSON) — the stored shape
  maps 1:1 (`Modify` ↔ semantic patch, copies+glue ↔ topological patch), format left
  unbound per the plan.
