using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// StepId watermark over a ggifc <see cref="DatabaseIfc"/>: ggifc allocates StepIds
/// monotonically, so the entities created between two <see cref="Current"/> reads are
/// exactly those with StepId in (before, after]. Used to capture per-element entity
/// ownership around converter calls (see <c>ElementConverterRegistry.ConvertOne</c>).
/// ggifc's <c>NextObjectRecord</c> counter has no public getter, so this scans the
/// database — O(entities), negligible at this project's model sizes.
/// </summary>
public static class StepIdWatermark
{
    /// <summary>The highest StepId currently allocated in <paramref name="db"/> (0 if empty).</summary>
    public static int Current(DatabaseIfc db)
    {
        var max = 0;
        foreach (var entity in db)
        {
            if (entity is not null && entity.StepId > max)
                max = entity.StepId;
        }
        return max;
    }
}
