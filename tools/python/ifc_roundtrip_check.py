#!/usr/bin/env python3
"""
ifc_roundtrip_check.py

Compare an ORIGINAL IFC (the temp IFC you fed into ConMan2) against a
ROUND-TRIPPED IFC (what came back out) and report whether the *semantics*
survived -- not just whether it opens in a viewer.

It checks the things a viewer silently ignores:
  - schema validity (ifcopenshell.validate)
  - element counts per IFC class
  - GlobalId identity (which elements were lost / added / kept)
  - spatial containment (does every element still sit under a storey?)
  - key relationships (voids, fills, material, property definitions)
  - property sets, per matching element
  - (optional) geometry bounding-box drift, per matching element

Usage:
    pip install ifcopenshell
    python ifc_roundtrip_check.py original.ifc roundtrip.ifc
    python ifc_roundtrip_check.py original.ifc roundtrip.ifc --geometry
"""
import sys
import argparse
from collections import Counter

import ifcopenshell
import ifcopenshell.util.element as ue


REL_TYPES = [
    "IfcRelContainedInSpatialStructure",
    "IfcRelAggregates",
    "IfcRelVoidsElement",
    "IfcRelFillsElement",
    "IfcRelAssociatesMaterial",
    "IfcRelDefinesByProperties",
    "IfcRelDefinesByType",
]


def banner(text):
    print("\n" + "=" * 70)
    print(text)
    print("=" * 70)


def guid_map(f):
    return {e.GlobalId: e for e in f.by_type("IfcRoot")}


def spatial_containment(f):
    """element GlobalId -> containing spatial-structure GlobalId."""
    m = {}
    for rel in f.by_type("IfcRelContainedInSpatialStructure"):
        host = rel.RelatingStructure.GlobalId
        for el in rel.RelatedElements:
            m[el.GlobalId] = host
    return m


def check_schema(label, f):
    banner(f"[1] Schema validation  ({label}: {f.schema})")
    try:
        import ifcopenshell.validate
        logger = ifcopenshell.validate.json_logger()
        ifcopenshell.validate.validate(f, logger)
        issues = logger.statements
        if not issues:
            print("  OK -- no schema issues reported.")
        else:
            print(f"  {len(issues)} issue(s):")
            for s in issues[:25]:
                print("   -", s.get("message", s))
            if len(issues) > 25:
                print(f"   ... (+{len(issues) - 25} more)")
    except Exception as exc:
        print("  Could not run validate():", exc)


def check_class_counts(a, b):
    banner("[2] Element counts per IFC class")
    ca, cb = Counter(e.is_a() for e in a), Counter(e.is_a() for e in b)
    keys = sorted(set(ca) | set(cb))
    diffs = [(k, ca[k], cb[k]) for k in keys if ca[k] != cb[k]]
    if not diffs:
        print("  OK -- identical class histogram.")
    else:
        print(f"  {'class':<42}{'orig':>8}{'round':>8}")
        for k, x, y in diffs:
            print(f"  {k:<42}{x:>8}{y:>8}")


def check_identity(a, b):
    banner("[3] GlobalId identity (version-control critical)")
    ga, gb = set(guid_map(a)), set(guid_map(b))
    lost, added, kept = ga - gb, gb - ga, ga & gb
    print(f"  kept : {len(kept)}")
    print(f"  lost : {len(lost)}  (in original, missing after round-trip)")
    print(f"  added: {len(added)}  (new GUIDs introduced by round-trip)")
    if lost:
        ma = guid_map(a)
        print("  -- lost examples:")
        for g in list(lost)[:10]:
            print(f"     {g}  {ma[g].is_a()}  {getattr(ma[g], 'Name', None)}")
    if lost or added:
        print("  WARNING: GlobalIds changed -> diff/merge will misread these "
              "as delete+insert instead of edits.")


