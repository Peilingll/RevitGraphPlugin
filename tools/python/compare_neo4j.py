#!/usr/bin/env python3
"""Semantic diff of two Neo4j query-table-data JSON exports, ignoring identity noise.

Compares the plugin's graph (ggifc IFC -> ConMan2 -> Neo4j) against the ConMan2
baseline graph (native IFC -> ConMan2 -> Neo4j). Because ConMan2's importer is
shared on both sides, this is the end-to-end mirror of compare_ifc.py: the
differences seen here should match the IFC-level ones -- minus orphan entities,
which a `MATCH (n)-[r]->(m)` export query drops because they have no relationship.

Input shape: a JSON array of rows, each `{"n": <node>, "r": <rel>, "m": <node>}`,
as produced by the Neo4j Browser "Export table data" on that query.

What it compares
----------------
* Nodes (deduplicated by Neo4j identity), grouped by EntityType: labels + every
  property, with identity noise masked (p21_id / timestamp / GlobalId, plus
  IfcOwnerHistory timestamps). Reference-typed values stay as-is because the
  export already flattens them to strings.
* Relationships (deduplicated by identity): the triple
  (startEntityType, rel_type, endEntityType, list_index), so Neo4j's internal
  numeric ids never leak into the comparison.

Usage
-----
    <conman2-venv-python> compare_neo4j.py [REF.json] [CAND.json] [--out [PATH]]

Defaults:
    REF  = data/samples/cypher/00_empty_neo4j_query_table_data.json   (ConMan2 baseline)
    CAND = data/test/query_test/Test7_empty_Plugin_00_empty_neo4j_query_table_data_2026-6-2.json
"""
import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_REF = REPO / "data" / "samples" / "cypher" / "00_empty_neo4j_query_table_data.json"
DEFAULT_CAND = (REPO / "data" / "test" / "query_test"
                / "Test7_empty_Plugin_00_empty_neo4j_query_table_data_2026-6-2.json")

# Node property keys masked on every node (identity / bookkeeping).
IGNORE_PROPS = {"p21_id", "timestamp", "GlobalId"}
# Per-EntityType extra masks (volatile values that are not content).
IGNORE_PROPS_BY_TYPE = {
    "IfcOwnerHistory": {"CreationDate", "LastModifiedDate"},
}


def entity_type(node):
    return node.get("properties", {}).get("EntityType", "<no-EntityType>")


def node_signature(node):
    """(EntityType, (labels_tuple, ((prop, repr(value)), ...)))  with noise masked."""
    props = node.get("properties", {})
    etype = props.get("EntityType", "<no-EntityType>")
    labels = tuple(sorted(node.get("labels", [])))
    type_mask = IGNORE_PROPS_BY_TYPE.get(etype, set())
    fields = tuple(sorted(
        (k, repr(v)) for k, v in props.items()
        if k != "EntityType" and k not in IGNORE_PROPS and k not in type_mask
    ))
    return etype, (labels, fields)


def load(path):
    """Return (node_sigs_by_type, rel_sigs, n_nodes, n_rels)."""
    rows = json.loads(Path(path).read_text(encoding="utf-8"))
    nodes = {}          # identity -> node dict (dedup)
    rels = {}           # rel identity -> rel dict (dedup)
    id_to_type = {}     # node identity -> EntityType
    for row in rows:
        for key in ("n", "m"):
            nd = row.get(key)
            if nd is not None and "identity" in nd:
                nodes[nd["identity"]] = nd
                id_to_type[nd["identity"]] = entity_type(nd)
        r = row.get("r")
        if r is not None and "identity" in r:
            rels[r["identity"]] = r

    node_sigs = defaultdict(Counter)
    for nd in nodes.values():
        etype, sig = node_signature(nd)
        node_sigs[etype][sig] += 1

    rel_sigs = Counter()
    for r in rels.values():
        s = id_to_type.get(r.get("start"), "?")
        e = id_to_type.get(r.get("end"), "?")
        rp = r.get("properties", {})
        rel_sigs[(s, rp.get("rel_type"), e, rp.get("list_index"))] += 1

    return node_sigs, rel_sigs, len(nodes), len(rels)


