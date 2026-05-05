using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Conversion;

/// <summary>
/// Walks the active Revit <see cref="Document"/> and produces a populated
/// <see cref="DatabaseIfc"/>: spatial hierarchy (Project → Site → Building →
/// Storey-per-Level), then dispatches each <see cref="Wall"/> to
/// <see cref="WallConverter"/> and each window <see cref="FamilyInstance"/>
/// to <see cref="WindowConverter"/>.
/// </summary>
internal sealed class RevitToIfcExporter
{
    public sealed record Result(DatabaseIfc Database, int WallCount, int WindowCount);

    /// <summary>
    /// Single-element export for Stage 4 incremental sync. The IFC entity
    /// tree contains the spatial hierarchy plus exactly one wall, or one
    /// wall + one window. Guarantees the same construction order as
    /// <see cref="Export(Document)"/> so MERGE-on-(p21_id, timestamp) hits
    /// the same node identity for the same Revit element across events.
    /// </summary>
    public Result ExportElement(Document doc, Element element)
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4X3_RC4);
        _ = new IfcProject(db, doc.ProjectInformation?.Number ?? "Project");
        var site = new IfcSite(db, doc.ProjectInformation?.Name ?? "Site");
        var building = new IfcBuilding(site, "Building");

        var storeys = new Dictionary<ElementId, IfcBuildingStorey>();
        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            storeys[level.Id] = new IfcBuildingStorey(
                building, level.Name ?? "Level",
                level.Elevation * GeometryHelpers.FeetToMm);
        }

        var wallCount = 0;
        var windowCount = 0;

        switch (element)
        {
            case Wall wall:
                if (storeys.TryGetValue(wall.LevelId, out var storey)
                    && WallConverter.Convert(wall, db, storey) is not null)
                    wallCount = 1;
                break;

            case FamilyInstance window when window.Category?.Id.Value == (long)BuiltInCategory.OST_Windows:
                if (window.Host is not Wall hostWall) break;
                if (!storeys.TryGetValue(hostWall.LevelId, out var hostStorey)) break;
                var hostIfcWall = WallConverter.Convert(hostWall, db, hostStorey);
                if (hostIfcWall is null) break;
                if (WindowConverter.Convert(window, db, hostIfcWall) is not null)
                {
                    wallCount = 1; // host re-synced as a side effect
                    windowCount = 1;
                }
                break;
        }

        return new Result(db, wallCount, windowCount);
    }

    public Result Export(Document doc)
    {
        var db = new DatabaseIfc(false, ReleaseVersion.IFC4X3_RC4);
        _ = new IfcProject(db, doc.ProjectInformation?.Number ?? "Project");
        var site = new IfcSite(db, doc.ProjectInformation?.Name ?? "Site");
        var building = new IfcBuilding(site, "Building");

        // One IfcBuildingStorey per Revit Level.
        var storeys = new Dictionary<ElementId, IfcBuildingStorey>();
        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            storeys[level.Id] = new IfcBuildingStorey(
                building,
                level.Name ?? "Level",
                level.Elevation * GeometryHelpers.FeetToMm);
        }

        // First pass: walls (each window needs its host wall already converted).
        var ifcWallByElementId = new Dictionary<ElementId, IfcWall>();
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
        var wallCount = 0;
        foreach (var wall in walls)
        {
            if (!storeys.TryGetValue(wall.LevelId, out var storey)) continue;
            var ifcWall = WallConverter.Convert(wall, db, storey);
            if (ifcWall is null) continue;
            ifcWallByElementId[wall.Id] = ifcWall;
            wallCount++;
        }

        // Second pass: windows hosted on already-converted walls.
        var windows = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Windows)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();
        var windowCount = 0;
        foreach (var window in windows)
        {
            if (window.Host is not Wall hostWall) continue;
            if (!ifcWallByElementId.TryGetValue(hostWall.Id, out var hostIfcWall)) continue;
            if (WindowConverter.Convert(window, db, hostIfcWall) is null) continue;
            windowCount++;
        }

        return new Result(db, wallCount, windowCount);
    }
}