def check_spatial(a, b):
    banner("[4] Spatial containment")
    sa, sb = spatial_containment(a), spatial_containment(b)
    orphan_a = [g for g in guid_map(a)
                if guid_map(a)[g].is_a("IfcElement") and g not in sa]
    orphan_b = [g for g in guid_map(b)
                if guid_map(b)[g].is_a("IfcElement") and g not in sb]
    print(f"  elements without a storey  -- orig: {len(orphan_a)}  "
          f"round: {len(orphan_b)}")
    moved = [g for g in (set(sa) & set(sb)) if sa[g] != sb[g]]
    print(f"  elements that changed host -- {len(moved)}")
    if orphan_b:
        print("  WARNING: round-trip produced orphan elements (no spatial "
              "structure). Solibri will list these as unassigned.")


def check_relationships(a, b):
    banner("[5] Relationship counts")
    print(f"  {'relationship':<38}{'orig':>8}{'round':>8}")
    for t in REL_TYPES:
        x, y = len(a.by_type(t)), len(b.by_type(t))
        flag = "" if x == y else "   <-- differs"
        print(f"  {t:<38}{x:>8}{y:>8}{flag}")


def check_psets(a, b):
    banner("[6] Property sets (per matching element)")
    ma, mb = guid_map(a), guid_map(b)
    shared = [g for g in (set(ma) & set(mb)) if ma[g].is_a("IfcObject")]
    missing = 0
    changed = 0
    for g in shared:
        try:
            pa = ue.get_psets(ma[g])
            pb = ue.get_psets(mb[g])
        except Exception:
            continue
        if set(pa) - set(pb):
            missing += 1
        elif pa != pb:
            changed += 1
    print(f"  matched objects        : {len(shared)}")
    print(f"  lost a whole pset      : {missing}")
    print(f"  pset values changed    : {changed}")


def check_geometry(a, b):
    banner("[7] Geometry bounding-box drift (optional, slow)")
    try:
        import ifcopenshell.geom
        import numpy as np
    except Exception as exc:
        print("  Skipped (needs ifcopenshell.geom + numpy):", exc)
        return
    settings = ifcopenshell.geom.settings()

    def bboxes(f):
        out = {}
        it = ifcopenshell.geom.iterator(settings, f)
        if it.initialize():
            while True:
                sh = it.get()
                v = np.array(sh.geometry.verts).reshape(-1, 3)
                if len(v):
                    out[sh.guid] = (v.min(0), v.max(0))
                if not it.next():
                    break
        return out

    ba, bb = bboxes(a), bboxes(b)
    shared = set(ba) & set(bb)
    worst = 0.0
    for g in shared:
        d = max(float(np.abs(ba[g][0] - bb[g][0]).max()),
                float(np.abs(ba[g][1] - bb[g][1]).max()))
        worst = max(worst, d)
    print(f"  elements with geometry both sides: {len(shared)}")
    print(f"  max bounding-box corner drift    : {worst:.6g} (model units)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("original")
    ap.add_argument("roundtrip")
    ap.add_argument("--geometry", action="store_true",
                    help="also compare per-element bounding boxes (slower)")
    args = ap.parse_args()

    a = ifcopenshell.open(args.original)
    b = ifcopenshell.open(args.roundtrip)
    print(f"original : {args.original}  ({a.schema})")
    print(f"roundtrip: {args.roundtrip}  ({b.schema})")
    if a.schema != b.schema:
        print("  NOTE: schema versions differ -- expect many downstream diffs.")

    check_schema("original", a)
    check_schema("roundtrip", b)
    check_class_counts(a, b)
    check_identity(a, b)
    check_spatial(a, b)
    check_relationships(a, b)
    check_psets(a, b)
    if args.geometry:
        check_geometry(a, b)

    print("\nDone. A clean round-trip = no lost GUIDs, no orphan elements,\n"
          "matching relationship counts, and no missing property sets.")


if __name__ == "__main__":
    main()
