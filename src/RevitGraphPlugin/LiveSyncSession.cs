using Autodesk.Revit.DB;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using RevitGraphPlugin.Ifc.Converters;

namespace RevitGraphPlugin;

/// <summary>
/// The live incremental sync session for one Revit document (plan step 3):
/// baseline-then-increment. <see cref="Start"/> performs a full snapshot through the
/// verified direct pipeline and keeps the ggifc <see cref="IfcModelContext"/> alive as
/// the in-memory mirror of the document; each subsequent element change is converted
/// into a <see cref="GraphRule"/> and applied to the current-state graph in one
/// transaction. Restarting a session simply re-baselines (wipe + rewrite of the live
/// timestamp), which also self-heals any p21_id drift across Revit restarts.
/// All methods must be called from the Revit API thread (they touch Elements and the
/// shared ggifc db); the Neo4j writes block via Task.Run like SyncDirectCommand —
/// step 4 decides queueing/threading when wiring DocumentChanged.
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

    /// <summary>
    /// Begin a session: build the full ggifc model from the document and write the
    /// baseline snapshot under <see cref="Timestamp"/> (wipe + rewrite, so enabling
    /// live sync is always a clean re-baseline).
    /// </summary>
    public static LiveSyncSession Start(Document doc)
    {
        var ctx = ModelAssembler.Build(doc);
        var (uri, user, password) = Neo4jConfig.Resolve();
        var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        try
        {
            var stats = Task.Run(() => CypherEmitter.WriteAsync(driver, ctx.Db, Timestamp, ctx.OwnerByStepId))
                            .GetAwaiter().GetResult();
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
    /// Element modified in Revit → detach + re-convert + Replace rule (old graphlet
    /// deleted by <c>revit_element_id</c>, new one inserted, same GlobalId). Elements
    /// we never converted (e.g. modified before live sync supported them) fall back to
    /// a plain Insert. False if unsupported.
    /// </summary>
    public bool ApplyModified(Element element)
    {
        if (!Supports(element)) return false;

        var known = _ctx.ConvertedElements.TryGetValue(element.Id, out var old);
        if (known)
        {
            LiveRuleBuilder.DetachFromContainment(old!);
            LiveRuleBuilder.ForgetOwnership(_ctx.OwnerByStepId, element.Id.Value);
        }
        var applied = Upsert(element, known ? RuleOp.Replace : RuleOp.Insert);

        // A re-converted host (wall) is a brand-new ggifc object; any hosted insert's
        // opening/void/fill still references the old, now-deleted object, so its
        // IfcRelVoidsElement.RelatingBuildingElement goes null. Re-sync each insert so
        // its opening re-attaches to the new host. (Placing OR moving a window/door
        // modifies its host wall, so without this every hosted element breaks.)
        if (applied) ResyncHostedInserts(element);
        return applied;
    }

    /// <summary>
    /// Re-sync the hosted inserts (windows / doors) of a just-re-converted host so their
    /// opening chain re-attaches to the new host object. No-op for non-hosts. Each insert
    /// is re-applied as a modify (Replace): its old opening/void/fill — owned by the
    /// insert's revit_element_id — is deleted and rebuilt against the current host, which
    /// the insert's converter finds via the (now-updated) ConvertedElements[host.Id].
    /// </summary>
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

    /// <summary>
    /// Element deleted in Revit → detach its ggifc mirror + Remove rule. Deleted
    /// elements have no body anymore, so this takes only the id — everything needed
    /// lives in the session (ConvertedElements) and the graph (revit_element_id).
    /// False if the element was never part of the session.
    /// </summary>
    public bool ApplyRemoved(ElementId id)
    {
        if (!_ctx.ConvertedElements.TryGetValue(id, out var old)) return false;

        LiveRuleBuilder.DetachFromContainment(old);
        LiveRuleBuilder.ForgetOwnership(_ctx.OwnerByStepId, id.Value);
        _ctx.ConvertedElements.Remove(id);

        var rule = LiveRuleBuilder.BuildRemove(
            _ctx.StoreyByLevel.Values, id.Value, Timestamp);
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
            element.Id.Value, before, after, Timestamp);
        Apply(rule);
        return true;
    }

    /// <summary>
    /// Apply one rule and hand back the completed rule — the same rule plus its
    /// <see cref="GraphRule.BeforeGraphlet"/> (L side, captured inside the transaction).
    /// Nothing consumes the return value yet; rule persistence (step 3 of
    /// doc_process/2026-08-02-plan-rule-persistence.md) is what will store it.
    /// </summary>
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
