using System.Reflection;
using System.Text;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// TEMPORARY — Fix B diagnostic.
/// Captures OwnerHistory chain state at multiple points inside
/// <see cref="BoilerplateBuilder.Build"/> so we can see why the user
/// organisation's <c>Name</c> still serialises as <c>UNKNOWN</c> in Revit
/// runtime despite <see cref="RevitOwnerHistory"/> writing null via the
/// backing field (xUnit passes; only Revit fails).
///
/// Remove this file and every call site once Fix B is resolved.
/// </summary>
internal static class OwnerHistoryDiagnostic
{
    private const string LogPath = @"D:\Hiwi\RevitGraphPlugin\data\test\owner_history_diag.txt";

    public static void Reset()
    {
        try { if (File.Exists(LogPath)) File.Delete(LogPath); }
        catch { /* diagnostic — never block the sync */ }
    }

    public static void Capture(string label, IfcProject project)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {label} @ {DateTime.Now:HH:mm:ss.fff} ===");

            var oh = project.OwnerHistory;
            if (oh is null)
            {
                sb.AppendLine("OwnerHistory is null");
                File.AppendAllText(LogPath, sb.ToString());
                return;
            }

            sb.AppendLine($"OwnerHistory step={oh.StepId} hash={oh.GetHashCode()}");

            DumpEntity(sb, "userOrg",       oh.OwningUser?.TheOrganization);
            DumpEntity(sb, "developerOrg",  oh.OwningApplication?.ApplicationDeveloper);
            DumpEntity(sb, "person",        oh.OwningUser?.ThePerson);
            DumpEntity(sb, "application",   oh.OwningApplication);

            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, $"[diag error] {ex.Message}\n"); }
            catch { /* swallow */ }
        }
    }

    private static void DumpEntity(StringBuilder sb, string label, object? entity)
    {
        if (entity is null)
        {
            sb.AppendLine($"  {label}: null");
            return;
        }

        var stepId = (entity as BaseClassIfc)?.StepId;
        sb.AppendLine($"  {label} {entity.GetType().Name} step={stepId} hash={entity.GetHashCode()}");

        Type? t = entity.GetType();
        while (t is not null && t.Name != "Object")
        {
            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType != typeof(string)) continue;
                var v = f.GetValue(entity);
                sb.AppendLine($"    [{t.Name}] {f.Name} = {(v is null ? "null" : $"'{v}'")}");
            }
            t = t.BaseType;
        }
    }
}
