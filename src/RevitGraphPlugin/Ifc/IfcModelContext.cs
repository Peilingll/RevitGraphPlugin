using System.Collections.Generic;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Shared state produced by <see cref="BoilerplateBuilder"/> (stage 1: the empty
/// IFC skeleton) and consumed by the per-element converters (stage 2): the IFC
/// database plus the spatial / geometric anchors converters need to attach
/// elements correctly.
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

    /// <summary>
    /// Revit element id → the IfcElement a converter produced for it. Lets
    /// hosted-element converters (windows / doors) wire back to their host (the
    /// IfcWall). Populated by converters as they run, so it relies on registration
    /// order (hosts before hosted) — see <see cref="Converters.ElementConverterRegistry"/>.
    /// </summary>
    public IDictionary<ElementId, IfcElement> ConvertedElements { get; }
        = new Dictionary<ElementId, IfcElement>();

    /// <summary>
    /// ggifc StepId → the Revit element id (its <see cref="ElementId.Value"/>) whose
    /// conversion created that entity. Ownership is captured by the registry as a
    /// StepId watermark around each converter call, so every entity a converter adds
    /// (element, placement, geometry, psets, openings…) is tagged with its element.
    /// Boilerplate entities (units, contexts, owner history, site, building) are created
    /// before any converter runs and stay untagged — they are shared resources that
    /// incremental removal must never touch. Storeys are the exception: each storey, its
    /// Pset_BuildingStoreyCommon and the rel between them are tagged with the LEVEL's id
    /// (the deduplicated Pset values stay shared and untagged), so a level modified or
    /// deleted live can be located in the graph like any element. Consumed by the direct
    /// pipeline, which writes it as the <c>revit_element_id</c> node property.
    /// </summary>
    public Dictionary<int, long> OwnerByStepId { get; } = new();
}