def fmt_node(sig):
    labels, fields = sig
    lines = [f"        labels = {list(labels)}"]
    lines += [f"        {k} = {v}" for k, v in fields]
    return "\n".join(lines)


def build_report(ref_path, cand_path):
    ref_nodes, ref_rels, rn_nodes, rn_rels = load(ref_path)
    cand_nodes, cand_rels, cn_nodes, cn_rels = load(cand_path)

    out = [
        f"REF  (ConMan2 baseline) : {ref_path}",
        f"CAND (plugin)           : {cand_path}",
        "=" * 72,
        f"nodes: REF={rn_nodes} CAND={cn_nodes}   relationships: REF={rn_rels} CAND={cn_rels}",
        "=" * 72,
        "",
        "## NODES (by EntityType)",
    ]

    n_diff = 0
    all_types = sorted(set(ref_nodes) | set(cand_nodes))
    node_diff_lines = []
    n_ok = 0
    for typ in all_types:
        ra = ref_nodes.get(typ, Counter())
        ca = cand_nodes.get(typ, Counter())
        rc, cc = sum(ra.values()), sum(ca.values())
        only_ref = ra - ca
        only_cand = ca - ra
        if rc == cc and not only_ref and not only_cand:
            n_ok += 1
            continue
        n_diff += 1
        node_diff_lines.append(f"\nDIFF  {typ}:  REF={rc}  CAND={cc}")
        for sig, k in only_ref.items():
            node_diff_lines.append(f"   - REF  only ({k}x):\n{fmt_node(sig)}")
        for sig, k in only_cand.items():
            node_diff_lines.append(f"   + CAND only ({k}x):\n{fmt_node(sig)}")
    out.append(f"node types OK (identical): {n_ok}")
    out.append(f"node types with diffs    : {n_diff}")
    out += node_diff_lines

    # Relationships
    out += ["", "## RELATIONSHIPS  (startType, rel_type, endType, list_index)"]
    only_ref_r = ref_rels - cand_rels
    only_cand_r = cand_rels - ref_rels
    if not only_ref_r and not only_cand_r:
        out.append("  all relationship triples match.")
    else:
        for trip, k in sorted(only_ref_r.items()):
            out.append(f"   - REF  only ({k}x): {trip}")
        for trip, k in sorted(only_cand_r.items()):
            out.append(f"   + CAND only ({k}x): {trip}")
    rel_diff = len(only_ref_r) + len(only_cand_r)

    out += ["", "=" * 72,
            "Masked by design: Neo4j identity/elementId, p21_id, timestamp, GlobalId,",
            "IfcOwnerHistory timestamps. Orphan nodes are absent from an (n)-[r]->(m) export."]
    return "\n".join(out), n_diff + rel_diff


def main():
    parser = argparse.ArgumentParser(
        prog="compare_neo4j",
        description="Semantic diff of two Neo4j query-table-data JSON exports.",
    )
    parser.add_argument("ref", nargs="?", default=str(DEFAULT_REF),
                        help="reference JSON (default: ConMan2 baseline 00_empty)")
    parser.add_argument("cand", nargs="?", default=str(DEFAULT_CAND),
                        help="candidate JSON (default: Test7 plugin export)")
    parser.add_argument("--out", nargs="?", const="<auto>", default=None,
                        help="also write the report to a file; bare --out auto-names "
                             "it data/test/compare_neo4j_<cand-stem>.txt")
    args = parser.parse_args()

    ref_path = Path(args.ref)
    cand_path = Path(args.cand)
    for p in (ref_path, cand_path):
        if not p.is_file():
            sys.exit(f"[compare_neo4j] file not found: {p}")

    report, n_diff = build_report(ref_path, cand_path)
    print(report)

    if args.out is not None:
        out_path = (REPO / "data" / "test" / f"compare_neo4j_{cand_path.stem}.txt"
                    if args.out == "<auto>" else Path(args.out))
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(report, encoding="utf-8")
        print(f"\n[compare_neo4j] report written to: {out_path}")

    return 1 if n_diff else 0


if __name__ == "__main__":
    sys.exit(main())
