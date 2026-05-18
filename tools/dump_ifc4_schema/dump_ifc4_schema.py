"""Dump the IFC4 forward-attribute list for every entity declaration.

The resulting JSON is the source of truth for RevitGraphPlugin's
EntityWalker schema whitelist. INVERSE / DERIVE attributes are
excluded by ifcopenshell's `all_attributes()` API.

Re-run only when the embedded IFC schema version changes
(e.g. IFC4 -> IFC4X3).
"""

import json
from pathlib import Path

import ifcopenshell
import ifcopenshell.ifcopenshell_wrapper as W

SCHEMA_NAME = "IFC4"


def main() -> None:
    schema = W.schema_by_name(SCHEMA_NAME)
    out: dict[str, list[str]] = {}
    for decl in schema.declarations():
        if not hasattr(decl, "all_attributes"):
            continue  # skip TYPE / ENUM / SELECT — only ENTITY exposes attributes
        out[decl.name()] = [a.name() for a in decl.all_attributes()]

    out_path = (
        Path(__file__).resolve().parents[2]
        / "data"
        / "schema"
        / "ifc4_attributes.json"
    )
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(out, indent=2, sort_keys=True))
    print(f"ifcopenshell {ifcopenshell.version} -> {SCHEMA_NAME}")
    print(f"wrote {len(out)} entities -> {out_path}")


if __name__ == "__main__":
    main()
