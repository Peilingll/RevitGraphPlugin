# Direct pipeline round-trip fixed — STEP-line property source

**Date:** 2026-07-18
**Follows:** `2026-07-12_direct-roundtrip-diagnosis.md` (root causes) and
`doc_process/2026-07-12-plan-stepline-entitywalker.md` (the fix plan).

## What was done

The direct pipeline (`Revit → ggifc tree → Cypher → Neo4j`, no temp IFC) now sources
node properties from each entity's lossless STEP (Part 21) line instead of from ggifc's
property getters. The getters collapse the unset/derived/empty-string distinctions that
caused every round-trip failure; `entity.ToString()` preserves them (`$` / `''` / `*` /
`.ENUM.` all faithful).

Three sub-steps, all landed on `feat/dev`:

1. **`Ifc4Schema.GetOrderedAttributes`** (`3272655`) — the JSON schema resource is already
   in EXPRESS declaration order (= STEP parameter order); expose it as an ordered list for
   positional parameter↔attribute mapping, alongside the existing whitelist set.
2. **`StepLineParser`** (`dce2ef9`) — a recursive tokenizer (`$`, `*`, `#ref`, `.ENUM.`,
   `.T./.F.`, int, real, string with `''` escape, list, typed inline), a `TryToPropertyValue`
   that yields the exact ConMan2-stored value (or defers references/typed inlines to the
   reflection layer), and a `PyRepr(double)` that reproduces CPython `repr(float)` so list
   strings match the bridge byte-for-byte. Backslash string escapes fail loud (not in the
   sample model). 28 unit tests, expected values taken from real sample IFC lines + the
   bridge graph ground truth in `data/samples/cypher/01_one_wall_neo4j_query_table_data.json`.
3. **`EntityWalker` rewire** (`4a76be9`) — node properties come from
   `EmitPropertiesFromStepLine` (parse `ToString()`, zip with the ordered schema, keep only
   primitive slots, fail loud on an arg-count/schema mismatch); edges and inline nodes stay
   on the unchanged reflection pass (verified isomorphic to the bridge — 128 relationships).
   The old getter-based property branches (DateTime→epoch, non-finite→"$", enum upper-case,
   list join, fallback ToString stripping) were deleted. 4 end-to-end tests build real ggifc
   entities and walk them, proving the ggifc `ToString()` format assumption without Revit.

## Verification (sample model, plugin-bridge vs plugin-direct, 92 nodes each)

| Check                              | Result                                                                       | Before                                  |
| ---------------------------------- | ---------------------------------------------------------------------------- | --------------------------------------- |
| `compare_neo4j` direct ≡ bridge    | 33 EntityTypes identical, 0 diffs; 128/128 relationships match               | —                                       |
| `graph_2_ifc(plugin-direct)`       | OK, 5785 bytes                                                               | `OverflowError: setArgumentAsInt` crash |
| `ifcopenshell.validate`            | 0 issues                                                                     | (crashed before validate)               |
| Reconstructed IFC direct vs bridge | 84 entities identical after normalizing#ids / GUIDs / OwnerHistory timestamp | —                                       |

The reconstruction crash (the unset `LastModifiedDate` sentinel `-62135596800` overflowing
INT32) and all five other root causes are gone. The direct graph is now byte-identical to
the bridge graph on every non-identity property.

## Key finding

The whole fix rests on ggifc `entity.ToString()` emitting a faithful, complete Part-21 line
whose parameter count matches the EXPRESS-ordered schema attribute list. This was verified
against real ggifc serialization in `EntityWalkerStepLineTests` (no Revit needed). A
secondary insight: STEP reals always carry a decimal point (`0.`), so parsing from the STEP
line inherently fixes diagnosis root cause #3 (integer-vs-float list elements) for free.

## Open items

- Solibri viewer open of the reconstructed `plugin-direct` IFC (the manual check owed since
  Task123) — pending.
- The `dce2ef9` commit subject was truncated (an em-dash in the message); content is correct.
- Only verified on the 92-node sample A/B pair. A new entity type in another model whose
  ggifc `ToString()` arity disagrees with the schema will trip the fail-loud guard — by
  design, so it surfaces the offending type instead of corrupting silently.
