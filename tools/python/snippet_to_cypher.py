"""
RevitGraphPlugin — Python bridge.

Reads an IFC STEP file (a "snippet" produced by the C# plugin via ggifc),
parses it with ifcopenshell, and writes the corresponding nodes / edges
to Neo4j following ConMan2's schema rules.

This is the Python half of the hybrid C# / Python architecture decided
2026-05-29 (see doc_process/2026-05-29-architecture-revisit-ifc-snippets.md).

Invocation (typically from the C# plugin via Process.Start):
    python snippet_to_cypher.py <ifc_path> --action CREATE --timestamp plugin-1

Environment variables:
    CONMAN2_PATH         — absolute path to ConMan2 src/ directory
                            (default: D:\\Hiwi\\ConMan2\\src)
    NEO4J_LOCAL_USERNAME — overrides --neo4j-user (default: neo4j)
    NEO4J_LOCAL_PASSWORD — overrides --neo4j-password
    NEO4J_LOCAL_HOSTNAME — overrides --neo4j-host (default: localhost)
    NEO4J_LOCAL_PORT     — overrides --neo4j-port (default: 7687)

A .env file in CWD / parents is also picked up (handled by ConMan2's
Neo4jConnection).

Action dispatch:
    CREATE — implemented (delegates to ConMan2's IfcGraphInterface.ifc_2_graph)
    DELETE — NotImplementedError; Step 3+ work
    UPDATE — NotImplementedError; Step 3+ work
"""

import argparse
import os
import sys
from pathlib import Path


def _default_conman2_src() -> Path:
    """Derive ConMan2's src/ from this script's location, assuming ConMan2 is
    cloned as a sibling of the RevitGraphPlugin repo:

        <parent>/
        ├── RevitGraphPlugin/tools/python/snippet_to_cypher.py   <- this file
        └── ConMan2/src                                          <- target

    This file lives at <repo>/tools/python/, so the repo root is two parents up
    and the shared parent folder is one more; ConMan2/src sits beside the repo.
    """
    repo_root = Path(__file__).resolve().parents[2]   # tools/python -> repo root
    return repo_root.parent / "ConMan2" / "src"


def _bootstrap_conman2_path() -> Path:
    """Insert ConMan2's src/ onto sys.path so we can import its modules.

    Resolution order:
      1. CONMAN2_PATH env var (override, for a clone placed elsewhere)
      2. sibling-clone default (ConMan2 next to this repo)
    """
    raw = os.environ.get("CONMAN2_PATH")
    conman2_src = Path(raw) if raw else _default_conman2_src()
    if not conman2_src.is_dir():
        sys.exit(
            f"[snippet_to_cypher] ConMan2 source not found at: {conman2_src}\n"
            f"  Clone ConMan2 (https://github.com/seb-esser/ConMan2) as a sibling of\n"
            f"  this repo, or set the CONMAN2_PATH env var to its src/ directory."
        )
    if str(conman2_src) not in sys.path:
        sys.path.insert(0, str(conman2_src))
    return conman2_src


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="snippet_to_cypher",
        description="Write an IFC snippet to Neo4j following ConMan2 schema",
    )
    parser.add_argument(
        "ifc_path",
        help="Absolute path to the IFC STEP file (snippet) to process",
    )
    parser.add_argument(
        "--action",
        choices=["CREATE", "DELETE", "UPDATE"],
        default="CREATE",
        help="Graph transformation: CREATE adds the snippet's entities, "
             "DELETE/UPDATE reserved for Step 3+ incremental sync",
    )
    parser.add_argument(
        "--timestamp",
        default="plugin-1",
        help="Version label written to every node's `timestamp` property "
             "(baseline uses '1'; plugin output should use something distinct "
             "for side-by-side comparison, e.g. 'plugin-1')",
    )
    parser.add_argument("--neo4j-user", default="neo4j")
    parser.add_argument("--neo4j-password", default="password")
    parser.add_argument("--neo4j-host", default="localhost")
    parser.add_argument("--neo4j-port", type=int, default=7687)
    return parser.parse_args()


def _run_create(ifc_path: Path, timestamp: str) -> None:
    """Delegate to ConMan2's batch importer. Empty boilerplate is effectively
    'create everything', which matches ifc_2_graph's CREATE-only behaviour."""
    from ifc_graph_interface.IfcGraphInterface import IfcGraphInterface
    interface = IfcGraphInterface()
    interface.ifc_2_graph(str(ifc_path), timestamp)


def main() -> int:
    args = _parse_args()

    ifc_path = Path(args.ifc_path)
    if not ifc_path.is_file():
        sys.exit(f"[snippet_to_cypher] IFC file not found: {ifc_path}")

    conman2_src = _bootstrap_conman2_path()
    print(f"[snippet_to_cypher] ConMan2 src: {conman2_src}")
    print(f"[snippet_to_cypher] IFC snippet: {ifc_path}")
    print(f"[snippet_to_cypher] Action: {args.action}, timestamp: {args.timestamp}")

    # Establishes the neomodel global db connection. ConMan2's Neo4jConnection
    # itself prefers .env / env vars over the constructor args, so the args
    # below act as a final fallback.
    from neo4j_core.neo4j_connection import Neo4jConnection
    Neo4jConnection(
        username=args.neo4j_user,
        password=args.neo4j_password,
        hostname=args.neo4j_host,
        port=args.neo4j_port,
    )

    if args.action == "CREATE":
        _run_create(ifc_path, args.timestamp)
    else:
        raise NotImplementedError(
            f"Action {args.action!r} is reserved for Step 3+ incremental sync. "
            f"See doc_process/2026-05-29-architecture-revisit-ifc-snippets.md."
        )

    print(f"[snippet_to_cypher] done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
