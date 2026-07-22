# Live Sync Architecture

Live incremental sync mirrors one Revit document into Neo4j in real time. It is
**pure C#** — it does **not** go through Python / ConMan2 / ifcopenshell. (Python is
only used by the separate "Sync (bridge)" button.)

Live sync reuses the whole **direct-write** pipeline (`Revit → ggifc tree → Cypher →
Neo4j`, no temp `.ifc`). It adds three things on top of it: an in-memory ggifc mirror
kept alive after the baseline, a `DocumentChanged` event subscription, and an
incremental rule engine (`GraphRule` + `CypherEmitter.ApplyRuleAsync`).

---

## Direct vs. Live Sync

The baseline is almost identical to a one-shot direct sync; everything different is
what happens *after* the baseline.

| | Direct (`SyncDirectCommand`) | Live Sync |
|---|---|---|
| Trigger | one button click = one full write | ON writes a baseline, then follows every change automatically |
| Phase A (build tree) | `ModelAssembler.Build` | `ModelAssembler.Build` (same) |
| Phase B (write) | `CypherEmitter.WriteAsync` (full) | baseline also uses `WriteAsync` (same) |
| After building | `IfcModelContext` is discarded | `IfcModelContext` **kept** as in-memory mirror + subscribes `DocumentChanged` |
| On each later change | none (must re-click for a full rewrite) | `CypherEmitter.ApplyRuleAsync` (only the changed graphlet) |
| Timestamp | `plugin-direct` | `plugin-live` |

**Live Sync = Direct's full write as a baseline + keep `IfcModelContext` as a mirror +
event-driven incremental updates.** The incremental machinery (`LiveRuleBuilder`,
`GraphRule`, the StepId watermark, `ApplyRuleAsync`) is the part Direct does not have.

- **Converters are shared.** Same `IElementConverter` set. Baseline runs
  `ElementConverterRegistry.ConvertAll` (all elements); the increment runs
  `TryConvertOne` (only the changed element). Same `ConvertOne` internals, incl.
  ownership tagging.
- **Cypher is shared.** Same `CypherEmitter` / `EntityWalker` / `NodeClassifier`.
  Baseline calls `WriteAsync`; the increment calls `ApplyRuleAsync` — both go to Neo4j
  over `Neo4j.Driver` (bolt) with the same node/edge shapes.

---

## Data flow (pure C#, no Python)

### Startup — build the baseline

```
User clicks the ribbon "Live Sync" button
│
├─ RevitGraphApp.cs
│     On app startup: builds the buttons and subscribes DocumentChanged / DocumentClosing
│
└─ LiveSyncToggleCommand.cs
      Grabs ActiveUIDocument.Document, calls Toggle, shows ON/OFF via TaskDialog
      │
      └─ LiveSyncManager.Toggle(doc)
            Static coordinator; no session → start one (baseline); else dispose. Updates button text
            │
            └─ LiveSyncSession.Start(doc)
                  │
                  ├─ ModelAssembler.Build(doc)          build the whole ggifc tree
                  │     ├─ BoilerplateBuilder            skeleton (Project/Site/Building/Storey/units/contexts)
                  │     └─ ElementConverterRegistry.ConvertAll
                  │           convert every Revit element to an IFC product, filling
                  │           OwnerByStepId (StepId→Revit id) and ConvertedElements (Revit id→IfcElement)
                  │
                  ├─ Neo4jConfig.Resolve()              bolt URI + credentials from NEO4J_LOCAL_*
                  │
                  └─ CypherEmitter.WriteAsync(...)      full wipe + rewrite into Neo4j (timestamp = plugin-live)
                        (session keeps IfcModelContext alive as the in-memory mirror)
```

### Increment — every committed Revit transaction

