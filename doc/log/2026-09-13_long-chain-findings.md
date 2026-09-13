# A 139-rule chain: two silent gaps in the rule chain, closed

**Date:** 2026-09-13 (found late 2026-09-11 on the three-storey model built live)
**Follows:** `2026-09-11_levels-in-live-sync.md`.
**Goal:** the first long live session (139 rules, 5644 nodes, walls / doors / windows /
columns / beams / roofs across three levels) was replayed with `checkout.ps1`. It broke
at seq 119 and it held an element Revit no longer had. Both turned out to be old gaps
that only a long chain exposes.

## Gap 1 — a shared rel revived by refresh is never recorded

A storey's containment rel is dropped when its last element leaves (`SharedDelete`,
memberless rels are invalid IFC). The ggifc object survives with zero members, and the
next element placed on that storey re-joins the SAME object — outside the watermark, so it
reaches the apply as a shared REFRESH. Refresh MERGEs the node back into the live graph
silently; nothing records that. Replay rebuilds the graph from copies, the node never
returns, and the first glue naming it (`[connection_node=…]|RelatingStructure|
ObjectPlacement`, a roof's placement relative to the storey) throws `context not found`.

**Fix** (`CypherEmitter.ApplyRuleAsync`): before anything else, every `SharedRefresh`
entity that is in neither the graph nor the graphlet is moved into the graphlet — it is
inserted, copied into the rule, glued like any rider.

## Gap 1b — the lingering memberless rel disqualified every later Replace

The mirror image: after the drop, every later rule keeps listing the memberless rel for
deletion (`StoreyContainmentChanges` reports it while the ggifc object has no members).
Deleting nothing is harmless, but the aligned (partial-replace) path requires an empty
`SharedDelete`, so every later property change on ANY element was stored as a full
Replace. This is why the 139-rule chain showed so many `aligned=false` rules, and why a
reconcile after a roof rollback stored three whole-graphlet Replaces instead of nothing.

**Fix**: `SharedDelete` entries whose graph node is already gone are filtered out before
the alignment decision.

## Gap 2 — Revit tells us nothing about a rollback

A cancelled roof sketch arrives as `DocumentChanged` with `Operation =
TransactionGroupRolledBack` and **empty** added / deleted / modified sets. The roof
element from the earlier committed sub-transaction is gone from the document, but the
session had no id to act on: mirror and graph kept it (seq 118 of the long chain, absent
from the native export). Undo of a *move* does carry ids (`TransactionUndone`,
`modified=1`), so only the id-less cases needed handling.

**Fix** (`LiveSyncSession.ReconcileVanished` / `ReconcileAll`, called from
`LiveSyncManager` after every event): a roll call of every tracked element id against the
document — vanished ones get a `Remove` — on every event; and when `Operation` is not a
plain commit, a re-conversion of every tracked element (the diff reports `NoChange` for
the untouched ones, stores a `Modify` for whatever the undo changed) plus an `Insert` for
any supported element the document holds but the session does not. The log now prints
`op=…` on every event and a `reconcile:` line when it acted.

## Verification

- Tests: `SharedRevivalTests` — (1) wall in → wall out → wall in → checkout 1 / 4 / 3 /
  2 / 4 with the rel present exactly when it should be; (2) storey 1 emptied, a rename on
  storey 2 still stores an aligned `Modify`. Suite: 109 passed, 1 skipped.
- Revit: wall → delete → wall → `checkout 1` / `head` / `3` / `head` all succeed (this
  path used to throw). Roof by footprint → cancel: `TransactionGroupRolledBack` →
  `reconcile: 1 tracked element(s) no longer in the document, removed: 314518`, a `Remove`
  stored, graph back to baseline. Two walls, delete one, flip the other's IsExternal →
  `Modify` with one `:Change`, `shared_delete` empty.

## Cost and open

- `ReconcileAll` is O(model) per undo / rollback (each tracked element re-converted, one
  transaction each). Fine at demo scale; a large model will feel every Esc. The cheap
  roll call alone covers the observed case; the full pass could be limited to elements
  touched by the last few rules.
- The old 139-rule chain cannot replay past 118 (its seq 119 copy lacks the rel); it was
  recorded before the fix and is not repairable. Fresh chains are.
