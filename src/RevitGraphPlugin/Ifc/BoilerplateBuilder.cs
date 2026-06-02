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

        // -- Units: length in MILLImetre; area/volume metric squared/cubed.
        //    Matches Revit's IFC 4 Reference View export, which uses .MILLI.METRE.
        //    for length (see data/samples/ifc/00_empty.ifc #19). All length values
        //    below (elevations, coordinates) must therefore be emitted in mm too.
        var lengthUnit = new IfcSIUnit(db, IfcUnitEnum.LENGTHUNIT, IfcSIPrefix.MILLI, IfcSIUnitName.METRE);
        var areaUnit   = new IfcSIUnit(db, IfcUnitEnum.AREAUNIT,   IfcSIPrefix.NONE, IfcSIUnitName.SQUARE_METRE);
        var volumeUnit = new IfcSIUnit(db, IfcUnitEnum.VOLUMEUNIT, IfcSIPrefix.NONE, IfcSIUnitName.CUBIC_METRE);
        project.UnitsInContext = new IfcUnitAssignment(new IfcUnit[] { lengthUnit, areaUnit, volumeUnit });

        // -- Geometric Representation Context (Model) + 4 SubContexts.
        //    Factory methods register them on the project automatically.
        var modelContext = factory.GeometricRepresentationContext(
            IfcGeometricRepresentationContext.GeometricContextIdentifier.Model);
        modelContext.Precision = 0.01;   // match Revit native (0.01 mm); ggifc default emits 0.0001
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Body);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.Axis);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.BoundingBox);
        _ = factory.SubContext(IfcGeometricRepresentationSubContext.SubContextIdentifier.FootPrint);

        // -- Spatial breakdown: Site -> Building -> Storeys (RelAggregates auto-created
        //    by ggifc when the parent is passed to the constructor).
        var site = new IfcSite(db, "Default Site");
        site.CompositionType = IfcElementCompositionEnum.ELEMENT;
        site.RefElevation = 0;
        _ = new IfcRelAggregates(project, site);

        var building = new IfcBuilding(site, "Default Building");
        building.CompositionType = IfcElementCompositionEnum.ELEMENT;

        // -- Building postal address. Baseline writes literal empty PostalCode (`''`),
        //    which is preserved exactly via the STEP → ifcopenshell pipeline.
        var address = new IfcPostalAddress(db);
        address.AddressLines.Add("Enter address here");
        address.Town = "London";
        address.Region = "London";
        address.PostalCode = "";
        address.Country = "United Kingdom";
        building.BuildingAddress = address;

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        var storeys = new List<IfcBuildingStorey>();
        foreach (var level in levels)
        {
            var elevationMm = UnitUtils.ConvertFromInternalUnits(
                level.Elevation,
                UnitTypeId.Millimeters);

            var storey = new IfcBuildingStorey(building, level.Name, elevationMm);
            storey.GlobalId = IfcGuidConverter.FromRevitUniqueId(level.UniqueId);
            storey.CompositionType = IfcElementCompositionEnum.ELEMENT;
            storey.LongName = level.Name;   // native mirrors Name into LongName

            // ObjectType = "Level:" + the Level's type name (native exporter writes
            // e.g. 'Level:Circle Head - Project Datum'; the type name also surfaces
            // as the Pset_BuildingStoreyCommon Reference value).
            var levelType = doc.GetElement(level.GetTypeId());
            if (levelType != null)
                storey.ObjectType = "Level:" + levelType.Name;

            storeys.Add(storey);
        }

        // -- Property sets attached to spatial elements. Baseline shows Revit's IFC
        //    exporter emits a fixed set of default Pset_*Common with deduplicated
        //    IfcPropertySingleValue entities reused across multiple Psets. Mirror
        //    the exact layout from data/samples/ifc/00_empty.ifc (#45–#67).
        AttachDefaultPropertySets(db, site, building, storeys);

        return db;
    }

    /// <summary>
    /// Build the Pset_*Common entities Revit's IFC exporter attaches to Site /
    /// Building / Storeys on an empty Architectural template. Mirrors the
    /// concrete layout in <c>data/samples/ifc/00_empty.ifc</c> (#45–#67):
    /// <list type="bullet">
    /// <item>6 deduplicated IfcPropertySingleValue (Reference × 2 strings, AboveGround, NumberOfStoreys, IsLandmarked, IsExternal)</item>
    /// <item>7 IfcPropertySet (Site, Storey × 2, Building × 4)</item>
    /// <item>7 IfcRelDefinesByProperties linking each Pset to its element</item>
    /// </list>
    /// The four Psets attached to <see cref="IfcBuilding"/> reproduce Revit's
    /// quirky behaviour of emitting BuildingElementProxyCommon, BuildingStoreyCommon
    /// and BuildingSystemCommon template Psets on the Building itself.
    /// </summary>
    private static void AttachDefaultPropertySets(
        DatabaseIfc db,
        IfcSite site,
        IfcBuilding building,
        IReadOnlyList<IfcBuildingStorey> storeys)
    {
        var unknown = IfcLogicalEnum.UNKNOWN;

        // Deduplicated property values (reused across multiple Psets, mirroring baseline).
        var refProjInfo = new IfcPropertySingleValue(db, "Reference",
            new IfcIdentifier("Project Information"));
        var refLevelDatum = new IfcPropertySingleValue(db, "Reference",
            new IfcIdentifier("Circle Head - Project Datum"));
        var aboveGround = new IfcPropertySingleValue(db, "AboveGround",
            new IfcLogical(unknown));
        var numberOfStoreys = new IfcPropertySingleValue(db, "NumberOfStoreys",
            new IfcInteger(0));
        var isLandmarked = new IfcPropertySingleValue(db, "IsLandmarked",
            new IfcLogical(unknown));
        var isExternal = new IfcPropertySingleValue(db, "IsExternal",
            new IfcBoolean(false));

        // ggifc's IfcPropertySet(name, props[]) constructor populates HasProperties for us
        // (the dictionary is keyed by property name, so we can't just .Add(prop)).
        var psetSite = new IfcPropertySet("Pset_SiteCommon",
            new IfcProperty[] { refProjInfo });
        _ = new IfcRelDefinesByProperties(site, psetSite);

        foreach (var storey in storeys)
        {
            var psetStorey = new IfcPropertySet("Pset_BuildingStoreyCommon",
                new IfcProperty[] { refLevelDatum, aboveGround });
            _ = new IfcRelDefinesByProperties(storey, psetStorey);
        }

        // Four Psets on the Building (Revit's default export decoration).
        var psetBuildingCommon = new IfcPropertySet("Pset_BuildingCommon",
            new IfcProperty[] { refProjInfo, numberOfStoreys, isLandmarked });
        _ = new IfcRelDefinesByProperties(building, psetBuildingCommon);

        var psetBuildingElementProxy = new IfcPropertySet("Pset_BuildingElementProxyCommon",
            new IfcProperty[] { refProjInfo, isExternal });
        _ = new IfcRelDefinesByProperties(building, psetBuildingElementProxy);

        var psetBuildingStoreyTemplate = new IfcPropertySet("Pset_BuildingStoreyCommon",
            new IfcProperty[] { refProjInfo, aboveGround });
        _ = new IfcRelDefinesByProperties(building, psetBuildingStoreyTemplate);

        var psetBuildingSystem = new IfcPropertySet("Pset_BuildingSystemCommon",
            new IfcProperty[] { refProjInfo });
        _ = new IfcRelDefinesByProperties(building, psetBuildingSystem);
    }
}
