# Stage 0 — Environment Bootstrap

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 0`

## Goal

Stand up a buildable Revit 2025 add-in skeleton (`net8.0-windows`, x64) that loads cleanly into Revit, plus a Neo4j connectivity probe — without writing any business logic. All four roadmap items are addressed: project file, NuGet references (`Neo4j.Driver`, `GeometryGymIFC`), `.addin` manifest, and a hello-world Cypher round-trip path.

## Environment

| Component      | Value                                                                                  |
| -------------- | -------------------------------------------------------------------------------------- |
| Revit 2025     | `D:\Autodesk\Revit 2025\` (non-standard install path; all `RevitAPI*.dll` present) |
| .NET SDK       | `8.0.403` (pinned via `global.json`, `rollForward: latestPatch`)                 |
| Add-ins folder | `%AppData%\Autodesk\Revit\Addins\2025\`                                              |
| Neo4j Desktop  | Downloaded by user; database creation / start pending                                  |

## Decisions and rationale

### 1. `Private=false` on `RevitAPI` / `RevitAPIUI` references

Revit ships its own copies of `RevitAPI.dll` and `RevitAPIUI.dll` and loads them into the Revit process before any add-in DLL. If the build output were to copy them into the Addins folder, the resulting load-context conflict produces silent type-identity mismatches (e.g. `IExternalApplication` from the add-in folder is not `==` to `IExternalApplication` from Revit's own folder). Setting `<Private>false</Private>` plus `<ExcludeAssets>runtime</ExcludeAssets>` keeps the references compile-time-only.

Verification: after `dotnet build -c Debug`, the output directory contains `RevitGraphPlugin.dll`, `Neo4j.Driver.dll`, `GeometryGymIFC.dll`, `Newtonsoft.Json.dll`, `Microsoft.Bcl.AsyncInterfaces.dll`, `System.IO.Pipelines.dll`, and `RevitGraphPlugin.addin` — but **no** `RevitAPI*.dll`.

### 2. Post-build deploy target instead of an external script

`RevitGraphPlugin.csproj` carries a `DeployToRevitAddins` target (`AfterTargets="Build"`, `Condition="'$(Configuration)' == 'Debug'"`) that copies `*.dll` / `*.pdb` / `*.addin` from `$(TargetDir)` into `$(APPDATA)\Autodesk\Revit\Addins\2025\`. This eliminates a separate install step from the dev loop. Release builds skip the copy — packaging concerns are deferred until the add-in has a stable release shape.

### 3. `.addin` uses a relative `<Assembly>` path

`<Assembly>RevitGraphPlugin.dll</Assembly>` co-locates the manifest with the DLL inside the Addins folder. Absolute paths in the manifest (a common dev shortcut) leak the developer's filesystem layout into a versioned artefact and break for any other contributor. Relative path is the production-shaped choice.

### 4. Neo4j credentials via environment variables

The smoke test (`tools/Neo4jSmokeTest`) reads `NEO4J_URI` / `NEO4J_USER` / `NEO4J_PASSWORD` from the environment. `NEO4J_PASSWORD` has no default — absent it, the program exits with code 2 and a diagnostic. This deliberately avoids the SpaceTracker anti-pattern documented in `related-work.md §3.4` (hard-coded `neo4j/password`).

### 5. SDK pinned at 8.0.403

A future `dotnet 9.x` install on the developer machine would silently roll the build to `net9.0` defaults. `global.json` pins `8.0.403` with `rollForward: latestPatch`, so SDK upgrades stay within the 8.0.x patch line until an explicit migration.

## Verification results

| Step                                                                  | Result                                                                                                                                                                                                  |
| --------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.`dotnet --version`                                                | `8.0.403`                                                                                                                                                                                             |
| 2.`dotnet build RevitGraphPlugin.sln -c Debug`                      | 0 errors, 4 warnings (all `NU1701`, see footnote)                                                                                                                                                     |
| 3.`RevitAPI.dll` not copied to bin                                  | confirmed                                                                                                                                                                                               |
| 4. Addins folder populated                                            | `RevitGraphPlugin.dll`, `RevitGraphPlugin.addin`, `Neo4j.Driver.dll`, `GeometryGymIFC.dll`, `Newtonsoft.Json.dll`, `Microsoft.Bcl.AsyncInterfaces.dll`, `System.IO.Pipelines.dll` present |
| 5. Revit launch test                                                  | pending (Revit launch not part of CI; manual verification deferred to next session)                                                                                                                     |
| 6. Neo4j Desktop database created and running                         | passed (after troubleshooting, see below)                                                                                                                                                               |
| 7.`dotnet run --project tools/Neo4jSmokeTest` returns `hello = 1` | passed                                                                                                                                                                                                  |

### Footnote on the 4 warnings

All four warnings are `NU1701`: `GeometryGymIFC 0.1.22` and `Newtonsoft.Json 6.0.3` declare `.NETFramework,v4.x` targets only and are restored under net8.0 via assembly compatibility. The IfcInfraToolKit reference in `related-work.md §4` confirms `GeometryGym.Ifc v0.1.22` as the working version, so no upgrade is available. The package is binary-compatible with net8.0 in practice for the API surface we will use; this will be revisited in Stage 3 when the converters actually invoke the IFC entity constructors.

The `MSB3277` cascade (~30 entries) about Revit's transitive references (`CefSharp.*`, `Autodesk.UI.*`, etc.) is suppressed via `<NoWarn>$(NoWarn);MSB3277</NoWarn>` — those DLLs are supplied by Revit at runtime, never resolved by us.

## Anti-patterns deliberately avoided

Per `related-work.md §3.4`:

1. No hard-coded Neo4j credentials.
2. No singleton `IDriver` constructed per query — even the smoke test uses a single `using` block.
3. No string-concatenated Cypher (the smoke test uses `RETURN 1 AS hello` with no parameters; once parameters are needed, parameterised Cypher is mandatory from Stage 2 onward).
4. No .NET Framework 4.8.

## Critical files

- `global.json` — SDK pin
- `RevitGraphPlugin.sln` — solution
- `src/RevitGraphPlugin/RevitGraphPlugin.csproj` — main class library, post-build deploy target
- `src/RevitGraphPlugin/RevitGraphPlugin.addin` — manifest with relative `<Assembly>` and stable `<FullClassName>RevitGraphPlugin.RevitGraphApp</FullClassName>`
- `src/RevitGraphPlugin/RevitGraphApp.cs` — minimal `IExternalApplication` implementation, returns `Result.Succeeded` from both lifecycle hooks
- `tools/Neo4jSmokeTest/{Neo4jSmokeTest.csproj, Program.cs}` — Neo4j connectivity probe

## Smoke-test connectivity troubleshooting

Three failure modes were hit before `hello = 1` printed. Documented here because they are not obvious from the design spec and will recur on any new dev machine.

### 6.1 URI scheme mismatch

The first attempt failed with `IOException: Failed to connect to server 'bolt://localhost:7687/' ... Connection with the server breaks`. Neo4j Desktop 2026.04 advertises the connection URI as `neo4j://127.0.0.1:7687` (routing-aware scheme), not `bolt://...`. Driver behaviour differs: under `neo4j://` the driver attempts routing discovery and returns a misleading IOException when the topology reply does not match expectations.

