using Autodesk.Revit.DB;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using RevitGraphPlugin.Ifc.Converters;

namespace RevitGraphPlugin;

/// <summary>
/// Live sync for one Revit document: <see cref="Start"/> writes a full baseline and keeps
/// the ggifc <see cref="IfcModelContext"/> as the in-memory mirror; each later element
/// change becomes one <see cref="GraphRule"/>, applied and stored in one transaction.
/// Restarting re-baselines (wipe + rewrite). Call only from the Revit API thread.
/// </summary>
public sealed class LiveSyncSession : IDisposable
{
    public const string Timestamp = "plugin-live";

    private readonly IDriver _driver;
    private readonly IfcModelContext _ctx;
    private readonly ElementConverterRegistry _registry = new();

    /// <summary>The document this session mirrors (event filtering key).</summary>
    public Document Document { get; }

    public CypherEmitter.EmitStats BaselineStats { get; }

    private LiveSyncSession(
        Document doc, IDriver driver, IfcModelContext ctx, CypherEmitter.EmitStats baseline)
    {
        Document = doc;
        _driver = driver;
        _ctx = ctx;
        BaselineStats = baseline;
    }

    /// <summary>Build the full ggifc model and write the baseline snapshot (wipe + rewrite under <see cref="Timestamp"/>).</summary>
    public static LiveSyncSession Start(Document doc)
    {
        var ctx = ModelAssembler.Build(doc);
        var (uri, user, password) = Neo4jConfig.Resolve();
        var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        try
        {
            var stats = Task.Run(() => CypherEmitter.WriteAsync(driver, ctx.Db, Timestamp, ctx.OwnerByStepId))
                            .GetAwaiter().GetResult();

            // Record the re-baseline in the rule chain: replay must know the graph was rebuilt here.
            var anchor = Task.Run(() => RuleStore.RecordBaselineAsync(driver, Timestamp, stats))
                             .GetAwaiter().GetResult();
            LiveSyncLog.Write($"  baseline anchor: seq={anchor.Seq} ({anchor.Timestamp})");

            return new LiveSyncSession(doc, driver, ctx, stats);
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    /// <summary>True if the live pipeline covers this element's category.</summary>
    public bool Supports(Element element)
        => element.Category is not null && _registry.Supports(element.Category.BuiltInCategory);

    /// <summary>Batch processing order (hosts before hosted; unsupported last).</summary>
    public int Priority(Element element)
        => element.Category is null
            ? int.MaxValue
            : _registry.ConversionPriority(element.Category.BuiltInCategory);

    /// <summary>Element added in Revit → convert + Insert rule. False if unsupported.</summary>
    public bool ApplyAdded(Element element) => Upsert(element, RuleOp.Insert);

    /// <summary>
    /// Element modified → re-convert and Replace (same GlobalId). Elements never
    /// converted fall back to Insert. False if unsupported.
    /// </summary>
    public bool ApplyModified(Element element)
    {
        if (!Supports(element)) return false;
        if (element is Level level) return ModifyLevel(level);

        var known = _ctx.ConvertedElements.TryGetValue(element.Id, out var old);
        if (known)
        {
            LiveRuleBuilder.DetachFromContainment(old!);
            LiveRuleBuilder.ForgetOwnership(_ctx.OwnerByStepId, element.Id.Value);
        }
        var applied = Upsert(element, known ? RuleOp.Replace : RuleOp.Insert);

        // A re-converted host is a new ggifc object; re-sync its inserts so their opening
        // chain points at it in the mirror (in the graph this usually diffs to NoChange).
        if (applied) ResyncHostedInserts(element);
        return applied;
    }

    /// <summary>Re-apply the hosted inserts (windows / doors) of a re-converted host as modifies so their opening chain re-attaches to the new host object.</summary>
    private void ResyncHostedInserts(Element element)
    {
        if (element is not HostObject host) return;

        ICollection<ElementId> inserts;
        try { inserts = host.FindInserts(true, true, false, true); }
        catch { return; }   // some host types don't support FindInserts

        foreach (var id in inserts)
        {
            var insert = Document.GetElement(id);
            if (insert is not null && Supports(insert))
                ApplyModified(insert);
        }
    }

    /// <summary>Element deleted → detach its mirror entities and apply a Remove rule. False if the element was never part of the session.</summary>
    public bool ApplyRemoved(ElementId id)
    {
        if (_ctx.StoreyByLevel.ContainsKey(id)) return RemoveLevel(id);
        if (!_ctx.ConvertedElements.TryGetValue(id, out var old)) return false;

        LiveRuleBuilder.DetachFromContainment(old);
        LiveRuleBuilder.ForgetOwnership(_ctx.OwnerByStepId, id.Value);
        _ctx.ConvertedElements.Remove(id);

        var rule = LiveRuleBuilder.BuildRemove(
            _ctx.StoreyByLevel.Values, id.Value, Timestamp, _ctx.Building);
        Apply(rule);
        return true;
    }

    /// <summary>True if this id is a level the session mirrors as a storey.</summary>
    public bool IsLevel(ElementId id) => _ctx.StoreyByLevel.ContainsKey(id);

    /// <summary>Every Revit element the session currently mirrors (elements and levels).</summary>
    public IReadOnlyCollection<ElementId> TrackedIds
        => _ctx.ConvertedElements.Keys.Concat(_ctx.StoreyByLevel.Keys).Distinct().ToList();

    /// <summary>
    /// Remove every tracked element that no longer exists in the document. Revit's undo /
    /// redo / rollback events carry no element ids, so this roll-call runs on every event.
    /// </summary>
    public IReadOnlyList<ElementId> ReconcileVanished()
    {
        var gone = TrackedIds.Where(id => Document.GetElement(id) is null).ToList();
        foreach (var id in gone.OrderBy(id => IsLevel(id) ? 1 : 0))
            ApplyRemoved(id);
        return gone;
    }

    /// <summary>
    /// Undo / redo handling: re-convert every tracked element (unchanged ones diff to
    /// NoChange) and insert supported elements the session does not track. O(model).
    /// </summary>
    public (int Modified, int Inserted) ReconcileAll()
    {
        var tracked = TrackedIds
            .Select(Document.GetElement)
            .Where(e => e is not null)
            .OrderBy(Priority)
            .ToList();
        foreach (var element in tracked)
            ApplyModified(element!);

        var known = TrackedIds.ToHashSet();
        var missing = new FilteredElementCollector(Document)
            .WherePasses(new ElementMulticategoryFilter(_registry.Categories.ToList()))
            .WhereElementIsNotElementType()
            .Where(e => !known.Contains(e.Id) && Supports(e))
            .OrderBy(Priority)
            .ToList();
        foreach (var element in missing)
            ApplyAdded(element);

        return (tracked.Count, missing.Count);
    }

    /// <summary>
    /// Level renamed / moved → update the storey in place (never rebuilt: its containment
    /// rel and elements point at it). A level with no storey yet is inserted instead.
    /// </summary>
    private bool ModifyLevel(Level level)
    {
        if (!_ctx.StoreyByLevel.TryGetValue(level.Id, out var storey))
            return Upsert(level, RuleOp.Insert);

        LevelConverter.UpdateStorey(storey, level, Document);
        var rule = LiveRuleBuilder.BuildLevelModify(
            _ctx.Db, _ctx.OwnerByStepId, _ctx.StoreyByLevel.Values, _ctx.Building,
            level.Id.Value, Timestamp);
        Apply(rule);
        return true;
    }

    /// <summary>
    /// Level deleted → detach the storey from the building and apply a Remove: its owned
    /// nodes and containment rels go, the building's aggregation rel is refreshed.
    /// </summary>
    private bool RemoveLevel(ElementId id)
    {
        var storey = _ctx.StoreyByLevel[id];
        var containment = LiveRuleBuilder.DetachStorey(storey);
        _ctx.StoreyByLevel.Remove(id);
        LiveRuleBuilder.ForgetOwnership(_ctx.OwnerByStepId, id.Value);

        var rule = LiveRuleBuilder.BuildRemove(
            _ctx.StoreyByLevel.Values, id.Value, Timestamp, _ctx.Building, containment);
        Apply(rule);
        return true;
    }

    private bool Upsert(Element element, RuleOp op)
    {
        var before = StepIdWatermark.Current(_ctx.Db);
        if (!_registry.TryConvertOne(element, _ctx)) return false;
        var after = StepIdWatermark.Current(_ctx.Db);

        var rule = LiveRuleBuilder.BuildUpsert(
            op, _ctx.Db, _ctx.OwnerByStepId, _ctx.StoreyByLevel.Values,
            element.Id.Value, before, after, Timestamp, _ctx.Building);
        if (rule.Graphlet.Count == 0)
        {
            // Converter produced nothing (e.g. element on a level without a storey): log, skip.
            LiveSyncLog.Write($"    !! {op} {element.Id.Value} ({element.Category?.Name}) produced no entities — not applied");
            return false;
        }
        Apply(rule);
        return true;
    }

    /// <summary>Apply one rule in one Neo4j transaction; returns it completed with its L side and what <see cref="RuleStore"/> recorded.</summary>
    private GraphRule Apply(GraphRule rule)
    {
        var applied = Task.Run(() => CypherEmitter.ApplyRuleAsync(_driver, rule)).GetAwaiter().GetResult();
        LiveSyncLog.Write(applied.Stored is { } s
            ? $"    rule stored: seq={s.Seq} op={s.Op} ({s.RuleTimestamp})"
            : $"    rule not stored ({applied.Op} was semantically empty)");
        return applied;
    }

    public void Dispose() => _driver.Dispose();
}
