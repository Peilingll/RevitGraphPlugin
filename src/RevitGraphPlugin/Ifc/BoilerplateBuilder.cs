using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// The empty-project boilerplate, laid out like Revit's native IFC4 export: IfcProject +
/// OwnerHistory chain, units (mm), representation contexts, Site → Building → Storeys
/// (from Revit Levels), and their default property sets.
/// </summary>
public static class BoilerplateBuilder
{
    public static IfcModelContext Build(Document doc)
    {
        var db = new DatabaseIfc(ReleaseVersion.IFC4A2);
        var factory = db.Factory;

        var projInfo = doc.ProjectInformation;
        // Native: IfcProject.Name <- "Project Number", LongName <- "Project Name".
        var projNumber = string.IsNullOrWhiteSpace(projInfo.Number) ? "Project" : projInfo.Number;

        // -- Project (ggifc auto-creates the OwnerHistory chain).
        var project = new IfcProject(db, projNumber);
        project.GlobalId = IfcGuidConverter.ForElement(projInfo);
        if (!string.IsNullOrWhiteSpace(projInfo.Name))   project.LongName = projInfo.Name;
        if (!string.IsNullOrWhiteSpace(projInfo.Status)) project.Phase    = projInfo.Status;

        // Overwrite ggifc's OwnerHistory defaults with what Revit's exporter writes.
        RevitOwnerHistory.Override(project, doc);

        // -- Units: millimetre (as native); every length below is emitted in mm.
        var lengthUnit = new IfcSIUnit(db, IfcUnitEnum.LENGTHUNIT, IfcSIPrefix.MILLI, IfcSIUnitName.METRE);
        var areaUnit   = new IfcSIUnit(db, IfcUnitEnum.AREAUNIT,   IfcSIPrefix.NONE, IfcSIUnitName.SQUARE_METRE);
        var volumeUnit = new IfcSIUnit(db, IfcUnitEnum.VOLUMEUNIT, IfcSIPrefix.NONE, IfcSIUnitName.CUBIC_METRE);
        project.UnitsInContext = new IfcUnitAssignment(new IfcUnit[] { lengthUnit, areaUnit, volumeUnit });

        // -- Representation context + 4 sub-contexts (registered on the project by ggifc).
        var modelContext = factory.GeometricRepresentationContext(
            IfcGeometricRepresentationContext.GeometricContextIdentifier.Model);
        modelContext.Precision = 0.01;   // match Revit native (0.01 mm); ggifc default emits 0.0001
        var bodyContext = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Body);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Axis);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.BoundingBox);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.FootPrint);

        // -- Site -> Building -> Storeys (ggifc creates the RelAggregates).
        var site = new IfcSite(db, "Default");   // native Site Name is 'Default'
        site.GlobalId = IfcGuidConverter.FromSeed(projInfo.UniqueId + ":Site");
        site.CompositionType = IfcElementCompositionEnum.ELEMENT;
        site.RefElevation = 0;

        // Site location: Revit radians → IFC compound angle.
        var siteLocation = doc.SiteLocation;
        if (siteLocation != null)
        {
            site.RefLatitude  = ToCompoundPlaneAngle(siteLocation.Latitude);
            site.RefLongitude = ToCompoundPlaneAngle(siteLocation.Longitude);
        }
        _ = new IfcRelAggregates(project, site) { GlobalId = StableIds.Seed(project, "Aggregates") };

        var building = new IfcBuilding(site, "Default Building");
        building.GlobalId = IfcGuidConverter.FromSeed(projInfo.UniqueId + ":Building");
        building.CompositionType = IfcElementCompositionEnum.ELEMENT;
        StableIds.StampAggregates(building);   // site → building rel: stable GlobalId

        // -- Building postal address, as Revit's exporter derives it: the project address
        //    as the address line, town / region / country parsed from the site's place
        //    name ("Town, Country" or "Town, Region, Country").
        var address = new IfcPostalAddress(db);
        if (!string.IsNullOrWhiteSpace(projInfo.Address))
            address.AddressLines.Add(projInfo.Address);
        var place = (siteLocation?.PlaceName ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (place.Length >= 2)
        {
            address.Town = place[0];
            address.Region = place.Length >= 3 ? place[1] : place[0];
            address.Country = place[^1];
        }
        building.BuildingAddress = address;

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        var storeys = new List<(IfcBuildingStorey Storey, string LevelTypeName)>();
        var storeyByLevel = new Dictionary<ElementId, IfcBuildingStorey>();
        foreach (var level in levels)
        {
            // Same entity LevelConverter builds for a level added live.
            var storey = Converters.LevelConverter.CreateStorey(level, building, doc);
            storeys.Add((storey, doc.GetElement(level.GetTypeId())?.Name ?? "Level"));
            storeyByLevel[level.Id] = storey;
        }

        // -- Default Pset_*Common on Site / Building / Storeys, with the deduplicated
        //    property values native uses.
        AttachDefaultPropertySets(db, site, building, storeys);

        var ctx = new IfcModelContext(db, bodyContext, building, storeyByLevel);

        // Tag storey + pset + rel with the level's id (live modify / remove); the shared
        // pset values stay untagged.
        foreach (var (levelId, storey) in storeyByLevel)
        {
            ctx.OwnerByStepId[storey.StepId] = levelId.Value;
            foreach (var rel in storey.IsDefinedBy)
            {
                ctx.OwnerByStepId[rel.StepId] = levelId.Value;
                foreach (var pset in rel.RelatingPropertyDefinition)
                    ctx.OwnerByStepId[pset.StepId] = levelId.Value;
            }
        }
        return ctx;
    }

    /// <summary>
    /// The Pset_*Common entities Revit's exporter attaches to Site / Building / Storeys on
    /// an empty Architectural template, including the four template Psets it puts on the
    /// Building itself.
    /// </summary>
    private static void AttachDefaultPropertySets(
        DatabaseIfc db,
        IfcSite site,
        IfcBuilding building,
        IReadOnlyList<(IfcBuildingStorey Storey, string LevelTypeName)> storeys)
    {
        var unknown = IfcLogicalEnum.UNKNOWN;

        // Deduplicated property values (reused across Psets, as native). A storey's
        // Reference is its level type name; storeys of the same type share one value.
        var refProjInfo = new IfcPropertySingleValue(db, "Reference",
            new IfcIdentifier("Project Information"));
        var refByLevelType = new Dictionary<string, IfcPropertySingleValue>(StringComparer.Ordinal);
        var aboveGround = new IfcPropertySingleValue(db, "AboveGround",
            new IfcLogical(unknown));
        var numberOfStoreys = new IfcPropertySingleValue(db, "NumberOfStoreys",
            new IfcInteger(0));
        var isLandmarked = new IfcPropertySingleValue(db, "IsLandmarked",
            new IfcLogical(unknown));
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));

        // IfcPropertySet(name, props[]) populates HasProperties (keyed by property name).
        StableIds.AttachPset(site, "Pset_SiteCommon", refProjInfo);

        foreach (var (storey, levelTypeName) in storeys)
        {
            if (!refByLevelType.TryGetValue(levelTypeName, out var reference))
                refByLevelType[levelTypeName] = reference =
                    new IfcPropertySingleValue(db, "Reference", new IfcIdentifier(levelTypeName));
            StableIds.AttachPset(storey, "Pset_BuildingStoreyCommon", reference, aboveGround);
        }

        // Four template Psets on the Building (as native).
        StableIds.AttachPset(building, "Pset_BuildingCommon", refProjInfo, numberOfStoreys, isLandmarked);

        StableIds.AttachPset(building, "Pset_BuildingElementProxyCommon", refProjInfo, isExternal);

        StableIds.AttachPset(building, "Pset_BuildingStoreyCommon", refProjInfo, aboveGround);

        StableIds.AttachPset(building, "Pset_BuildingSystemCommon", refProjInfo);
    }

    /// <summary>Radians → IFC compound plane angle (deg, min, sec, millionth-sec), sign on every component.</summary>
    private static IfcCompoundPlaneAngleMeasure ToCompoundPlaneAngle(double radians)
    {
        var totalSeconds = radians * (180.0 / Math.PI) * 3600.0;
        var sign = Math.Sign(totalSeconds);
        var abs  = Math.Abs(totalSeconds);
        var degrees = (int)(abs / 3600);
        var minutes = (int)(abs % 3600 / 60);
        var seconds = (int)(abs % 60);
        // Truncate, not round (as Revit's exporter does).
        var micro   = (int)((abs - Math.Floor(abs)) * 1_000_000);
        return new IfcCompoundPlaneAngleMeasure(
            sign * degrees, sign * minutes, sign * seconds, sign * micro);
    }
}
