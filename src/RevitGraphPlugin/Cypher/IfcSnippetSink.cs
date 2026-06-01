using System.Diagnostics;
using System.Reflection;
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
/// Paths default to a "sibling clone" layout (ConMan2 cloned next to this repo)
/// derived relative to the built DLL, and are overridable via environment variables:
/// <list type="bullet">
/// <item><c>PLUGIN_SNIPPET_SCRIPT</c> — defaults to <c>&lt;repo&gt;/tools/python/snippet_to_cypher.py</c></item>
/// <item><c>PLUGIN_PYTHON</c> — defaults to <c>&lt;repo&gt;/../ConMan2/venv/Scripts/python.exe</c></item>
/// </list>
///
/// Neo4j credentials (<c>NEO4J_LOCAL_PASSWORD</c> etc.) are inherited via the
/// process env block — the user must set them before launching Revit.
///
/// See <c>doc_process/2026-05-29-architecture-revisit-ifc-snippets.md</c>.
/// </summary>
public static class IfcSnippetSink
{
    private const string TempIfcName = "RevitGraphPlugin_last_sync.ifc";
    private const int    TimeoutMs   = 60_000;

    /// <summary>
    /// Repo root, derived from the executing assembly. The DLL builds to
    /// <c>&lt;repo&gt;/src/RevitGraphPlugin/bin/&lt;Config&gt;/</c> (the csproj sets
    /// AppendTargetFrameworkToOutputPath=false, so there is no extra TFM folder),
    /// i.e. four levels above the assembly.
    /// </summary>
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
