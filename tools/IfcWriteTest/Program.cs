using GeometryGym.Ifc;
using Neo4j.Driver;
using RevitGraphPlugin;
using RevitGraphPlugin.Cypher;
using RevitGraphPlugin.Mapping;

namespace RevitGraphPlugin.Tools.IfcWriteTest;

/// <summary>
/// Stage 2 verification harness: builds an in-memory "window on wall" IFC
/// scenario via GeometryGym.Ifc, runs <see cref="IfcGraphMapper"/> +
/// <see cref="Neo4jGraphWriter"/>, and prints the resulting graph shape so
/// the user can spot-check it against design.md §4 Stage 5.
///
/// Credentials follow the same env-var convention as the rest of the project.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("NEO4J_PASSWORD environment variable is required.");
            return 2;
        }

        var batch = BuildScenarioGraphBatch();
        Console.WriteLine($"Built batch: {batch.Nodes.Count} nodes, {batch.Edges.Count} edges");
        foreach (var n in batch.Nodes)
            Console.WriteLine($"  node #{n.P21Id,-3} {n.Kind,-10} {n.EntityType,-30} GlobalId={n.GlobalId}");
        foreach (var e in batch.Edges)
            Console.WriteLine($"  edge #{e.FromP21Id} -[{e.RelType} idx={e.ListIndex}]-> #{e.ToP21Id}");

        await using var driver = GraphDatabase.Driver(
            Environment.GetEnvironmentVariable("NEO4J_URI") ?? "neo4j://127.0.0.1:7687",
            AuthTokens.Basic(
                Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j",
                password),
            o => o.WithConnectionTimeout(TimeSpan.FromSeconds(5)));

        await driver.VerifyConnectivityAsync();
        await Neo4jSchema.EnsureAsync(driver);
        await Neo4jGraphWriter.WriteAsync(driver, batch);

        Console.WriteLine();
        Console.WriteLine("Wrote to Neo4j. Verify with:");
        Console.WriteLine("  MATCH (n:GenericNode) RETURN n.EntityType, n.p21_id, n.GlobalId, n.kind ORDER BY n.p21_id");
        Console.WriteLine("  MATCH (a)-[r:rel]->(b) RETURN a.EntityType, r.rel_type, r.list_index, b.EntityType");
        return 0;
    }

    private static Graph.GraphBatch BuildScenarioGraphBatch()
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4X3_RC4);
        _ = new IfcProject(db, "Stage2");
        var site = new IfcSite(db, "Site");
        var building = new IfcBuilding(site, "Building");
        var storey = new IfcBuildingStorey(building, "L1", 0.0);

        var wall = new IfcWall(storey, null, null) { Name = "Wall-1" };
        var opening = new IfcOpeningElement(wall, null, null) { Name = "Opening-1" };
        var window = new IfcWindow(storey, null, null) { Name = "Window-1", OverallHeight = 1500, OverallWidth = 800 };
        // GG.Ifc auto-creates IfcRelVoidsElement (wall ↔ opening) but NOT
        // IfcRelFillsElement (opening ↔ window). Create it explicitly so the
        // Stage 5 "window on wall" subgraph is exercised end-to-end here.
        _ = new IfcRelFillsElement(opening, window);

        // GeometryGym auto-creates IfcRelVoidsElement when an IfcOpeningElement
        // is constructed with an IfcElement host. Same for IfcRelFillsElement
        // when an IfcWindow's host is an IfcOpeningElement. We map the entire
        // database so the relations (which are not forward-reachable from the
        // wall) are picked up too.
        return IfcGraphMapper.MapAll(db);
    }
}