**Fix:** changed the smoke test default from `bolt://localhost:7687` to `neo4j://127.0.0.1:7687`. `bolt://` is a Neo4j 4.x convention; on 5.x and Desktop 2026 the canonical form is `neo4j://`. The Stage 1 `Neo4jConnector` should adopt the same default.

### 6.2 Instance running but no database

After the URI fix, the driver returned `Unable to connect to database, ensure the database is running`. The Desktop UI showed the instance as **RUNNING**, but `Databases (0)` — Desktop 2026 separates **Instance** (the DBMS process) from **Database** (a logical DB inside it), and creating an instance does **not** auto-create a `neo4j` database. This is a behavioural change from Neo4j Desktop 1.x, which provisioned a default DB on instance creation.

**Fix:** clicked **Create database** on the instance card, named it `neo4j` (the driver's default target).

### 6.3 Race between instance state and database creation

The first Create-database attempt failed with `500 Internal Server Error: Cannot connect to stopped DBMS` even though the UI displayed RUNNING. Desktop's status display lags behind backend reality during start-up — the JVM accepts management API calls only several seconds after the green RUNNING dot appears.

**Fix:** stop → start → wait ~30 seconds → create database.

After all three resolutions, the smoke test prints `Connecting to neo4j://127.0.0.1:7687 as neo4j ...` followed by `hello = 1`.

## Next step

Stage 1 — replace the empty `OnStartup` / `OnShutdown` bodies with `DocumentChanged` registration and a singleton `Neo4jConnector` (per `related-work.md §3.4`'s prohibition on per-query driver construction), and add a single ribbon button that triggers a manual sync. Verification: a `Project` node appears in Neo4j after clicking the button.mi
