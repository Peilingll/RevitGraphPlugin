using System.IO;

namespace RevitGraphPlugin;

/// <summary>
/// Append-only diagnostic log for the live sync path, written to a stable file so the
/// DocumentChanged flow can be traced without showing UI from an event context (Revit
/// forbids TaskDialog inside DocumentChanged — a swallowed exception there looks exactly
/// like "nothing happened"). Path: %TEMP%\RevitGraphPlugin\live.log.
/// </summary>
internal static class LiveSyncLog
{
    public static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RevitGraphPlugin", "live.log");

    public static void Write(string message)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break the sync */ }
    }
}
