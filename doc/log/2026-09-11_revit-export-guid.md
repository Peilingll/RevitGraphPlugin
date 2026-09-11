# Product GlobalIds now come from Revit's own export GUID

**Date:** 2026-09-11
**Follows:** `2026-09-11_stable-globalids.md` (every synthetic id is seeded from the
product's GlobalId — so the product's GlobalId had better be right).
**Goal:** the graph's product GlobalIds must equal the ones Revit writes in a native IFC
export, or nothing downstream — ConMan2's GlobalId-seeded diff, the professor's
`run_diff`, a comparison against a native export — can ever match a product.

## The finding

While explaining the id columns, the user put a wall's Revit **IfcGUID** parameter next
to its node in Neo4j:

| | |
|---|---|
| Revit, IFC Parameters → IfcGUID | `3eOty08Wb4yB3H21tjTIK8` |
| graph, `IfcWall.GlobalId` | `3eOty08Wb4yB3H21q7lUtY` |

First 16 characters equal, last 6 not. Decoding both with ggifc's `ParserIfc.DecodeGlobalID`
put the difference in GUID bytes 12–15 only, and the XOR of the two tails was
**`EA C8 C8 EA`** — byte-symmetric. That is the fingerprint of one 32-bit value folded in
twice with opposite byte orders: `FromRevitUniqueId` XORed the UniqueId's element suffix
into the tail least-significant-byte-first, Revit's exporter treats the tail as one
big-endian integer. (A first attempt to reconstruct the episode GUID with ifcopenshell's
`guid.expand` misled for an hour: its hex layout is not `.NET`'s `Guid` byte layout, and
the wall's UniqueId suffix was not its current ElementId either — a pasted element.)

The existing `IfcGuidConverterTests` never pinned a real Revit value; they checked
uniqueness, stability and length, all of which the wrong byte order satisfies.

## What shipped

- **`IfcGuidConverter.ForElement(Element)`** — `ParserIfc.EncodeGuid(ExportUtils.GetExportId(
  element.Document, element.Id))`. The Revit API hands out the exact GUID the exporter
  uses; no re-implementation of the UniqueId arithmetic. All 11 production call sites
  (8 converters, project + storeys in `BoilerplateBuilder`, the host lookup in
  `ElementConverterRegistry`) use it.
- **`FromRevitUniqueId`** kept for code without a Document (tests, tools), byte order
  fixed to big-endian.
- **`ElementConverterRegistry`** logs one line per converted element,
  `guid <id>: uid=<UniqueId> -> <GlobalId>`, so anyone can check the graph against the
  IfcGUID parameter without a query.
- **Tests**: `Element_id_is_folded_into_the_guid_tail_big_endian` (suffix `00000001` moves
  only byte 15, `01000000` only byte 12, bytes 0–11 never) and
  `Matches_the_IfcGUID_revit_shows_for_a_real_element`, pinned on a real pair read off
  Revit and live.log: `37aa155a-26ad-4e1d-9d57-d9ca5d731856-0004cbde` →
  `0tgXLQ9grE7PrNsSfTTzE8`.
- README: the live log lives under Revit's **per-session** temp folder,
  `%LOCALAPPDATA%\Temp\<session GUID>\RevitGraphPlugin\live.log`, not `%TEMP%` as seen
  from a shell.

## Verification

- Neo4j cleared, Revit restarted with the new DLL, Live Sync ON on Project1: live.log
  `guid 314334: uid=…-0004cbde -> 0tgXLQ9grE7PrNsSfTTzE8`, the graph's `IfcWall.GlobalId`
  the same, Revit's IfcGUID parameter the same. The seeded pset / RelDefines ids changed
  with it, as they must (`3jWGAGW$…`, `0W9$xYVl…`).
- `dotnet test -c Release`: 101 passed, 0 failed.

## Consequences

- Every GlobalId in any graph or rule chain recorded before this fix is wrong by the tail
  bytes; such graphs must be wiped and re-synced, their chains are not replayable onto a
  new baseline. Fresh chains are unaffected.
- Alignment work that "matched" a native export before today (06-09 wall converter,
  06-18 floor / column) must have masked or not compared product GlobalIds; the ConMan2
  baseline comparison should be re-run once with GlobalIds unmasked.
