# IFC4 schema dumper

Generates `data/schema/ifc4_attributes.json`, the forward-attribute whitelist
that the C# plugin embeds and consults in `EntityWalker` to suppress non-schema
properties (e.g. ggifc convenience getters such as `IfcPerson.Name`).

## When to re-run

Only when the embedded IFC schema version changes — for example IFC4 → IFC4X3.
Day-to-day plugin work consumes the committed JSON; the script does not need
to run on every build.

## Requirements

- Python 3.10+
- `ifcopenshell >= 0.8` (`pip install ifcopenshell`)

The `D:\Hiwi\ConMan2` virtual environment already satisfies this; reuse it if
the project Python is not configured globally.

## Usage

```powershell
python tools/dump_ifc4_schema/dump_ifc4_schema.py
```

Output (paths relative to repo root):

```
ifcopenshell 0.8.4.post1 -> IFC4
wrote <N> entities -> data/schema/ifc4_attributes.json
```

`<N>` is typically ~800–900 entity declarations for IFC4.

## What the JSON looks like

```json
{
  "IfcPerson": ["Identification", "FamilyName", "GivenName", "MiddleNames",
                "PrefixTitles", "SuffixTitles", "Roles", "Addresses"],
  "IfcPersonAndOrganization": ["ThePerson", "TheOrganization", "Roles"]
}
```

Keys are entity declaration names; values are the ordered list of forward
attributes ifcopenshell reports via `declaration.all_attributes()`. INVERSE
and DERIVE attributes are excluded by that API.

## Notes

- The wrapper module path is `ifcopenshell.ifcopenshell_wrapper`. Older
  ifcopenshell docs occasionally show `ifcopenshell.wrapper` — that alias is
  not available in 0.8.
- Output is sorted by entity name for stable diffs.
