using Autodesk.Revit.DB;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;
using RevitGraphPlugin.Ifc.Converters;

namespace RevitGraphPlugin;

/// <summary>
/// The live incremental sync session for one Revit document (plan 子步驟 3):
/// baseline-then-increment. <see cref="Start"/> performs a full snapshot through the
/// verified direct pipeline and keeps the ggifc <see cref="IfcModelContext"/> alive as
/// the in-memory mirror of the document; each subsequent element change is converted
/// into a <see cref="GraphRule"/> and applied to the current-state graph in one
/// transaction. Restarting a session simply re-baselines (wipe + rewrite of the live
/// timestamp), which also self-heals any p21_id drift across Revit restarts.
/// All methods must be called from the Revit API thread (they touch Elements and the
/// shared ggifc db); the Neo4j writes block via Task.Run like SyncDirectCommand —
/// 子步驟 4 decides queueing/threading when wiring DocumentChanged.
/// </summary>
public sealed class LiveSyncSession : IDisposable
{
    public const string Timestamp = "plugin-live";

    private readonly IDriver _driver;
    private readonly IfcModelContext _ctx;
    private readonly ElementConverterRegistry _registry = new();

    public CypherEmitter.EmitStats BaselineStats { get; }

    private LiveSyncSession(IDriver driver, IfcModelContext ctx, CypherEmitter.EmitStats baseline)
    {
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
            return new LiveSyncSession(driver, ctx, stats);
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
        return Upsert(element, known ? RuleOp.Replace : RuleOp.Insert);
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

    private void Apply(GraphRule rule)
        => Task.Run(() => CypherEmitter.ApplyRuleAsync(_driver, rule)).GetAwaiter().GetResult();

    public void Dispose() => _driver.Dispose();
}
