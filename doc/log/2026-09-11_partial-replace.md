# Partial replace — the interface stays, only the pushout moves

**Date:** 2026-09-11
**Plan:** `doc_process/2026-09-11-plan-partial-replace.md` (local).
**Follows:** `2026-09-11_stable-globalids.md` (the precondition: pset / rel nodes keep their
GlobalId across re-conversions, so L and R can be aligned) and
`2026-08-07_rule-persistence-step3.md` (where the "shallow apply, Replace copies both
graphlets" shortcut was chosen).
**Goal:** store and apply a Replace the way Esser 2022 §3.4 describes a rule — L \\ I
deleted, R \\ I inserted, I kept — instead of deleting and rebuilding the whole graphlet.
That is one piece of work with three names in the open-questions list: deep apply for
`Modify`, rules that copy far more than changed, and the half-second invalid intermediate
state a hosted insert's re-sync recorded.

## What shipped

- **`GraphletDiff`** now returns the alignment, not a yes/no: `Match` (L p21 → R p21, the
  interface I), `PushoutL`, `PushoutR`, `Changes` on matched nodes, and a new kind
  `Partial` between `PropertyOnly` and `Structural`. `TryMatch` no longer demands equal
  node counts or full coverage — a list slot present on one side only, or holding a
  different entity type, just leaves its node unmatched (that is the pushout; list
  members pair by position exactly as ConMan2's `create_equivalence_relations_primary`
  does). Only a genuine conflict (one node claimed by two partners), an edge between two
  interface nodes that changed, an inline list that changed shape, or an unnameable
  changed node still falls back to `Structural` — the legacy whole-graphlet replace.
- **`CypherEmitter.ApplyRuleAsync`** diffs a Replace BEFORE touching the graph. Aligned →
  `ApplyAlignedAsync`: resolve portable names for the pushout boundary, delete the L
  pushout, merge the R pushout (edges to interface nodes translated R→L p21, the graph
  has not been renumbered yet), SET the changed values on interface nodes by their L
  p21, then **renumber** the interface to the R p21s, then shared refresh, then persist.
  `NoChange` renumbers and stores nothing. Not aligned → the old path, unchanged.
- **`RuleStore`** stores an aligned rule as: `DELETES` = L pushout copies only,
  `INSERTS` = R pushout copies only, `SETS` = changes, `GLUE` = every edge crossing the
  pushout boundary (interface ends named by path like any context), plus two parallel
  arrays `renumber_from` / `renumber_to` and `aligned: true` on the `:Rule` node.
- **`RuleReplayer`** replays / undoes aligned rules symmetrically (delete pushout by copy
  p21s, merge the other side's copies, glue, changes, renumber forward or reverse) and
  keeps the legacy path for chains recorded before today.
- **Why renumber**: the ggifc mirror holds the R objects with fresh StepIds, and every
  later rule names the kept nodes by those ids (containment refresh edges, a window's
  void rel, context resolution). p21 is a local file number, not an identity (Esser §3.6);
  GlobalId and paths are. NoChange renumbers are not stored: a later undo / replay whose
  `from` id is absent skips that pair, leaving the node at the id the mirror expects —
  structure and values are addressed by path, never by p21.
- **`ResyncHostedInserts`** stays: the mirror still needs the window's opening re-attached
  to the new host object, but in the graph the host node survived, so the re-sync diffs
  to `NoChange` and records nothing.

## Two Neo4j gotchas the real chain found

1. **Index seeks after SET + DELETE in one transaction.** Undoing several steps in one
   transaction — a Modify's reverse renumber (`SET p21_id`) followed by an Insert's undo
   (`DETACH DELETE` by element id, then `MATCH … {p21_id}`) — throws
   `Node with id N has been deleted in this transaction`: Neo4j's transaction-state index
   still returns a node whose indexed property was changed and which was then deleted in
   the same transaction. First mitigation: in every undo the p21-based delete now runs
   before the element-id delete. It resurfaced on a longer path (undo 5→2 of the window
   chain: SET in step k, delete in step k+1, seek in step k+2), so the real fix is
   structural: **`CheckoutAsync`, `ReplayAsync` and `UndoAsync` run one transaction per
   step**, each committing the step with its new `checked_out_seq`. Within one step every
   delete precedes every SET, which keeps the index consistent; and a failure now leaves
   the graph at a real version with the bookmark pointing at it.
2. **Lazy errors.** The driver reports a server-side error at consume time, and the
   replayer never consumed its write results, so every such error surfaced at commit
   with no statement to blame. `RuleReplayer.Run` now consumes each write immediately.

## Verification

- `dotnet test -c Release` with Neo4j up: **105 passed**. New: `PartialReplaceCheckoutTests`
  (pset gains a property → Partial with one inserted copy; loses it → Partial with one
  deleted copy; checkout 1 / 3 / 4 / 2 / head with identical node + edge + value
  signatures; after every aligned apply the kept nodes' p21s equal the mirror's StepIds).
  `GraphletDiffTests` gained the Partial cases; `ConsecutiveModifyCheckoutTests` and
  `PingPongTests` unchanged and green.
- **Revit, same wall / window / move / delete sequence as this morning**, Neo4j cleared,
  new DLL:

  | seq | action | recorded this morning | recorded now |
  |---|---|---|---|
  | 4 | wall gets the hole | Replace, L 24 + R 44 copies | Replace **aligned**: 20 new faces inserted, 12 faces + coord list SET, 24 nodes kept |
  | — | window re-sync after 4 | Replace, 225 + 225 | **not recorded** (NoChange) |
  | 5 | move: wall mesh values | Modify, 25 changes | Modify, 29 changes |
  | 6 | move: the window itself | Replace, 225 + 225 | **Modify, 3 changes** (placement coordinates) |
  | 8 | delete: hole closes | Replace, L 44 + R 24 | Replace aligned: 20 faces deleted, 24 kept |

  Chain: 11 rules / 1677 nodes → **8 rules / 745 nodes**, 450 of which are the window's
  own Insert + Remove copies (208 faces of family geometry — a separate question).
- `checkout.ps1` to every seq 2–8 with `-Ifc`: **0 issues** each, including seq 4 and 5
  — the states that used to lose the void rel's edge to the wall when reached by undo.
  Seq 5 reached by undo (from 8) and by replay (from 1): identical 384-edge sets.
  Round trips 1 / head / 3 / head / 5 / 2 / head; `CHAIN_TOOL=pingpong` 5 rounds, no drift.

## Open

- An edge between two interface nodes that changes (I-internal topology) still falls
  back to the full replace. Not seen on any real chain yet.
- The window's 225-node graphlet is copied whole on Insert and Remove — that is its
  family geometry; the bounding-box question for hosted inserts (Tier 2) is unchanged.
- The stored rule now maps 1:1 onto ConMan2's patch pair (`DELETES`/`INSERTS`/`GLUE` ↔
  `Patch_Topo`, `SETS` ↔ `Patch_Sema`); the export adapter is still pending the
  professor's answer on whether interchange is wanted.
