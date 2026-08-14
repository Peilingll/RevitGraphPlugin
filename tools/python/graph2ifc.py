#!/usr/bin/env python3
"""Reconstruct an IFC file from a Neo4j graph via ConMan2's graph_2_ifc.

The reverse half of the round-trip: read all nodes/edges under one `timestamp` from
Neo4j and rebuild them into an IFC (Part 21) file with ConMan2's IfcGraphInterface,
then (optionally) validate it with ifcopenshell.

Usage
-----
    <conman2-venv-python> graph2ifc.py [TIMESTAMP] [OUTPUT.ifc] [--no-validate]

Defaults: TIMESTAMP=plugin-live, OUTPUT=roundtrip_<timestamp>.ifc (current dir).

Connection uses the NEO4J_LOCAL_* env vars (same convention as the plugin):
    NEO4J_LOCAL_USERNAME (neo4j) / NEO4J_LOCAL_PASSWORD (password) /
    NEO4J_LOCAL_HOSTNAME (localhost) / NEO4J_LOCAL_PORT (7687)
Point CONMAN2_PATH at ConMan2/src if it is not cloned as a sibling of this repo
(same convention as snippet_to_cypher.py).
"""
import argparse
import os
import sys
from pathlib import Path

# Sibling-clone default: <parent>/RevitGraphPlugin/tools/python/graph2ifc.py
#                        <parent>/ConMan2/src
_SIBLING = Path(__file__).resolve().parents[3] / "ConMan2" / "src"
CONMAN2 = Path(os.environ.get("CONMAN2_PATH", _SIBLING))
if not CONMAN2.is_dir():
    sys.exit(f"[graph2ifc] ConMan2 source not found at: {CONMAN2}\n"
             f"  Clone ConMan2 as a sibling of this repo, or set CONMAN2_PATH to its src/.")
sys.path.insert(0, str(CONMAN2))

from neo4j_core.neo4j_connection import Neo4jConnection
from ifc_graph_interface.IfcGraphInterface import IfcGraphInterface


def main():
    ap = argparse.ArgumentParser(prog="graph2ifc")
    ap.add_argument("timestamp", nargs="?", default="plugin-live",
                    help="graph version to reconstruct (default: plugin-live)")
    ap.add_argument("output", nargs="?", default=None,
                    help="target .ifc path (default: roundtrip_<timestamp>.ifc)")
    ap.add_argument("--no-validate", action="store_true",
                    help="skip the ifcopenshell validation pass")
    args = ap.parse_args()

    out = Path(args.output) if args.output else Path(f"roundtrip_{args.timestamp}.ifc")
    out = out.resolve()

    # Configure the (global) neomodel connection IfcGraphInterface reads from.
    Neo4jConnection(
        username=os.environ.get("NEO4J_LOCAL_USERNAME", "neo4j"),
        password=os.environ.get("NEO4J_LOCAL_PASSWORD", "password"),
        hostname=os.environ.get("NEO4J_LOCAL_HOSTNAME", "localhost"),
        port=int(os.environ.get("NEO4J_LOCAL_PORT", "7687")),
    )

    IfcGraphInterface().graph_2_ifc(str(out), timestamp=args.timestamp)
    print(f"Wrote {out} ({out.stat().st_size} bytes)")

    if args.no_validate:
        return 0

    import ifcopenshell
    import ifcopenshell.validate
    model = ifcopenshell.open(str(out))
    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(model, logger)
    issues = logger.statements
    print(f"validate: {len(issues)} issue(s)")
    for e in issues[:20]:
        print("  -", str(e.get("message"))[:160])
    return 1 if issues else 0


if __name__ == "__main__":
    sys.exit(main())