```
Revit transaction commits
│
└─ ControlledApplication.DocumentChanged
   │
   └─ LiveSyncManager.OnDocumentChanged(e)
         Core router; filters by doc.Equals(session.Document) (NOT reference equality),
         then dispatches in order: deletes → adds → modifies (hosts before hosted).
         Any exception → dispose session, turn button off (fail loud, never desync silently).
         Every decision is written to LiveSyncLog (TaskDialog is forbidden inside the event).
         │
         └─ LiveSyncSession.ApplyAdded / ApplyModified / ApplyRemoved
               │
               ├─ StepIdWatermark.Current(db)          record the highest StepId before converting
               │
               ├─ ElementConverterRegistry.TryConvertOne
               │     convert only this element; the new entities land exactly in (before, after]
               │
               ├─ LiveRuleBuilder.BuildUpsert / BuildRemove
               │     turn "one element change" into one GraphRule
               │     ├─ GraphletExtractor.WalkNew       walk only the watermark range (O(graphlet), not whole db)
               │     ├─ DetachFromContainment           on modify/remove, detach old element from storey containment first
               │     └─ ForgetOwnership                 drop superseded entities' ownership so they are not re-walked
               │
               └─ CypherEmitter.ApplyRuleAsync(rule)   apply the rule in ONE transaction (all-or-nothing)
                     delete old nodes by {timestamp, revit_element_id} → MERGE graphlet → refresh shared containment
```

### Supporting files used along the way

```
EntityWalker.cs      walk one ggifc entity into EntityData { properties, edges, inlines }
NodeClassifier.cs    node kind (Primary / Connection / Secondary / Inline) + Neo4j label
IfcModelContext.cs   the live in-memory mirror: Db, StoreyByLevel, ConvertedElements, OwnerByStepId
```

---

## Per-file reference (what each `.cs` does + key methods)

Ordered along the flow `rvt → DocumentChanged → converter → cypher → neo4j`.

### Startup & event wiring

**`RevitGraphApp.cs`** — `IExternalApplication` entry point.
- `OnStartup` — builds the 3 ribbon buttons; subscribes
  `ControlledApplication.DocumentChanged += LiveSyncManager.OnDocumentChanged` and
  `DocumentClosing`. This wires live sync into Revit's global event stream.
- `OnShutdown` — unsubscribes + `LiveSyncManager.Shutdown()`.

**`LiveSyncToggleCommand.cs`** — the toggle button (`IExternalCommand`,
`[Transaction(ReadOnly)]`).
- `Execute` — reads `ActiveUIDocument.Document`, calls `LiveSyncManager.Toggle(doc)`,
  shows status via `TaskDialog`. The only place UI is shown (a click is not inside a
  `DocumentChanged` context).

### Coordination & session

**`LiveSyncManager.cs`** — static coordinator + event router; holds the single
`LiveSyncSession? _session`.
- `Toggle(doc)` — no session → `LiveSyncSession.Start` (baseline); else `Dispose`.
  Updates button text.
- `OnDocumentChanged(e)` — the core router, fires once per committed transaction:
  filters with `doc.Equals(_session.Document)` (deliberately **not** `ReferenceEquals`;
  Revit does not guarantee the same managed reference); dispatches **deletes →
  adds → modifies**, adds/mods ordered by `SupportedByPriority` (hosts before hosted).
  Any exception disposes the session and flips the button off (fail loud; never show UI
  from the event).
- `SupportedByPriority(doc, ids)` — filters supported elements, orders by `Priority`.
- `OnDocumentClosing` — closing the mirrored document ends the session.

**`LiveSyncSession.cs`** — baseline + increment core for one document.
`const Timestamp = "plugin-live"`; holds `_driver`, `_ctx` (the mirror), `_registry`.
- `Start(doc)` (static) — **baseline**: `ModelAssembler.Build` → `Neo4jConfig.Resolve`
  → open driver → `CypherEmitter.WriteAsync`. Returns a live session keeping `_ctx`.
- `ApplyAdded(el)` → `Upsert(el, Insert)`.
- `ApplyModified(el)` — if previously converted (`ConvertedElements` has it):
  `DetachFromContainment` + `ForgetOwnership`, then `Upsert(Replace)`; otherwise
  falls back to `Insert`. Then `ResyncHostedInserts` re-attaches windows/doors to the
  rebuilt host wall.
- `ApplyRemoved(id)` — `DetachFromContainment` + `ForgetOwnership` + remove from
  `ConvertedElements` → `LiveRuleBuilder.BuildRemove` → `Apply`.
