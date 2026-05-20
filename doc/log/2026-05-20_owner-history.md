# Step 2 — Override the auto-built OwnerHistory chain with Revit values

**Date:** 2026-05-20
**Branch:** `feat/dev`
**Plan:** [`doc_process/2026-05-18-plan-baseline-alignment-five-steps.md`](../../doc_process/2026-05-18-plan-baseline-alignment-five-steps.md) Step 2 (Part B core)
**Commits (oldest first):** `085dcc4` `efebedd` `11627a6` + this log

## Why

After Step 1 the EntityWalker emits only IFC4 schema attributes — but the *values* in those attributes for the OwnerHistory chain still come from ggifc's defaults:

| Entity / attribute | ggifc default | Revit baseline |
|---|---|---|
| `IfcPerson.Identification` | `Environment.UserName` (`"Sandy"`) | `$` |
| `IfcPerson.GivenName` | `null` | `peiling.song` |
| `IfcOrganization.Name` (user org) | `"UNKNOWN"` | `$` |
| `IfcApplication.ApplicationFullName` | `"GeometryGymIFC v0.1.22.0"` | `Autodesk Revit 2025 (ENG)` |
| `IfcApplication.ApplicationIdentifier` | `"GeometryGymIFC v0.1.22.0"` | `Revit` |
| `IfcApplication.Version` | `" v0.1.22.0"` | `2025` |
| `IfcApplication.ApplicationDeveloper.Name` | `"Geometry Gym Pty Ltd"` | `Autodesk Revit 2025 (ENG)` |
| `IfcOwnerHistory.State` | `NOTDEFINED` | `$` |

Eight attributes across four entity types. Step 2 overrides them in place on the auto-created chain rather than rebuilding entities, so we avoid disturbing ggifc's internal references between Project / OwnerHistory / Person / Organization / Application.

## Approach

The Autodesk IFC exporter is open source. Before writing any C# we did a survey
([`doc_process/2026-05-20-revit-ifc-source-survey.md`](../../doc_process/2026-05-20-revit-ifc-source-survey.md)) of `Autodesk/revit-ifc` `master` branch to find the exact source of every value:

- Application full name template — `app.VersionName + GetLanguageExtension(langType)` (Exporter.cs L2880). `VersionName` already prefixes `"Autodesk Revit "`.
- Application identifier — literal `"Revit"` (Exporter.cs L2882).
- Language extension — 16-case `switch` on `LanguageType` (Exporter.cs L2806–2845). Default branch returns empty string.
- Person name — `projectInfo.Author` with fallback to `Application.Username`, parsed via `NamingUtil.ParseName` (Exporter.cs L3175–3191).
- Two distinct `IfcOrganization` instances: a **developer** org attached to the Application (name = ApplicationFullName, everything else null) and a **user** org attached to OwnerHistory.OwningUser (name = `projectInfo.OrganizationName`, often null in an Architectural template).

Survey results pre-empted several wrong assumptions in the original plan — most importantly that the user Organization's name should match the application name, and that English_USA mapped to `"ENG"`. Both were wrong; both are corrected.

## Implementation

| Commit | Sub-step | What it did |
|---|---|---|
| `085dcc4` | 2a | `Ifc/RevitOwnerHistory.cs` with `Source` POCO (testable overload), `Override(IfcProject, Document)` Revit-side adapter, the 16-case `GetLanguageExtension(LanguageType)`, and reflection helpers for ggifc fields that the public setter cannot reach. |
| `efebedd` | 2b | `BoilerplateBuilder.Build` calls `RevitOwnerHistory.Override(project, doc)` right after `new IfcProject(...)`. Replaces the previous five-line `ChangeAction = NOCHANGE` block — that write now lives inside the helper for cohesion. |
| `11627a6` | 2c | `tests/RevitGraphPlugin.Tests/RevitOwnerHistoryTests.cs` (7 facts) and a compile-time `RevitAPI` reference on the test csproj. The test for the null-Organization case failed against the first cut of the helper and prompted the backing-field fallback discussed below. |

### Two overloads, one source of truth

The helper exposes two `Override` entry points:

- `Override(IfcProject project, Document doc)` — used from `BoilerplateBuilder`. Reads `doc.Application.VersionName`, `Username`, etc. and forwards a `Source` record to the second overload.
- `Override(IfcProject project, Source src)` — pure data, callable from unit tests without spinning up Revit.

The pattern keeps Revit API references confined to the Revit-side overload while letting xUnit cover every behavioural decision.

### Hard-coded values carry their provenance

Every literal in the helper has a comment naming the file and line in `Autodesk/revit-ifc` it was copied from. Example for `ApplicationIdentifierToken`:

```csharp
// Source: revit-ifc Exporter.cs L2882 (string productIdentifier = "Revit";).
// Re-verify against a fresh baseline if Revit's IFC exporter is upgraded.
private const string ApplicationIdentifierToken = "Revit";
```

