using System.Diagnostics;
using System.Reflection;
using System.Text;
using GeometryGym.Ifc;

// ── Pipeline: TEMP-IFC BRIDGE (ggifc tree → .ifc → ConMan2 ifc_2_graph) ──
// Reference path behind the "Sync (bridge)" button; Live Sync and "Sync (direct)"
// use the direct-write pipeline (Cypher/Direct/) instead.
namespace RevitGraphPlugin.Cypher;

/// <summary>
/// Bridge to the Python <c>snippet_to_cypher.py</c> script: write the ggifc tree to a
/// temp .ifc, run the script, capture its output; the temp file is kept for inspection.
/// Paths assume ConMan2 is cloned beside this repo; override with <c>PLUGIN_PYTHON</c> /
/// <c>PLUGIN_SNIPPET_SCRIPT</c>. Neo4j credentials (<c>NEO4J_LOCAL_*</c>) are inherited
/// from the Revit process environment.
/// </summary>
public static class IfcSnippetSink
{
    private const string TempIfcName = "RevitGraphPlugin_last_sync.ifc";
    private const int    TimeoutMs   = 60_000;

    /// <summary>Repo root: the DLL builds to <c>&lt;repo&gt;/src/RevitGraphPlugin/bin/&lt;Config&gt;/</c>, four levels down.</summary>
    private static string RepoRoot
    {
        get
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", ".."));
        }
    }

    /// <summary>Bridge script inside this repo (sibling-clone default).</summary>
    private static string DefaultScript =>
        Path.Combine(RepoRoot, "tools", "python", "snippet_to_cypher.py");

    /// <summary>ConMan2's venv interpreter, assuming ConMan2 is cloned beside this repo.</summary>
    private static string DefaultPython =>
        Path.Combine(RepoRoot, "..", "ConMan2", "venv", "Scripts", "python.exe");

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
                $"Expected ConMan2 cloned beside this repo; set PLUGIN_PYTHON to override.");
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
