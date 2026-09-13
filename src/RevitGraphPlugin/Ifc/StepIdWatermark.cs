using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// ggifc allocates StepIds monotonically, so the entities created between two
/// <see cref="Current"/> reads are exactly those in (before, after]. Scans the database
/// (ggifc exposes no counter).
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
