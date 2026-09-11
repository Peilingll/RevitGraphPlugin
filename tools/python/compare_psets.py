#!/usr/bin/env python
"""Compare Pset_*Common values between a native Revit IFC export and the live graph.

Usage:
    python compare_psets.py <native.ifc> [--timestamp plugin-live] [--props IsExternal,LoadBearing]

For every product that carries a Pset_*Common in either source, prints the property
values side by side — plus, with --storeys, the storey each product is contained in — keyed by the Revit element id (the IFC Tag on the native side,
revit_element_id on the graph side) and the IFC entity type. Exit code 1 when any
value differs or exists on one side only — so it doubles as a check in scripts.

This is the acceptance tool for the converter TODO(verify) items (open-questions §3):
model every element type once, export natively, Live Sync the same model, run this.

Environment: NEO4J_LOCAL_PASSWORD (default "password"); the ConMan2 venv (ifcopenshell).
"""
import argparse
import os
import sys

import ifcopenshell
import ifcopenshell.util.element as ue
from neo4j import GraphDatabase

PRODUCT_TYPES = ["IfcWall", "IfcSlab", "IfcBeam", "IfcColumn", "IfcCovering",
                 "IfcRoof", "IfcDoor", "IfcWindow"]


def native_psets(path, props):
    f = ifcopenshell.open(path)
    out = {}
    for e in f.by_type("IfcProduct"):
        if e.is_a() not in PRODUCT_TYPES or not getattr(e, "Tag", None):
            continue
        if getattr(e, "Decomposes", None):
            continue   # a part of an aggregate (Revit splits a roof into IfcSlab parts) — the whole carries the psets we compare
        for name, values in ue.get_psets(e).items():
            if not name.endswith("Common"):
                continue
            out[(int(e.Tag), e.is_a())] = (name, {k: values[k] for k in props if k in values})
    return out


def graph_psets(timestamp, props):
    driver = GraphDatabase.driver("bolt://127.0.0.1:7687",
                                  auth=("neo4j", os.environ.get("NEO4J_LOCAL_PASSWORD", "password")))
    out = {}
    query = """
MATCH (p:PrimaryNode {timestamp: $ts}) WHERE p.revit_element_id IS NOT NULL AND p.EntityType IN $types
MATCH (rel {timestamp: $ts, EntityType: 'IfcRelDefinesByProperties'})-[:rel {rel_type: 'RelatedObjects'}]->(p)
MATCH (rel)-[:rel {rel_type: 'RelatingPropertyDefinition'}]->(ps)
      -[:rel {rel_type: 'HasProperties'}]->(v)-[:rel {rel_type: 'NominalValue'}]->(b:InlineNode)
WHERE ps.Name ENDS WITH 'Common' AND v.Name IN $props
RETURN p.revit_element_id AS eid, p.EntityType AS t, ps.Name AS pset, v.Name AS prop, b.wrappedValue AS val"""
    with driver.session() as s:
        for r in s.run(query, ts=timestamp, types=PRODUCT_TYPES, props=list(props)):
            key = (r["eid"], r["t"])
            out.setdefault(key, (r["pset"], {}))[1][r["prop"]] = r["val"]
    driver.close()
    return out


def native_storeys(path):
    f = ifcopenshell.open(path)
    out = {}
    for e in f.by_type("IfcProduct"):
        if e.is_a() not in PRODUCT_TYPES or not getattr(e, "Tag", None) or getattr(e, "Decomposes", None):
            continue
        rels = getattr(e, "ContainedInStructure", None) or []
        out[(int(e.Tag), e.is_a())] = rels[0].RelatingStructure.Name if rels else None
    return out


def graph_storeys(timestamp):
    driver = GraphDatabase.driver("bolt://127.0.0.1:7687",
                                  auth=("neo4j", os.environ.get("NEO4J_LOCAL_PASSWORD", "password")))
    out = {}
    query = """
MATCH (p:PrimaryNode {timestamp: $ts}) WHERE p.revit_element_id IS NOT NULL AND p.EntityType IN $types
OPTIONAL MATCH (c {timestamp: $ts, EntityType: 'IfcRelContainedInSpatialStructure'})-[:rel {rel_type: 'RelatedElements'}]->(p)
OPTIONAL MATCH (c)-[:rel {rel_type: 'RelatingStructure'}]->(st)
RETURN p.revit_element_id AS eid, p.EntityType AS t, st.Name AS storey"""
    with driver.session() as s:
        for r in s.run(query, ts=timestamp, types=PRODUCT_TYPES):
            out[(r["eid"], r["t"])] = r["storey"]
    driver.close()
    return out


def norm(v):
    if v is None:
        return None
    if isinstance(v, str) and v.lower() in ("true", "false"):
        return v.lower() == "true"
    return v


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("native")
    ap.add_argument("--timestamp", default="plugin-live")
    ap.add_argument("--props", default="IsExternal,LoadBearing")
    ap.add_argument("--storeys", action="store_true",
                    help="also compare the storey (IfcRelContainedInSpatialStructure) of every product")
    a = ap.parse_args()
    props = [p.strip() for p in a.props.split(",") if p.strip()]

    native = native_psets(a.native, props)
    graph = graph_psets(a.timestamp, props)
    if a.storeys:
        for key, storey in native_storeys(a.native).items():
            native.setdefault(key, ("(containment)", {}))[1]["Storey"] = storey
        for key, storey in graph_storeys(a.timestamp).items():
            graph.setdefault(key, ("(containment)", {}))[1]["Storey"] = storey

    print(f"{'element':<10} {'type':<12} {'pset':<22} {'prop':<12} {'native':<8} {'graph':<8}")
    diffs = 0
    for key in sorted(set(native) | set(graph)):
        n = native.get(key)
        g = graph.get(key)
        names = sorted(set((n or ("", {}))[1]) | set((g or ("", {}))[1]))
        for prop in names:
            nv = norm(n[1].get(prop)) if n else None
            gv = norm(g[1].get(prop)) if g else None
            mark = ""
            if nv != gv:
                diffs += 1
                mark = "  <-- differs" if (n and g and prop in n[1] and prop in g[1]) else "  <-- one side only"
            print(f"{key[0]:<10} {key[1]:<12} {(n or g)[0]:<22} {prop:<12} {str(nv):<8} {str(gv):<8}{mark}")
    print(f"\n{diffs} difference(s)")
    sys.exit(1 if diffs else 0)


if __name__ == "__main__":
    main()
