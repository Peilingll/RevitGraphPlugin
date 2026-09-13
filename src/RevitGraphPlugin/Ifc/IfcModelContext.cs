using System.Collections.Generic;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// The ggifc database plus the anchors converters attach to. Built by
/// <see cref="BoilerplateBuilder"/>; live sync keeps it as the in-memory mirror.
/// </summary>
public sealed class IfcModelContext
{
    public IfcModelContext(
        DatabaseIfc db,
        IfcGeometricRepresentationSubContext bodyContext,
        IfcBuilding building,
        Dictionary<ElementId, IfcBuildingStorey> storeyByLevel)
    {
        Db = db;
        BodyContext = bodyContext;
        Building = building;
        StoreyByLevel = storeyByLevel;
    }

    /// <summary>The single IfcBuilding; new storeys (levels added live) aggregate under it.</summary>
    public IfcBuilding Building { get; }

    /// <summary>The in-memory IFC tree; converters add their sub-graphs here.</summary>
    public DatabaseIfc Db { get; }

    /// <summary>The 'Body' sub-context, used as the context of shape representations.</summary>
    public IfcGeometricRepresentationSubContext BodyContext { get; }

    /// <summary>
    /// Revit <see cref="Level"/> id → the IfcBuildingStorey built from it. Mutable: live
    /// sync adds a storey when a level is added and drops it when the level is deleted.
    /// </summary>
    public Dictionary<ElementId, IfcBuildingStorey> StoreyByLevel { get; }

    /// <summary>Revit element id → its principal IfcElement (host lookup for hosted elements; modify / remove lookup for live sync).</summary>
    public IDictionary<ElementId, IfcElement> ConvertedElements { get; }
        = new Dictionary<ElementId, IfcElement>();

    /// <summary>
    /// ggifc StepId → the Revit element whose conversion created it; written to the graph
    /// as <c>revit_element_id</c>. Boilerplate stays untagged (shared), except that each
    /// storey, its pset and their rel are tagged with the level's id.
    /// </summary>
    public Dictionary<int, long> OwnerByStepId { get; } = new();
}
