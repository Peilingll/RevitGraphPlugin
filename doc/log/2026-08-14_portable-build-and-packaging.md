# Portable build and packaging — Revit 2026 on other machines

**Date:** 2026-08-14
**Commits:** `4083aa9` (RevitVersion parameterization), `d5158df` (sibling-clone paths),
`849d6e6` (NuGet API fallback), `272448b` (package.ps1 + install.ps1), `efefc19` (README).
**Goal:** the repo assumed one machine — Revit 2025 at `D:\Autodesk\Revit 2025\`, ConMan2
at `D:\Hiwi\ConMan2` — so a clone on a Revit-2026 machine failed at every stage: the build
could not find `RevitAPI.dll`, a successful build deployed into `Addins\2025\` where
Revit 2026 never looks, and `checkout.ps1` invoked an absolute-path venv python. Make the
repo build anywhere, and make testing not require building at all.

## What shipped

- **`RevitVersion` MSBuild property** (default `2025`) drives both the API lookup and the
  deploy folder (`Addins\$(RevitVersion)\`). Install path resolution, first match wins:
  `RevitInstallPath` override → legacy `RevitInstallPath2025` → `D:\Autodesk\Revit
  <version>\` → `%ProgramFiles%\Autodesk\Revit <version>\`. Same chain in the tests csproj.
- **NuGet fallback**: when no local install matches, the csproj swaps the `<Reference>`
  items for `Nice3point.Revit.Api.RevitAPI(+UI)` `$(RevitVersion).*` packages — verbatim
  copies of the API assemblies, `ExcludeAssets="runtime"` so nothing ships. This is what
  lets a 2025-only machine compile a 2026 package: `-p:RevitVersion=2026` resolved
  Nice3point 2026.4.10 and built with **0 errors** — the compile-level proof that no API
  this plugin uses broke between 2025 and 2026.
- **Sibling-clone defaults everywhere**: `checkout.ps1` now resolves python via
  `PLUGIN_PYTHON` → `<repo>\..\ConMan2\venv\Scripts\python.exe` (the C# bridge's existing
  convention); `graph2ifc.py`'s `CONMAN2_PATH` default went from the absolute
  `D:/Hiwi/ConMan2/src` to the same sibling derivation `snippet_to_cypher.py` already used.
- **`package.ps1` → `dist\RevitGraphPlugin-<version>.zip`**: Release build + dependency
  DLLs + `.addin` + `install.ps1` + `INSTALL.md` + `revit-version.txt`. `install.ps1`
  copies into the per-user Addins folder (no admin); `INSTALL.md` documents the only
  external requirement — a local Neo4j at `bolt://127.0.0.1:7687` with the plugin's
  default credentials or `NEO4J_LOCAL_PASSWORD`. No Docker, no Python, no SDK on the
  target machine.

## Decisions

- **Zip over repo link** for handing to the professor: a clone is a source distribution
  and re-imports every machine assumption; the zip is the finished dish.
- **No Docker**: the add-in lives inside Revit's process (never containerizable), and the
  only service dependency is "a reachable local Neo4j", which the target machine already
  has. A compose file would add a Docker Desktop requirement for nothing.
- **Compile ≠ runtime proof**: the 2026 build passing shows no removed/renamed API, but
  behavioral differences only surface in Revit 2026 itself. Acceptance = run the packaged
  zip on the 2026 machine once (install → Live Sync → counts track the model).

## Gotcha caught on the way

`dotnet build` of the csproj alone outputs to `bin\Release\`, while the solution build
maps to `bin\x64\Release\` — the first `package.ps1` draft collected from the latter and
would happily zip a stale build. Fixed by pinning `-p:Platform=x64` in the script; a
clean-output repackage verified the zip contents come from the fresh build.

## Open

- Runtime validation on the Revit 2026 machine (manual, user-side).
- The bridge button's `RepoRoot` derivation (4 levels above the deployed DLL) is broken
  for any deployed copy — irrelevant for the professor package (Live Sync only), noted
  for a future cleanup.
