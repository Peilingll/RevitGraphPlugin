using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Build an in-memory IFC4 tree representing the "empty project" boilerplate:
/// Project + Site + Building + Storeys (from Revit Levels) + the supporting
/// boilerplate (UnitAssignment, GeometricRepresentationContext + SubContexts,
/// OwnerHistory + Person/Org/Application chain) that ggifc auto-creates when
/// IfcProject is constructed.
/// </summary>
public static class BoilerplateBuilder
{
    public static DatabaseIfc Build(Document doc)
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);

        // -- Project: ggifc auto-creates UnitAssignment + GeometricRepresentationContext
        //             + OwnerHistory + Person/Org/Application chain.
        var projInfo = doc.ProjectInformation;
        var projName = string.IsNullOrWhiteSpace(projInfo.Name) ? "Project" : projInfo.Name;
        var project  = new IfcProject(db, projName);
        project.GlobalId = IfcGuidConverter.FromRevitUniqueId(projInfo.UniqueId);
        if (!string.IsNullOrWhiteSpace(projInfo.Name))     project.LongName = projInfo.Name;
        if (!string.IsNullOrWhiteSpace(projInfo.Status))   project.Phase    = projInfo.Status;

        // -- Spatial breakdown: Site -> Building -> Storeys (RelAggregates auto-created
        //    by ggifc when a parent entity is passed to the constructor).
        var site     = new IfcSite(db, "Default Site");
        _ = new IfcRelAggregates(project, site);

        var building = new IfcBuilding(site, "Default Building");

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        foreach (var level in levels)
        {
            var elevationMetres = UnitUtils.ConvertFromInternalUnits(
                level.Elevation,
                UnitTypeId.Meters);

            var storey = new IfcBuildingStorey(building, level.Name, elevationMetres);
            storey.GlobalId = IfcGuidConverter.FromRevitUniqueId(level.UniqueId);
        }

        return db;
    }
}