The contract is "observed today against this commit; capture a new baseline if Revit changes behaviour." Upgrades the value from inferred constant to verified observation.

### LanguageType mapping

The 16-case `switch` is verbatim from `Exporter.cs::GetLanguageExtension`. Default returns empty string, matching Autodesk's default branch. Two non-obvious results:

- `English_USA` → `" (ENU)"`, not `" (ENG)"`. The baseline file shows `(ENG)`, which means the captured Revit session was set to `English_GB`.
- `Chinese_Simplified` → `" (CHS)"`, `Chinese_Traditional` → `" (CHT)"`. A naive `substring(0,3).ToUpper()` would collide both at `"CHI"` and break round-tripping under either locale.

The full table is not unit-tested directly because `LanguageType` lives in `RevitAPI.dll`, which is not loaded at test runtime. The audit trail (literal copy + line-numbered comment) covers correctness; end-of-step smoke against different Revit language settings covers behaviour.

### ggifc validator workaround for null Organization fields

The first cut of `SetNullableString` used the public setter for both null and non-null values. The xUnit test for the null user-Organization case immediately failed:

> Expected user org Name to be null or empty after Override with null source, got `'UNKNOWN'`

ggifc's `IfcOrganization.Name` setter rejects null/empty and substitutes `"UNKNOWN"`. To produce STEP `$` (which is what Revit emits and what the ConMan2 baseline expects), the helper bypasses the public setter on null:

```csharp
private static void SetNullableString(object target, string propertyName, string? value)
{
    var type = target.GetType();
    var prop = type.GetProperty(propertyName);

    if (value is not null && prop is not null && prop.CanWrite)
    {
        try { prop.SetValue(target, value); return; }
        catch { /* fall through */ }
    }

    var field = FindBackingField(type, propertyName);
    if (field is null) return;
    try { field.SetValue(target, value); }
    catch { /* give up — ggifc default remains */ }
}
```

Non-null values still go through the validator. Null writes go to the backing field directly. `FindBackingField` walks the inheritance chain looking for `m<PropertyName>`, `_<PropertyName>`, or `<propertyName>` — covering ggifc's conventional naming.

### `IfcOwnerHistory.State` reflection — option A taken

Revit's exporter passes `null` to `CreateOwnerHistory`'s state parameter (Exporter.cs L3244). ggifc exposes `IfcOwnerHistory.State` as a non-nullable `IfcStateEnum`, so the public setter cannot clear it. The helper uses `FindBackingField(typeof(IfcOwnerHistory), "State")` and writes null to that field instead.

Test output:

```
State path: A (reflection cleared)
State property reports: NOTDEFINED
```

The field accepted null (so the backing field is in fact `Nullable<IfcStateEnum>` or similar), but the property getter reports `NOTDEFINED` — likely because the getter returns `default(IfcStateEnum)` when the nullable backing field is unset. **The STEP serialisation behaviour is what actually matters and can only be observed in the Revit smoke** at the end of Step 2: ggifc either writes `$` (acknowledging the null backing field) or writes `NOTDEFINED` (reading via the getter). The log will be updated with the observed result.

## Verification

### Static (xUnit, 25/25 pass)

```
RevitOwnerHistoryTests:
  ✓ Person_GivenName_and_FamilyName_come_from_Author
  ✓ Application_FullName_Identifier_Version_match_Source
  ✓ Developer_Organization_Name_matches_ProductFullName
  ✓ User_Organization_Name_and_Description_use_Source_values
  ✓ User_Organization_is_blank_when_Source_values_are_null
  ✓ OwnerHistory_ChangeAction_is_NOCHANGE
  ✓ OwnerHistory_State_path_is_logged_for_research_log
```

Total suite: 25 passes (18 from Step 1 + 7 from Step 2).

### Not in xUnit, deferred to Revit smoke

- The 16 LanguageType cases (`English_USA → "(ENU)"` etc.). Mapping correctness is covered by audit trail; runtime behaviour needs Revit.
- `IfcOwnerHistory.State` STEP serialisation (`$` vs `NOTDEFINED`).
- `projectInfo.Author` resolution against a real saved Revit document.
- `NamingUtil.ParseName` parity — the helper uses a simplified "whole string into givenName" port; Revit's util splits comma-delimited names. For an empty Architectural template where Author resolves to `Application.Username` (no commas) this is exactly equivalent.

The end-of-Step-2 Revit smoke (run, dump, diff against `data/samples/cypher/00_empty_neo4j_query_table_data.json`) is the closing verification. Update this section with the observed delta.

## Carryover

- **Revit smoke** to verify STEP serialisation of the cleared `State` field and the LanguageType behaviour against a known language setting.
- **`NamingUtil.ParseName` upgrade** if a richer Author value (e.g. `"Smith, John"`) ever appears in practice. Right now the simplified port matches every observed case.
- **Step 2.5** (IfcSIUnit prefix `MILLI`) is next — single-line fix in `BoilerplateBuilder.cs`.
