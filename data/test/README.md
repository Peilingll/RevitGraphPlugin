# Test artifacts

Working directory for the comparison tools in `tools/python/`. Unlike `data/samples/`,
which holds *inputs* (the Revit-exported IFC and the ConMan2 baseline graphs), this
directory holds *outputs* — files the plugin or the tools regenerate on demand.

## What is committed

Only artifacts that act as a regression reference:

| File | Role |
| ---- | ---- |
| `00_empty_ggifc.ifc` | Plugin's ggifc export of the empty project — the default candidate for `compare_ifc.py` |
| `roundtrip/roundtrip_plugin-bridge.ifc` | Bridge-mode round-trip baseline (`ifc_roundtrip_check.py`) |
| `roundtrip/roundtrip_plugin-direct.ifc` | Direct-mode round-trip baseline |

## What is not committed

Everything else produced here — `compare_*.txt` reports, Neo4j query exports, Neo4j
Browser screenshots, diagnostic dumps. They are reproducible by re-running the tools,
and a directory of dated near-duplicates (`Test3_…`, `Test5_…`, `Test7_…`) makes it
unclear which one is authoritative.

When a comparison run settles a question, write the conclusion into `doc/log/` and let
the raw output go. The empty-model alignment, for example, is recorded in
`doc/log/2026-06-02_empty-model-alignment.md` — the JSON exports behind it are not kept.
