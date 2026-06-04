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
        IReadOnlyDictionary<ElementId, IfcBuildingStorey> storeyByLevel)
    {
        Db = db;
        BodyContext = bodyContext;
        StoreyByLevel = storeyByLevel;
    }

    /// <summary>The in-memory IFC tree; converters add their sub-graphs here.</summary>
    public DatabaseIfc Db { get; }

    /// <summary>The 'Body' sub-context, used as the context of shape representations.</summary>
    public IfcGeometricRepresentationSubContext BodyContext { get; }

    /// <summary>Revit <see cref="Level"/> id → the IfcBuildingStorey built from it.</summary>
    public IReadOnlyDictionary<ElementId, IfcBuildingStorey> StoreyByLevel { get; }
}