- `Upsert(el, op)` (private, **increment heart**) — read
  `StepIdWatermark.Current` (before) → `_registry.TryConvertOne` → read (after) →
  `LiveRuleBuilder.BuildUpsert` → `Apply`.
- `Apply(rule)` — `Task.Run(() => CypherEmitter.ApplyRuleAsync(...)).GetAwaiter().GetResult()`
  (blocking call to avoid the Revit UI-thread `SynchronizationContext` deadlock).
- `Supports` / `Priority` — proxy the registry for the manager's filtering/ordering.

**`LiveSyncLog.cs`** — append-only diagnostics.
- `Write(msg)` — writes to `%TEMP%\RevitGraphPlugin\live.log`. Exists because
  `TaskDialog` is forbidden inside `DocumentChanged`; without a file, a swallowed
  exception is invisible. Swallows its own errors (diagnostics must never break sync).

**`Neo4jConfig.cs`** — connection settings.
- `Resolve()` — builds `(bolt://host:port, user, password)` from `NEO4J_LOCAL_*` env
  vars; forces `localhost → 127.0.0.1` (matches ConMan2). Shared by Direct and Live.

### Phase A — build tree / converters

**`ModelAssembler.cs`** — Phase A assembly.
- `Build(doc)` — `BoilerplateBuilder.Build` (skeleton) →
  `ElementConverterRegistry().ConvertAll`. Shared by Direct and Live baseline.

**`IfcModelContext.cs`** — the live in-memory mirror (the key Live state; kept alive
after baseline so the model is remembered).
- `Db` — the ggifc `DatabaseIfc` (the IFC tree itself).
- `StoreyByLevel` — Revit Level → IfcBuildingStorey.
- `ConvertedElements` (Revit ElementId → IfcElement) — lets modify/remove find the IFC
  product, and lets hosted elements wire back to their host wall.
- `OwnerByStepId` (ggifc StepId → Revit id) — later written as the Neo4j
  `revit_element_id` property; the increment locates a graphlet by it.

**`Ifc/Converters/ElementConverterRegistry.cs`** — element→converter dispatch +
ownership tagging.
- `ConvertAll(doc, ctx)` — baseline; runs every converter over all elements.
- `TryConvertOne(el, ctx)` — the **increment entry point**; converts one element.
- `ConvertOne` (private, shared) — reads a StepId watermark around the converter call,
  tags every new StepId into `OwnerByStepId` (skipping `SharedResourceTypes`, e.g. a
  shared containment rel), then registers the principal product into `ConvertedElements`
  by GlobalId (without which a non-wall modify would duplicate instead of replace).
- `ConversionPriority` / `Supports` — back the ordering/filtering.
- `FindProduct` — the IfcElement in the watermark range whose GlobalId matches.

**`Ifc/StepIdWatermark.cs`** — isolates one element's graphlet.
- `Current(db)` — highest allocated StepId. ggifc allocates monotonically, so entities
  created by one converter call are exactly those in `(before, after]`. This is the
  mechanism that lets the increment touch only the changed graphlet.

### Build the GraphRule (increment-only)

**`Cypher/Direct/GraphRule.cs`** — rule types + extraction.
- `enum RuleOp { Insert, Remove, Replace }`.
- `record GraphRule(Op, RevitElementId, Timestamp, Graphlet, SharedRefresh, SharedDelete)`
  — one Revit change = one graph transformation rule.
- `SharedResourceTypes` — types that are shared context, never owned by one element
  (`IfcRelContainedInSpatialStructure`); ownership tagging must skip them.
- `GraphletExtractor.WalkNew(db, owner, before, after, ts)` — walks **only** the
  watermark range (O(graphlet), not the whole db), via `CypherEmitter.WalkOwned`.
- `GraphletExtractor.StoreyContainmentChanges(storeys, ts)` — produces the storey
  containment (Refresh, Delete): rels with members are re-walked (list_index renumbered);
  memberless rels go to Delete (ggifc refuses to serialize a memberless rel).

**`Cypher/Direct/LiveRuleBuilder.cs`** — turns one change into a `GraphRule`
(Revit-API-free, headless-testable).
- `BuildUpsert(op, db, owner, storeys, elementId, before, after, ts)` — Insert/Replace:
  `WalkNew` graphlet + `StoreyContainmentChanges` refresh/delete.
