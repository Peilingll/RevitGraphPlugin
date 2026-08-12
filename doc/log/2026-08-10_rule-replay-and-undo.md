# Rule persistence step 5 — replay, undo, and the closed loop

**Date:** 2026-08-10
**Plan:** `doc_process/2026-08-02-plan-rule-persistence.md` §7 (step 5 of 5 — the final
acceptance). Steps 1–2: `doc/log/2026-08-02_dpo-l-side-and-portable-context.md`;
step 3: `doc/log/2026-08-07_rule-persistence-step3.md`.
**Commits:** `0223fe2` (step 4: chain + baseline anchors), `29efd50` (RuleReplayer),
`1c4af14`, `9db3fa4` (both found by live data), `93d81b0` (manual harness).
**Goal:** consume the chain. A stored history is only worth what can be done with it, and
the plan's acceptance was never a unit test: **replay** the chain over a baseline copy
must reproduce the current-state graph, and **undo** must take the current-state graph
back to its baseline. Passing both proves the chain is a lossless record of the editing
history — the option-2 deliverable, end to end.

## What shipped

- **`RuleReplayer.ReplayAsync`**: walk `[:NEXT]` from the newest `(:Baseline)` anchor,
  re-apply each stored rule to a target graph — Insert/Replace merge the `-R` copies and
  re-create their glue, Modify applies its `:Change` rows forward, Remove deletes the
  element's graphlet plus the shared nodes it emptied. One transaction: a partial replay
  is a graph matching no version.
- **`RuleReplayer.UndoAsync`**: walk backwards from `HEAD`, inverting each rule (Insert
  → delete, Remove → restore from `-L` copies, Modify → apply changes in reverse,
  Replace → delete R, restore L). Stops at a `(:Baseline)` anchor — the graph was rebuilt
  there, so earlier rules cannot be unwound across it. The walk is stateless (undoing
  does not pop history), so a caller continuing a partial undo passes `belowSeq`.
- Context resolution goes through `ContextResolver.FindAsync` throughout — the same
  GlobalId + unique-path machinery a foreign host graph would use, not p21 shortcuts.
- **`ManualChainTools`** (`93d81b0`): an opt-in harness (`CHAIN_TOOL=undo|replay`), inert
  under a normal `dotnet test`. RuleReplayer has no UI yet and the automated loops clean
  up after themselves, so this is how a human drives real chains and watches the result
  in Neo4j Browser / `graph2ifc`.

## Three gaps the loops exposed

Each was a silent hole in earlier steps that only closed-loop consumption could reveal:

1. **The L side was incomplete: `SharedDelete` nodes were never captured.** Step 1
   captured what the element *owned*; the containment rel a removal empties is not owned,
   so it was deleted with no record. Replay therefore left a stale rel behind, and undo of
   a Remove could restore the wall but not the rel it emptied. Fixed by reading those
   nodes by p21 into `GraphletCapture.SharedDeleted` (they join the `-L` copies) and
   storing their portable names in the rule's `shared_delete` array.
2. **Undoing an Insert by p21 alone missed renumbered nodes.** A later Modify-stored rule
   was still *applied* as delete+rebuild (the shallow option), so the live graph may hold
   different p21s than the stored copies. Undo now deletes by element id *and* by copy
   p21 — the id catches renumbered nodes, the p21 catches untagged riders (a first
   element's containment rel) that have no element id.
3. **A context ref must not anchor on a node the same rule deletes.** Undoing a Remove
   failed on an `IfcOwnerHistory` ref whose shortest path ran *through* the containment
   rel that rule had emptied — unresolvable by construction. `ResolveAsync`'s exclude set
   now covers `SharedDelete` nodes as well as the graphlet (they still name *themselves*:
   direct IfcRoot anchoring walks no path).

## The one bug only live data could find

The automated loops passed while the real `plugin-live` chain did not: undoing a Modify
threw `context not found`. The cause is a genuine subtlety of the shallow apply, and worth
stating as a rule of its own:

> A stored change names a node by an anchored path — but **the same node wears different
> ggifc-generated GlobalIds in different graphs.** The L capture anchors on the pset
> GlobalId as it was *before* the rule; the live graph, rebuilt by the shallow
> delete+rebuild apply, carries the *R* walk's fresh one. A single name cannot address
> both.

The synthetic tests missed it because they replayed onto a graph built from stored copies
(L-shaped) and undid a chain whose Modify was the last rule — both directions happened to
match the L name. Fixed by storing both names on each `:Change` (`path` / `path_after`),
with replay preferring the before-name, undo the after-name, each falling back to the
other. (This is the same GlobalId churn `GraphletDiff` masks when *comparing* — here it
had to be handled when *addressing*.)

## Verification

**Automated** (`RuleReplayTests`, 91/91 with the rest of the suite): the main loop
(insert ×2 → rename stored as Modify → remove ⇒ replay ≡ live, undo ≡ baseline; the end
state deliberately isn't the baseline, so a do-nothing replay cannot pass), the
`SharedDelete` loop in both directions, and undo stopping at a baseline anchor.
Comparison is the structural signature (EntityType + edge triples with counts), p21
masked — the shallow apply renumbers where replay does not.

**Live, against a real Revit session's chain** (Project1: draw wall → set WallType
Function → delete wall), driven through the harness with `graph2ifc` after every step:

| step | live graph | round trip |
| ---- | ---------- | ---------- |
| start (post-delete) | 65 nodes | `live_final.ifc`, 0 issues — the reference |
| undo Remove | 65 → **92**: wall, geometry and the containment rel back from the `-L` copies; `IsExternal` = True, the value of that moment | 0 issues |
| undo Modify | 92; `IsExternal` → **False** (the pre-change value) | 0 issues |
| replay whole chain | 92 → **65** | **line-for-line identical to the reference** (only the FILE_NAME timestamp differs) |

Both live-only bugs above were walked over by this run: the Modify undo exercised the
dual-name fallback, and the Remove undo restored its rel through `shared_delete` +
`SharedDeleted`.

## Where this leaves the plan

All five steps are done: rules are generated, applied, **stored**, replayable and
undoable, with the current-state graph provably unpolluted (`graph_2_ifc` output is
unchanged by persistence existing). Still deliberately open:

- Deep apply for Modify (`SET` in place instead of delete+rebuild) — plan option (b).
  Would also retire the dual-name workaround above, since p21s and GlobalIds would stop
  churning on a property-only edit.
- Cross-host portability of one ref shape: the first geometry-bearing element's
  `IfcGeometricRepresentationSubContext` glue still falls back to a raw p21
  (documented in step 3; replay/undo in the same database are unaffected).
- A ConMan2 patch-file export adapter — the stored shape maps 1:1 (`Modify` ↔
  `Patch_Sema`, copies + glue ↔ `Patch_Topo`); the plan leaves the file format unbound
  pending the professor's answer on whether rules must be consumable by `apply_patch`.
- No UI: replay/undo run from the harness. A checkout-style command is a separate step.
