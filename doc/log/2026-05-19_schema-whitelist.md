# Step 1 — IFC4 schema whitelist replaces EntityWalker's blocklist filter

**Date:** 2026-05-19
**Branch:** `feat/dev`
**Plan:** [`doc_process/2026-05-18-plan-baseline-alignment-five-steps.md`](../../doc_process/2026-05-18-plan-baseline-alignment-five-steps.md) Step 1 (Part A)
**Commits (oldest first):** `58c7735` `3d2682b` `21f5982` `c61ff45` `8c5f769` + this log

## Why

Stage 2's `EntityWalker.cs` used three manually-maintained `HashSet<string>` blocklists to filter the reflected C# properties of every IFC entity:

- `GgIfcInternals` (6 entries) — framework members such as `Database`, `StepId`.
- `InverseRelationships` (44 entries) — IFC4 INVERSE attributes that ggifc exposes as forward C# properties.
- `GgIfcConvenienceAccessors` (7 entries) — per-axis scalars on `IfcCartesianPoint` and `IfcDirection`.

The list misses any ggifc convenience getter that did not happen to be in the test sample. The baseline-alignment diff turned up three concrete misses on the empty Revit project alone:

- `IfcPerson.Name` — ggifc composes this from `GivenName` + `FamilyName` + `Identification`.
- `IfcApplication.Name` — derived from `ApplicationFullName`.
- `IfcPersonAndOrganization.Name` — `Person.Name` + `Organization.Name`.

None of those `Name` properties exist in the IFC4 EXPRESS schema. The plugin emitting them broke the "plugin output ≡ ConMan2 import of Revit IFC export" equivalence promise.

Patching the blocklist would solve the three concrete cases but leaves the same trap for every future entity type the plugin walks. Replacing the blocklist with a schema-driven whitelist is a one-time fix.

## Approach

1. Dump the IFC4 forward-attribute table once with `ifcopenshell`.
2. Embed the JSON as a manifest resource in `RevitGraphPlugin.dll`.
3. Replace the two attribute blocklists with a single whitelist check that consults the embedded table at reflection time. `GgIfcInternals` stays because framework members never appear in any IFC schema and the contains-check is a faster short-circuit than the schema lookup.

`ifcopenshell.ifcopenshell_wrapper.schema_by_name("IFC4").declarations()` returns 776 entity declarations for IFC4. `declaration.all_attributes()` returns only forward attributes — INVERSE and DERIVE attributes are excluded by the API. That removes 44 manual entries automatically and stops new ones from accumulating.

## Implementation

Each sub-step landed as its own commit:

| Commit | Sub-step | What it did |
|---|---|---|
| `58c7735` | 1a | `tools/dump_ifc4_schema/dump_ifc4_schema.py` + `data/schema/ifc4_attributes.json` (776 entities). |
| `3d2682b` | 1b | `<EmbeddedResource ... LogicalName="RevitGraphPlugin.Schema.ifc4_attributes.json" />` in csproj. |
| `21f5982` | 1c | `Ifc4Schema.cs` with `Lazy<IReadOnlyDictionary<string, HashSet<string>>>` loader and `IsSchemaAttribute(entityType, attributeName)` predicate. |
| `c61ff45` | 1d | `EntityWalker.cs` drops `InverseRelationships` and `GgIfcConvenienceAccessors` (-52 lines net); reflection loop calls `Ifc4Schema.IsSchemaAttribute` instead. |
| `8c5f769` | 1e | `tests/RevitGraphPlugin.Tests/` xUnit project with 18 tests. |

### Loader choices

- `LazyThreadSafetyMode.ExecutionAndPublication` — the plugin syncs from the Revit UI thread but `CypherEmitter.WriteAsync` walks entities on a background task, so the loader has to be thread-safe.
- `StringComparer.Ordinal` — IFC attribute names are case-sensitive in the EXPRESS schema and ggifc preserves the case in C# class properties.
- Unknown entity types fall back to `true`. ggifc occasionally exposes internal helper classes (e.g. when an `IfcMeasureValue` SELECT is materialised) that have no IFC4 entity declaration. Returning `false` would silently drop their forward attributes. Returning `true` accepts them; if a problem appears later, the loader logs to `Debug.WriteLine`.
- The loader throws on missing or malformed resource — fail-fast at first call beats discovering empty output during a Revit sync.

### xUnit setup

The plan flagged Revit-API isolation as a risk: `RevitGraphPlugin.csproj` references `RevitAPI.dll` via a HintPath on the local Revit install, and a `ProjectReference` from a test project might pull that reference into the test runtime. In practice the test process never loads `RevitAPI.dll` because neither `Ifc4Schema` nor `EntityWalker` touches a Revit type. Compilation needs the HintPath; the test runner does not. The csproj has `<NoWarn>$(NoWarn);MSB3277</NoWarn>` to silence the unrelated reference-resolution warning.

Recommended fallbacks (sub-project split, console smoke) were not needed.

## Verification

### Static (xUnit, 18/18 pass)

- 9 known-good IFC4 attribute lookups (`IfcPerson.GivenName`, `IfcWall.Description`, `IfcSite.RefLatitude`, ...).
- 4 known-bad attribute lookups (the three convenience `Name` getters plus a synthetic fake).
- 1 unknown-entity fallback case.
- 4 `EntityWalker.Walk` end-to-end cases that build entities via ggifc and assert against the `Properties` dictionary.

### Functional smoke (PowerShell reflection)

Run against the built DLL during 1c development to confirm the resource path, deserialiser, and fallback logic all line up. 7/7 cases passed.

### Plugin output diff (pending)

The end-to-end Revit smoke described in the plan still has to run: sync an empty Architectural project against an empty Neo4j database, dump the table, and diff against `data/samples/cypher/00_empty_neo4j_query_table_data.json`. Predictions from the static evidence:

- Node count unchanged (38) — Step 1 only adjusts which properties land on each node, not which nodes exist.
- Edge count unchanged (54).
- `IfcPerson`, `IfcApplication`, `IfcPersonAndOrganization` lose their spurious `Name` property.
- Any other ggifc convenience getter that the old blocklist missed will also be filtered automatically.

If the Revit dump reveals further removed properties beyond the three known cases, update this log with the entity type and attribute name; that becomes the cleanest record of which previously-unblocked ggifc convenience getters were silently affecting Stage 2 output.

## Carryover

- **End-to-end Revit smoke** before declaring Step 1 closed.
- **`IfcOwnerHistory.State` mismatch** (`$` in baseline, `NOTDEFINED` in plugin) is unrelated to Step 1 — `State` is in the IFC4 schema, so the whitelist accepts it and `EntityWalker` emits whatever ggifc has set. Resolution belongs to Step 2 Part B (OwnerHistory override).
- **Schema dump version pinning** — `data/schema/ifc4_attributes.json` is committed; regenerate only on an IFC schema bump. The script README records that.