- `BuildRemove(storeys, elementId, ts)` — Remove: empty graphlet, containment repair only.
- `DetachFromContainment(element)` — in the ggifc mirror, remove the element from its
  storey's shared containment rel (required before walking containment for Remove/Replace).
- `ForgetOwnership(owner, elementId)` — drop the dead entities' ownership entries so
  superseded entities are never re-walked.

### Phase B — write to Neo4j

**`Cypher/Direct/NodeClassifier.cs`** — ConMan2 node taxonomy (shared).
- `Classify(entity)` — Primary / Connection / Secondary / Inline.
- `LabelExpression(kind)` — the Neo4j label expression (e.g. `PrimaryNode:GenericNode:Node`).

**`Cypher/Direct/EntityWalker.cs`** — one ggifc entity → `EntityData` (shared).
- `Walk(entity, ts)` — produces `EntityData { Properties, Edges, Inlines }`. Properties
  come from the Part-21 STEP line (lossless); edges/inlines from reflection.
- Defines `record EntityData / EdgeData / InlineData`.

**`Cypher/Direct/CypherEmitter.cs`** — the Neo4j sink (shared class for Direct + Live).
- `WriteAsync(driver, db, ts, owner)` — **full baseline**: `MATCH {timestamp} DETACH
  DELETE`, then `BulkMergeNodes` (Primary/Connection/Secondary) + `BulkMergeEdges` +
  `BulkCreateInlines`.
- `ApplyRuleAsync(driver, rule)` — **increment, single all-or-nothing transaction**:
  1. Remove/Replace → `DETACH DELETE` old nodes by `{timestamp, revit_element_id}`.
  2. Insert/Replace → MERGE the graphlet's nodes/edges/inlines.
  3. `SharedRefresh` → MERGE shared node props, replace its whole outgoing edge set
     (list_index renumbers).
  4. `SharedDelete` → drop shared nodes that became memberless.
- `WalkOwned(entity, ts, owner)` — walk one entity and stamp `revit_element_id` onto the
  node and its inlines (so graphlet deletion reaches inlines, no orphans).
- `BulkMergeNodes / BulkMergeEdges / BulkCreateInlines` (private) — emit the
  `UNWIND ... MERGE/CREATE` Cypher; called by both baseline and increment.

---

## One-line file roster

- `RevitGraphApp.cs` — app entry: ribbon + `DocumentChanged`/`DocumentClosing` subscriptions.
- `LiveSyncToggleCommand.cs` — toggle button; calls `Toggle`, shows status.
- `LiveSyncManager.cs` — static session holder; routes DocumentChanged into the session; fail loud.
- `LiveSyncSession.cs` — per-document mirror; baseline snapshot + Insert/Replace/Remove.
- `LiveSyncLog.cs` — append-only `%TEMP%` diagnostics (no UI allowed in events).
- `Neo4jConfig.cs` — resolves bolt URI + credentials from `NEO4J_LOCAL_*`.
- `Ifc/ModelAssembler.cs` — Phase A: boilerplate + convert-all into the ggifc tree.
- `Ifc/IfcModelContext.cs` — the in-memory mirror (`Db`, `OwnerByStepId`, `ConvertedElements`, storeys).
- `Ifc/Converters/ElementConverterRegistry.cs` — element→converter dispatch, ownership tagging, `TryConvertOne`.
- `Ifc/StepIdWatermark.cs` — highest-StepId watermark isolating one element's graphlet.
- `Cypher/Direct/GraphRule.cs` — `RuleOp`/`GraphRule` + `GraphletExtractor` (watermark walk, containment changes).
- `Cypher/Direct/LiveRuleBuilder.cs` — one change → one `GraphRule`; ggifc-side detach/forget.
- `Cypher/Direct/NodeClassifier.cs` — ConMan2 node-kind taxonomy + labels.
- `Cypher/Direct/EntityWalker.cs` — ggifc entity → `EntityData` (node/edges/inlines).
- `Cypher/Direct/CypherEmitter.cs` — Neo4j sink: `WriteAsync` (baseline) + `ApplyRuleAsync` (increment).
