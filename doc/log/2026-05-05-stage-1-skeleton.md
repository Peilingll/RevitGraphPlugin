# Stage 1 — Plugin Skeleton

**Date:** 2026-05-05
**Roadmap reference:** `doc/spec/design.md §4 Stage 1`

## Goal

Bring the Stage 0 skeleton to life: an `IExternalApplication` that owns a Revit-session-scoped Neo4j driver, registers the `DocumentChanged` event, and exposes a manual ribbon button that writes a `(:Project)` node — the smallest end-to-end path from Revit click to graph row.

## What the stage delivers

| File | Role |
| --- | --- |
| `src/RevitGraphPlugin/RevitGraphApp.cs` | `IExternalApplication`. `OnStartup` boots the connector, subscribes a placeholder `DocumentChanged` handler, builds the ribbon. `OnShutdown` unsubscribes and disposes. |
| `src/RevitGraphPlugin/Neo4jConnector.cs` | Wraps a single `IDriver` per Revit process, factory reads credentials from environment, capped 5 s connection timeout, max pool size 20. |
| `src/RevitGraphPlugin/SyncCommand.cs` | `IExternalCommand` triggered by the ribbon button. `MERGE`s a `(:Project)` keyed on `ProjectInformation.UniqueId`. |

## Decisions and rationale

### Singleton `IDriver` is the only correct choice

`related-work.md §3.4` item 3 documents SpaceTracker's per-query driver construction as a memory-leak class bug. The Neo4j .NET driver is explicitly designed to be created once and shared — internal connection pooling depends on it. `RevitGraphApp.Connector` (static, set in `OnStartup`, disposed in `OnShutdown`) gives one driver per Revit session.

### Async is forced; bridge it carefully

Neo4j.Driver 5.x dropped the synchronous API entirely. `IDriver.Session()` no longer compiles; only `AsyncSession()` exists. `IExternalCommand.Execute` is synchronous and runs on Revit's UI thread. The first attempt used `WriteProjectNodeAsync(...).GetAwaiter().GetResult()` directly on the UI thread — when Neo4j was unreachable, the driver's default 30-second connection timeout froze Revit completely, requiring End-task to recover.

The fix has three layers:

1. **Driver-side timeout** (`Neo4jConnector.cs`): `o.WithConnectionTimeout(TimeSpan.FromSeconds(5))` caps how long a single TCP attempt blocks.
2. **Threadpool offload** (`SyncCommand.cs`): `Task.Run(() => WriteProjectNodeAsync(...))` keeps the work off the UI thread.
3. **Wait budget** (`SyncCommand.cs`): `task.Wait(TimeSpan.FromSeconds(10))` returns `Result.Failed` with a `TaskDialog` if the call exceeds the budget, so Revit stays responsive.

For Stage 4 the synchronous bridge will not scale — incremental sync of many ElementIds per `DocumentChanged` cannot block on a serial threadpool wait. That stage will likely need `Application.Idling` or a dedicated worker queue.

### `MERGE` is parameterised; `timestamp` is reserved

```cypher
MERGE (p:Project {RevitProjectId: $id})
SET p.Name = $name,
    p.Number = $number,
    p.timestamp = 0
```

`$id` is `ProjectInformation.UniqueId` (stable across saves) with `doc.PathName` as fallback. Cypher uses parameter binding only — no string concatenation — explicitly avoiding `related-work.md §3.4` item 2 (SpaceTracker's injection class). `timestamp = 0` reserves the field on day one per `design.md §6.3`, so adding temporal versioning later is a value migration rather than a schema migration.

### Environment variables visible to Revit, not just to PowerShell

Process-scope `$env:NEO4J_PASSWORD = "..."` is invisible to Revit launched via Start-menu / desktop shortcut. The plugin reads `NEO4J_PASSWORD` at `OnStartup`, so a User-scope variable is required:

```powershell
[Environment]::SetEnvironmentVariable("NEO4J_PASSWORD", "<value>", "User")
```

Documented in `README.md` so a new contributor can set it once and forget. Fully avoids hard-coded credentials in code (`related-work.md §3.4` item 4).

## Verification — passed

1. Set `NEO4J_PASSWORD` at User scope.
2. Started Neo4j Desktop instance + `neo4j` database.
3. Built `dotnet build -c Debug` (post-build target deployed to `%AppData%\Autodesk\Revit\Addins\2025\`).
4. Launched Revit 2025, opened a blank project.
5. Located ribbon tab `RevitGraphPlugin` → panel `Sync` → button `Sync\ncurrent doc`. Clicked.
6. `TaskDialog` confirmed `Project 'Project Name' synced to Neo4j.`
7. Neo4j Browser query `MATCH (p:Project) RETURN p` returned one node:

| Property | Value |
| --- | --- |
| `Name` | `"Project Name"` (Revit's default for a blank doc) |
| `Number` | `"Project Number"` |
| `RevitProjectId` | `bf0552b6-b7f7-4331-96d5-b19843262895-00015084` |
| `timestamp` | `0` |

Round-trip latency 59 ms (Browser-reported).

## Anti-patterns avoided in Stage 1

Per `related-work.md §3.4`:

| # | Anti-pattern | Avoided by |
| --- | --- | --- |
| 2 | Cypher injection via string concatenation | Parameterised query everywhere in `SyncCommand.cs` |
| 3 | Per-query driver construction | `Neo4jConnector` singleton, lifetime tied to `OnStartup` / `OnShutdown` |
| 4 | Hard-coded credentials | `Environment.GetEnvironmentVariable(...)`, no defaults for password |

Item 6 (stale-edge cleanup gaps) is out of scope until Stage 4. The current `MERGE` is idempotent for property updates but does not detach edges — acceptable because the `(:Project)` node has none yet.

## Open issues parked for later

- The placeholder `DocumentChanged` handler does nothing. Stage 4 partitions the payload.
- The MERGE idempotency was not exercised (would require deliberate re-clicks); first click is the only verified path.
- No connectivity check at `OnStartup` — a misconfigured Neo4j only surfaces when the user clicks the button. Trade-off: probing at startup adds 5 s to Revit launch even when the user never plans to sync.

## Next step

Stage 2 — port the ConMan2 node taxonomy and three-phase write into C# (`IfcGraphMapper`). Composite index `CREATE INDEX ON :GenericNode(p21_id, timestamp)`. First entity exercised end-to-end: `IfcWall`.
