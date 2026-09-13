using System.IO;

namespace RevitGraphPlugin;

/// <summary>
/// Append-only diagnostic log at %TEMP%\RevitGraphPlugin\live.log. Revit forbids UI
/// inside DocumentChanged, so this is the only trace of the live path.
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
