using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Build an in-memory IFC4 tree for the "empty project" boilerplate:
/// - IfcProject (with the OwnerHistory chain ggifc auto-creates: Person/Org/Application/PersonAndOrganization)
/// - IfcUnitAssignment + IfcSIUnit (length / area / volume in metric)
/// - IfcGeometricRepresentationContext (Model) + 4 SubContexts (Body / Axis / BoundingBox / FootPrint)
/// - IfcSite + IfcBuilding + IfcBuildingStorey (from Revit Levels) tied together with IfcRelAggregates
///
/// ggifc's IfcProject(db, name) constructor only creates the project + OwnerHistory chain. Units,
/// representation contexts, and spatial breakdown all have to be constructed explicitly here.
/// </summary>
public static class BoilerplateBuilder
{
    public static DatabaseIfc Build(Document doc)
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4);
        var factory = db.Factory;

        var projInfo = doc.ProjectInformation;
        var projName = string.IsNullOrWhiteSpace(projInfo.Name) ? "Project" : projInfo.Name;

        // -- Project (ggifc auto-creates OwnerHistory + Person/Org/App chain alongside it).
        var project = new IfcProject(db, projName);
        project.GlobalId = IfcGuidConverter.FromRevitUniqueId(projInfo.UniqueId);
        if (!string.IsNullOrWhiteSpace(projInfo.Name))   project.LongName = projInfo.Name;
        if (!string.IsNullOrWhiteSpace(projInfo.Status)) project.Phase    = projInfo.Status;

        // Replace ggifc's defaults on the auto-created chain (Sandy / GeometryGymIFC / Unknown
        // / ChangeAction=ADDED) with values that match Revit's own IFC exporter output. See
        // RevitOwnerHistory.cs for the per-attribute source provenance.
        RevitOwnerHistory.Override(project, doc);

        // -- Units: metric (matches Revit's IFC 4 Reference View export).
        var lengthUnit = new IfcSIUnit(db, IfcUnitEnum.LENGTHUNIT, IfcSIPrefix.NONE, IfcSIUnitName.METRE);
        var areaUnit   = new IfcSIUnit(db, IfcUnitEnum.AREAUNIT,   IfcSIPrefix.NONE, IfcSIUnitName.SQUARE_METRE);
        var volumeUnit = new IfcSIUnit(db, IfcUnitEnum.VOLUMEUNIT, IfcSIPrefix.NONE, IfcSIUnitName.CUBIC_METRE);
        project.UnitsInContext = new IfcUnitAssignment(new IfcUnit[] { lengthUnit, areaUnit, volumeUnit });

        // -- Geometric Representation Context (Model) + 4 SubContexts.
        //    Factory methods register them on the project automatically.
        _ = factory.GeometricRepresentationContext(
            IfcGeometricRepresentationContext.GeometricContextIdentifier.Model);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Body);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Axis);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.BoundingBox);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.FootPrint);

        // -- Spatial breakdown: Site -> Building -> Storeys (RelAggregates auto-created
        //    by ggifc when the parent is passed to the constructor).
        var site = new IfcSite(db, "Default Site");
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
