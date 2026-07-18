using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Neo4j.Driver;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Ifc;

namespace RevitGraphPlugin;

/// <summary>
/// Sync via the DIRECT-WRITE pipeline: build the ggifc tree (Phase A, shared with
/// <see cref="SyncCommand"/>) then walk it straight into Neo4j with
/// <see cref="CypherEmitter"/> — no temp IFC, no Python. Counterpart to the
/// temp-IFC bridge; both write the same ConMan2-shaped graph, under DISTINCT
/// timestamps so the two can be diffed in ConMan2 to prove equivalence.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SyncDirectCommand : IExternalCommand
{
    private const string Timestamp = "plugin-direct";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            TaskDialog.Show("RevitGraphPlugin", "No active document.");
            return Result.Cancelled;
        }

        // Phase A — identical to the bridge path.
        IfcModelContext ctx;
        try
        {
            ctx = ModelAssembler.Build(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"Failed to build IFC model:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        // Phase B — direct write: walk the tree → Cypher → Neo4j (bolt), no temp IFC.
        CypherEmitter.EmitStats stats;
        try
        {
            var (uri, user, password) = Neo4jConfig();
            using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
            // Run on a thread-pool thread: blocking on the async write directly from
            // Revit's UI thread (which carries a SynchronizationContext) deadlocks the
            // library's awaiting continuations. Task.Run gives them a context-free thread.
            stats = Task.Run(() => CypherEmitter.WriteAsync(driver, ctx.Db, Timestamp))
                        .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitGraphPlugin",
                $"Direct write failed:\n{ex.GetBaseException().Message}");
            return Result.Failed;
        }

        TaskDialog.Show("RevitGraphPlugin",
            $"Sync done via DIRECT-WRITE (no temp IFC).\n\n" +
            $"Timestamp: {Timestamp}\n" +
            $"Primary: {stats.PrimaryNodes}   Connection: {stats.ConnectionNodes}\n" +
            $"Secondary: {stats.SecondaryNodes}   Inline: {stats.InlineNodes}\n" +
            $"Edges: {stats.Edges}");
        return Result.Succeeded;
    }

    /// <summary>
    /// Mirror ConMan2's Neo4jConnection env-var convention so both pipelines hit the
    /// same database with the same credentials (NEO4J_LOCAL_*).
    /// </summary>
    private static (string uri, string user, string password) Neo4jConfig()
    {
        var user = Environment.GetEnvironmentVariable("NEO4J_LOCAL_USERNAME") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PASSWORD") ?? "password";
        var host = Environment.GetEnvironmentVariable("NEO4J_LOCAL_HOSTNAME") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("NEO4J_LOCAL_PORT") ?? "7687";
        if (host == "localhost") host = "127.0.0.1";   // force IPv4 (matches ConMan2)
        return ($"bolt://{host}:{port}", user, password);
    }
}
