#!/usr/bin/env python3
"""Semantic diff of two IFC files, ignoring identity noise.

Built to verify the plugin's ggifc export (Revit -> ggifc) against Revit's
own native IFC export. Because the two come from different exporters, they
legitimately differ in identity fields (GlobalId, OwnerHistory timestamps,
#id numbering, the HEADER). This tool masks that noise and reports only
content differences: which entities exist per type, and which attribute
values differ.

What it compares
----------------
* Per IFC type: entity count in REF vs CAND.
* Per entity: a content "signature" = type + every forward attribute, with
  - GlobalId / OwnerHistory masked (identity),
  - entity references reduced to their type name (so #id renumbering and
    deep geometry don't create false diffs),
  - wrapped value types (IfcIdentifier('x'), IfcInteger(0), ...) kept WITH
    their value, so property values ARE compared.

Known limitation: real entity refs are reduced to type only (depth 0), so
deep numeric diffs inside referenced geometry (e.g. IfcCartesianPoint
coordinates) are NOT surfaced. Direct numeric attributes (e.g. Storey
Elevation) ARE. Good enough for the boilerplate; deepen later if needed.

Usage
-----
    <conman2-venv-python> compare_ifc.py [REF.ifc] [CAND.ifc] [--out [PATH]]

With no positional args it defaults to the 00_empty pair in this repo:
    REF  = data/samples/ifc/00_empty.ifc        (Revit native)
    CAND = data/test/00_empty_ggifc.ifc          (plugin ggifc export)

--out writes the full report to a file (in addition to stdout):
    --out PATH    write the report to PATH
    --out         (no value) auto-name it data/test/compare_<cand-stem>.txt

Requires ifcopenshell (present in ConMan2's venv).
"""
import argparse
import sys
from collections import Counter, defaultdict
from pathlib import Path

try:
    import ifcopenshell
except ImportError:
    sys.exit(
        "[compare_ifc] ifcopenshell not found. Run with ConMan2's venv, e.g.:\n"
        r"  D:\Hiwi\ConMan2\venv\Scripts\python.exe "
        + str(Path(__file__).name)
    )

# Repo root = two parents up (tools/python -> repo root).
REPO = Path(__file__).resolve().parents[2]
DEFAULT_REF = REPO / "data" / "samples" / "ifc" / "00_empty.ifc"
DEFAULT_CAND = REPO / "data" / "test" / "00_empty_ggifc.ifc"

# Attribute names masked on EVERY entity (pure identity / bookkeeping).
IGNORE_ATTRS = {"GlobalId", "OwnerHistory"}
# Per-type attribute masks (volatile values that are not content).
IGNORE_BY_TYPE = {
    "IfcOwnerHistory": {"CreationDate", "LastModifiedDate"},
    "IfcApplication": {"Version"},
}


def norm_value(v):
    """Normalize one attribute value for comparison.

    - real entity instances (id > 0)  -> "<IfcType>"  (drops #id)
    - wrapped value types  (id == 0)  -> "<IfcType value>"  (keeps the value)
    - list / tuple                    -> tuple of normalized items
    - primitives                      -> unchanged
    """
    if v is None:
        return None
    if isinstance(v, ifcopenshell.entity_instance):
        try:
            is_wrapped = v.id() == 0
        except Exception:
            is_wrapped = False
        if is_wrapped:
            try:
                return f"<{v.is_a()} {v.wrappedValue!r}>"
            except Exception:
                return f"<{v.is_a()}>"
        return f"<{v.is_a()}>"
    if isinstance(v, (list, tuple)):
        return tuple(norm_value(x) for x in v)
    return v


def signature(inst):
    """Content signature for an entity: (type, ((attr, value), ...))."""
    info = inst.get_info(recursive=False)
    typ = info.pop("type")
    info.pop("id", None)
    type_mask = IGNORE_BY_TYPE.get(typ, set())
    fields = []
    for name in sorted(info):
        if name in IGNORE_ATTRS or name in type_mask:
            continue
        fields.append((name, repr(norm_value(info[name]))))
    return typ, tuple(fields)


def load(path):
    """Return {ifc_type: Counter(signature_fields -> count)}."""
    model = ifcopenshell.open(str(path))
    by_type = defaultdict(Counter)
    for inst in model:
        typ, fields = signature(inst)
        by_type[typ][fields] += 1
    return by_type


def fmt(fields):
    if not fields:
        return "(no content attrs)"
    return "\n".join(f"        {name} = {val}" for name, val in fields)


def build_report(ref_path, cand_path):
    """Run the comparison and return (report_text, n_diff)."""
    ref, cand = load(ref_path), load(cand_path)
    all_types = sorted(set(ref) | set(cand))

    head = [
        f"REF  (native) : {ref_path}",
        f"CAND (ggifc)  : {cand_path}",
        "=" * 72,
    ]
    n_ok = n_diff = 0
    diff_lines = []
    for typ in all_types:
        ra = ref.get(typ, Counter())
        ca = cand.get(typ, Counter())
        rn, cn = sum(ra.values()), sum(ca.values())
        only_ref = ra - ca       # signatures (with multiplicity) only in REF
        only_cand = ca - ra      # ... only in CAND
        if rn == cn and not only_ref and not only_cand:
            n_ok += 1
            continue
        n_diff += 1
        diff_lines.append(f"\nDIFF  {typ}:  REF={rn}  CAND={cn}")
        for sig, n in only_ref.items():
            diff_lines.append(f"   - REF  only ({n}x):\n{fmt(sig)}")
        for sig, n in only_cand.items():
            diff_lines.append(f"   + CAND only ({n}x):\n{fmt(sig)}")

    body = [
        f"types OK (identical): {n_ok}",
        f"types with diffs    : {n_diff}",
        "\n".join(diff_lines),
        "\n" + "=" * 72,
        "Reminder: GlobalId / OwnerHistory timestamps / #id are masked by design.",
    ]
    return "\n".join(head + body), n_diff


def main():
    parser = argparse.ArgumentParser(
        prog="compare_ifc",
        description="Semantic diff of two IFC files, ignoring identity noise.",
    )
    parser.add_argument("ref", nargs="?", default=str(DEFAULT_REF),
                        help="reference IFC (default: data/samples/ifc/00_empty.ifc)")
    parser.add_argument("cand", nargs="?", default=str(DEFAULT_CAND),
                        help="candidate IFC (default: data/test/00_empty_ggifc.ifc)")
    parser.add_argument("--out", nargs="?", const="<auto>", default=None,
                        help="also write the report to a file; bare --out auto-names "
                             "it data/test/compare_<cand-stem>.txt")
    args = parser.parse_args()

    ref_path = Path(args.ref)
    cand_path = Path(args.cand)
    for p in (ref_path, cand_path):
        if not p.is_file():
            sys.exit(f"[compare_ifc] file not found: {p}")

    report, n_diff = build_report(ref_path, cand_path)
    print(report)

    if args.out is not None:
        out_path = (REPO / "data" / "test" / f"compare_{cand_path.stem}.txt"
                    if args.out == "<auto>" else Path(args.out))
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(report, encoding="utf-8")
        print(f"\n[compare_ifc] report written to: {out_path}")

    return 1 if n_diff else 0


if __name__ == "__main__":
    sys.exit(main())
