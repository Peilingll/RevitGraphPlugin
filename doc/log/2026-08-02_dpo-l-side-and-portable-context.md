# Rule persistence steps 1–2 — the DPO L side, and portable context names

**Date:** 2026-08-02
**Plan:** `doc_process/2026-08-02-plan-rule-persistence.md` (steps 1–2 of 5).
**Commits:** `f900cb2` (L-side capture), `0037fbe` (portable context refs).
**Goal:** before rules can be *stored* (option 2 proper, step 3), each `GraphRule` must be
self-contained: it must remember what it destroyed (the DPO **L** side — today a Remove is
just `DETACH DELETE` with no record) and must name everything outside its own graphlet in a
way that survives a Revit restart or a foreign host graph (today those references are raw
p21, which neither does). Both are pure data-completeness work: whatever the final `:Rule`
schema looks like, it needs this payload — so they could ship before the schema questions
are settled. The verified apply behavior did not change in either step; both only capture
and annotate.

## What shipped

- **Step 1 — L-side capture** (`f900cb2`): `ApplyRuleAsync` on a Remove/Replace reads the
  about-to-be-deleted graphlet from the current-state graph *inside the same transaction,
  before* the `DETACH DELETE`, and returns the rule completed with
  `GraphRule.BeforeGraphlet` (`GraphletReader.ReadOwnedAsync` → `GraphletCapture`).
  `Nodes` reuses the insert-path `EntityData` shape, so a capture can be fed straight back
  into `CypherEmitter` to restore the graphlet. `NodeClassifier` recovers each node's
  `NodeKind` from its labels so the round trip preserves Primary/Connection/Generic.
- **Step 2 — portable context refs** (`0037fbe`): `GraphRule.PartitionReferences()` splits
  every p21 the rule mentions into *Own* (its R graphlet + captured L graphlet — local
  names, like node letters in a paper rule diagram) and *External* (glue targets, shared
  refresh/delete nodes). `ContextResolver.ResolveAsync` names each external node with a
  `ContextRef` — a GlobalId-bearing anchor plus a `rel_type/list_index/EntityType` step
  path, ConMan2's `create_unique_path_mappings()` mechanism — and `FindAsync` resolves a
  ref back to a node (ConMan2's `find_node_from_unique_path`). `ContextRef` serializes
  compatibly with ConMan2's `path_to_string` and re-parses (`TryParse`). `P21Id`
  centralizes the `"#123"` ↔ int helpers that were private in three places.

## Design decisions worth remembering

1. **Capture L from Neo4j, not from ggifc.** By rule-build time `DetachFromContainment` +
   `ForgetOwnership` have already mutated the in-memory mirror; re-walking it is
   order-sensitive and easy to get wrong. What the graph holds under
   `{timestamp, revit_element_id}` is, by definition, exactly what the delete is about to
   destroy.
2. **Incoming glue must be captured too** — found while implementing, not in the original
   design. `DETACH DELETE` also severs edges *into* the graphlet (e.g. the containment
   rel's `RelatedElements` edge), and those live on no captured node. Without them a
   restored L side would dangle unattached. Hence `GraphletCapture(Nodes, IncomingGlue)`;
   the glue is captured but deliberately not re-applied — restoring shared context is
   undo's job (plan step 5).
3. **The apply path keeps p21; portability is a property of the *stored* rule.** Apply runs
   against the very graph the rule was built from, where p21 is exact and free. So step 2
   annotates (`ContextRefs` alongside), it does not replace — the verified apply logic is
   untouched. The boundary: inside the graphlet p21 is a local name; the moment a
   reference crosses out, it needs a portable one.
4. **Measure before designing.** Against the real 338-node `plugin-live` graph, references
   leaving a wall graphlet come in exactly 6 shapes: both incoming-glue types
   (`IfcRelContainedInSpatialStructure`, `IfcRelVoidsElement`) and the host wall anchor
   directly by GlobalId (depth 0); only 3 boilerplate targets need a path walk
   (`IfcOwnerHistory` depth 1, `IfcLocalPlacement` depth 1,
   `IfcGeometricRepresentationSubContext` depth 3). `MaxPathDepth = 4` leaves one hop of
   slack instead of guessing a "safe" large bound.
5. **Determinism trap: equal-length path candidates.** `IfcOwnerHistory` has 32
   equal-length anchor paths (every IfcRoot points at it) and the count grows with the
   model. The first draft fetched 25 candidates into C# and picked the minimum — truncate
   *then* choose, which can drop the winner. Fixed by having Cypher impose the complete
   total order (depth → anchor kind → GlobalId → the three step lists element-wise) and
   `LIMIT 1`: the same graph now always yields the same ref.

## Verification (`ApplyRuleIntegrationTests`, `ContextRefTests`)

| Assertion | Meaning |
| --- | --- |
| Remove captures L with node count = owned count, kind + `revit_element_id` + GlobalId intact | the capture is complete, not a sample |
| `IncomingGlue` contains the containment rel's `RelatedElements` edge | the severed inbound edge is recorded |
| Insert yields `BeforeGraphlet == null` | an insert destroys nothing |
| A captured L re-applied as an insert restores node *and* edge counts, GlobalId stable | L is genuinely replayable — the undo primitive works |
| Every `PartitionReferences().External` p21 has a `ContextRef`; none anchors on an Own node | no stored rule can leak a bare p21, and no ref routes through the graphlet being created/destroyed |
| Every ref `FindAsync`s back to the p21 it names; serialize → `TryParse` round-trips | refs are resolvable and ConMan2-string-compatible |
| A Remove rule names its incoming-glue *source* portably, still resolvable after the rule ran | the glue's context end survives in portable form |

## Deliberately deferred

- `RuleStore` / the `:Rule` chain itself (step 3) — blocked on freezing the schema; the
  open question that actually shapes it is modify granularity (plan §8 Q4: property-only
  `Modify` vs always-Replace, i.e. whether our rules should be consumable by ConMan2's
  `apply_patch`, whose `Patch_Sema` half would otherwise stay empty). Recommendation and
  trade-offs are written up in the plan.
- Re-applying `IncomingGlue` (undo's shared-context restore, step 5).
- Type-parameter propagation (open-questions #2) is *prioritized next*, before step 3: it
  is a data-correctness bug (graph holds stale `IsExternal`), and its fix — expanding a
  type change into per-instance re-syncs — is also the highest-volume real source of
  property-only modifies, i.e. the test material for the Modify/Replace split.
  → **Fixed and closed-loop verified 2026-08-05** (commit `3d8d3ce`): `LiveSyncManager`
  expands a modified `ElementType` into per-instance `ApplyModified` calls; types
  themselves no longer reach a converter. Verified end to end: WallType Function change →
  log `type 45419 -> 1 instance(s)` → graph `IsExternal` = false → round-trip IFC
  `IFCBOOLEAN(.F.)`, validate 0 issues (the 7-20 `wall_modify4_interior.ifc` was valid
  but stale; the reconstruction now tells the truth).
