# Repeated reversibility and identity evidence — pingpong tests

**Date:** 2026-09-11
**Commits:** `c13794b` (DPO test data, 2026-08-14), `f03b0eb` (tests, 2026-09-11 — committed
with the one-word message "test"; this log is its description).
**Follows:** `2026-08-10_rule-replay-and-undo.md` (replay + undo closed loops) and
`2026-08-14_portable-build-and-packaging.md` (`checkout.ps1` shipped).
**Goal:** the closed loops in `RuleReplayTests` prove that replay ∘ undo returns the initial
graph **once**. Esser 2022 §3.6 asks for more: reverse application must return the initial
graph, and `checkout.ps1` is used by bouncing the graph between versions repeatedly. A rule
whose undo ∘ replay is not *exactly* the identity leaves a little drift each round that a
single loop cannot see. Turn "checkout sometimes fails" into "round K, seq S, direction D".

## What shipped

- **`PingPongTests`** — the mixed chain of `RuleReplayTests` (baseline → insert w1 →
  insert w2 → rename w2, stored as `Modify` → remove w2; seq 1–5, so every stored op shape
  is on it), bounced **10 rounds** head ↔ baseline through `RuleReplayer.CheckoutAsync` —
  the same entry point `checkout.ps1` drives, so the `checked_out_seq` bookkeeping is
  exercised too. After every hop the graph's signature must equal the signature taken on the
  first visit of that end.
- **The signature** is one multiset over structure *and* values: node `EntityType` counts,
  edge triples (`type|rel_type|list_index|type`), and every non-identity
  `EntityType|key=value` row. The value half is what catches a `Name` or `IsExternal`
  bounced to the wrong side, which the structural half cannot see. Masked keys:
  `p21_id`, `timestamp`, `revit_element_id`, `GlobalId` — the same set `GraphletDiff`
  masks, for the reason proven in `GgifcIdentityTests` below. A failing round prints the
  drifted rows with round and direction.
- **`ManualChainTools` `CHAIN_TOOL=pingpong`** — the same probe against a **real** chain
  (`CHAIN_TARGET`, default `plugin-live`; `CHAIN_ONTO`, default = target; `CHAIN_ROUNDS`,
  default 5). It finds the newest `:Baseline` anchor and HEAD, starts from wherever the
  graph currently stands (`CurrentSeqAsync`), warms up to head, bounces, and restores the
  starting seq at the end. The unit chain cannot produce the mixed `SharedDelete` path
  (a containment rel that empties on remove); only a real chain covers it.
- **`GgifcIdentityTests`** — two pure-ggifc facts, no Neo4j, no Revit:
  1. every entity not handed a `GlobalId` explicitly gets a **random** one per conversion
     (the wall's plugin-assigned GlobalId is stable across two identical builds; the
     containment rel's and the storey's differ);
  2. re-converting the same element (detach + rebuild, the `ApplyModified` sequence)
     shares **no StepId** with the original — ggifc allocates p21 monotonically and never
     reuses.
  These two facts are the whole reason for the four-name fallback in `PropertyChange`
  (commits `1c4af14`, `23ba585`) and the dual deletion in undo-Insert. Esser §5.2 names
  unstable identifiers as the method's central domain limitation; ggifc exhibits both
  flavors. If either test ever fails, that diagnosis and the design built on it must be
  revisited.
- **DPO test data** (`c13794b`) — `data/samples/ifc/DPO_test/basic/seq1–4.ifc` and
  `test_1/v1–v8.ifc` + `rv2.ifc`: `graph2ifc.py` exports (IfcOpenShell 0.8.4, 2026-08-13)
  of the checked-out graph at successive chain positions; `rv2` is v2 reached by replay
  rather than by undo. Kept as fixtures for `compare_ifc.py` so a future change to
  replay/undo can be diffed against a known-good export.

## Verification

- `dotnet test` on 2026-09-11: **94 passed, 0 failed, 0 skipped**. `PingPongTests`
  completed 10 rounds with no drift in structure or values.
- The real-chain `pingpong` tool has **no recorded run** against `plugin-live` in this log;
  run it (see below) before relying on it for a demo.
- Caveat for whoever reads "94 passed": the Neo4j-backed tests (`PingPongTests`,
  `RuleReplayTests`, `ApplyRuleIntegrationTests`, `RuleStoreIntegrationTests`) do not
  xunit-`Skip` when Neo4j is unreachable — they write one line to test output and pass
  vacuously. A green run on a machine without Neo4j proves only the pure tests.

## Where this leaves DPO granularity

The question "if only `IsExternal` changes, does the rule touch one node?" has two answers:

| layer | granularity today |
|---|---|
| stored rule | **one node**: a `Modify` rule with a single `:Change` row (`NominalValue: True→False`, path anchored on the pset GlobalId) — ConMan2's semantic-patch shape (step 3 log) |
| replay / undo | **one node**: the `:Change` rows are applied in place, forward or reversed |
| live apply from Revit | **whole graphlet**: still the shallow option (a) — delete + rebuild (`GraphletDiff.cs` header, `RuleReplayer.cs` §195), p21s renumber, ggifc-generated GlobalIds churn |

The identity mask in this log's tests exists because of that asymmetry: replay does not
renumber, live apply does, so the two ends of a bounce agree only modulo identity columns.
Deep apply for `Modify` (`SET` in place) is the first item still open in the 08-10 log and
nothing after 08-10 touched it; landing it would let the mask narrow to `timestamp` only.

## How to run

```powershell
$env:NEO4J_LOCAL_PASSWORD = '<password>'
dotnet test tests\RevitGraphPlugin.Tests --filter FullyQualifiedName~PingPongTests

# against a real chain (graph is restored to its starting seq afterwards)
$env:CHAIN_TOOL = 'pingpong'; $env:CHAIN_TARGET = 'plugin-live'; $env:CHAIN_ROUNDS = '5'
dotnet test tests\RevitGraphPlugin.Tests --filter FullyQualifiedName~ManualChainTools
```

## Open

- Deep apply for `Modify` (above).
- Make the Neo4j-backed tests report `Skipped` instead of a vacuous pass.
- Tests use `DatabaseIfc(bool, ReleaseVersion)` and `ReleaseVersion.IFC4`, both marked
  obsolete by ggifc — warnings only, but a ggifc upgrade will break them.
