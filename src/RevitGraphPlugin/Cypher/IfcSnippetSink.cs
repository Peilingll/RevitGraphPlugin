using System.Diagnostics;
using System.Text;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Bridge between the C# plugin and the Python <c>snippet_to_cypher.py</c> script.
///
/// Workflow:
/// <list type="number">
/// <item>Serialise the supplied <see cref="DatabaseIfc"/> to a temp .ifc file</item>
/// <item>Spawn python.exe with the script + temp path + action + timestamp</item>
/// <item>Capture stdout/stderr; raise on non-zero exit</item>
/// <item>Leave the temp .ifc on disk after success so it can be inspected</item>
/// </list>
///
/// Paths are overridable via environment variables:
/// <list type="bullet">
/// <item><c>PLUGIN_PYTHON</c> — defaults to <c>D:\Hiwi\ConMan2\venv\Scripts\python.exe</c></item>
/// <item><c>PLUGIN_SNIPPET_SCRIPT</c> — defaults to the repo path of snippet_to_cypher.py</item>
/// </list>
///
/// Neo4j credentials (<c>NEO4J_LOCAL_PASSWORD</c> etc.) are inherited via the
/// process env block — the user must set them before launching Revit.
///
/// See <c>doc_process/2026-05-29-architecture-revisit-ifc-snippets.md</c>.
/// </summary>
public static class IfcSnippetSink
{
    private const string DefaultPython = @"D:\Hiwi\ConMan2\venv\Scripts\python.exe";
    private const string DefaultScript = @"D:\Hiwi\RevitGraphPlugin\tools\python\snippet_to_cypher.py";
    private const string TempIfcName   = "RevitGraphPlugin_last_sync.ifc";
    private const int    TimeoutMs     = 60_000;

    public sealed record SinkResult(
        string TempIfcPath,
        string Stdout,
        string Stderr,
        int ExitCode);

    public static SinkResult Run(DatabaseIfc db, string action = "CREATE", string timestamp = "plugin-1")
    {
        var pythonExe  = Environment.GetEnvironmentVariable("PLUGIN_PYTHON")          ?? DefaultPython;
        var scriptPath = Environment.GetEnvironmentVariable("PLUGIN_SNIPPET_SCRIPT")  ?? DefaultScript;

        if (!File.Exists(pythonExe))
            throw new InvalidOperationException(
                $"Python interpreter not found at: {pythonExe}. " +
                $"Set the PLUGIN_PYTHON env var to override.");
        if (!File.Exists(scriptPath))
            throw new InvalidOperationException(
                $"snippet_to_cypher.py not found at: {scriptPath}. " +
                $"Set the PLUGIN_SNIPPET_SCRIPT env var to override.");

        var tempIfc = Path.Combine(Path.GetTempPath(), TempIfcName);
        db.WriteFile(tempIfc);

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(tempIfc);
        psi.ArgumentList.Add("--action");
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add("--timestamp");
        psi.ArgumentList.Add(timestamp);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for python.exe");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(TimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* swallow */ }
            throw new TimeoutException(
                $"snippet_to_cypher.py timed out after {TimeoutMs} ms. " +
                $"Temp IFC kept at: {tempIfc}\nstderr captured so far:\n{stderr}");
        }

        var result = new SinkResult(tempIfc, stdout.ToString(), stderr.ToString(), proc.ExitCode);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"snippet_to_cypher.py exited with code {proc.ExitCode}.\n" +
                $"Temp IFC kept at: {tempIfc}\n" +
                $"--- stdout ---\n{result.Stdout}\n" +
                $"--- stderr ---\n{result.Stderr}");

        return result;
    }
}
