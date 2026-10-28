"""Inspect an ordered mechanics program and explicitly supplied current values."""

import argparse
import json
from pathlib import Path
import sys

# Support running this file by absolute path, including from an isolated copy.
if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from research.semantic_card_v1.catalog import Catalog, UnsupportedSemantics
from research.semantic_card_v1.encoding import encode_current_card, sparse_fields
from research.semantic_card_v1.schema import (
    CARD_DENSE_FIELDS, SCHEMA, SEMANTIC_DIMS, STEP_FIELDS, TERM_FIELDS, encode_step, encode_term,
)


def build_example(card="Bash", *, upgraded=False, numeric=None, in_hand=False, playable=False):
    """Return a deterministic, readable encoding without evaluating game mechanics."""
    catalog = Catalog()
    if card not in catalog.by_name:
        raise ValueError(f"Unknown card {card!r}; use --list for catalog names")
    if card == "MadScience":
        raise ValueError("MadScience needs a live mutable type and rider; this inspector has no live adapter")
    metadata = catalog.by_name[card]
    descriptor = catalog.card(metadata["engine_id"], upgraded)
    keywords = set(metadata["upgraded_keywords" if upgraded else "keywords"])
    flags = {name: keyword in keywords for name, keyword in (
        ("exhaust", "Exhaust"), ("ethereal", "Ethereal"), ("retain", "Retain"),
        ("innate", "Innate"), ("unplayable", "Unplayable"), ("sly", "Sly"), ("eternal", "Eternal"))}
    flags.update(in_hand=in_hand, playable=playable, cost_x=metadata["cost_x"],
                 strike_tag="Strike" in metadata["tags"], defend_tag="Defend" in metadata["tags"],
                 can_upgrade=metadata["can_upgrade"] and not upgraded)
    supplied = dict(numeric or {})
    if "upgrade_level" in supplied:
        raise ValueError("Use upgraded=True to select the matching upgrade variant")
    current = dict(supplied, upgrade_level=int(upgraded))
    if metadata["cost_x"] and supplied.get("current_cost") is not None:
        current["current_x"] = supplied["current_cost"]
    dense = encode_current_card(card_type=metadata["type"], rarity=metadata["rarity"],
                                target=metadata["target"], numeric=current, flags=flags)
    program = []
    for i, step in enumerate(catalog.descriptors[descriptor].steps):
        program.append({
            "position": i,
            "mechanics": {key: value for key, value in step.items() if key != "terms"},
            "nonzero_features": sparse_fields(encode_step(step), STEP_FIELDS),
            "terms": [{"position": j, "expression": term,
                       "nonzero_features": sparse_fields(encode_term(term), TERM_FIELDS)}
                      for j, term in enumerate(step.get("terms", []))],
        })
    return {
        "schema": SCHEMA,
        "source_version": catalog.source_version,
        "card": card,
        "upgraded": bool(upgraded),
        "input_note": "Explicit example values, not a simulator preview. Omitted current numeric values are unknown; upgrade_level comes from the selected variant.",
        "supplied_current_values": supplied,
        "context": {"in_hand": bool(in_hand), "playable": bool(playable)},
        "widths": SEMANTIC_DIMS,
        "current_card": {"width": len(CARD_DENSE_FIELDS), "nonzero_features": sparse_fields(dense, CARD_DENSE_FIELDS)},
        "program": program,
    }


def display(example):
    suffix = "+" if example["upgraded"] else ""
    lines = [f"{example['card']}{suffix} | {example['schema']}", example["input_note"],
             "", "Current card: 84 features (nonzero positions shown)",
             " index  feature                         encoded value"]
    for field in example["current_card"]["nonzero_features"]:
        lines.append(f" {field['index']:>5}  {field['name']:<30}  {field['value']:.6f}")
    lines.append("\nOrdered mechanics (278 features per step; 83 per expression term)")
    for step in example["program"]:
        lines.append(f"  Step {step['position']}: {json.dumps(step['mechanics'], sort_keys=True)}")
        for term in step["terms"]:
            lines.append(f"    Term {term['position']}: {json.dumps(term['expression'], sort_keys=True)}")
    lines.append("\nUse --json for the indexed effect-step and expression-term values.")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--card", default="Bash", help="Exact catalog name; defaults to Bash")
    parser.add_argument("--upgraded", action="store_true")
    parser.add_argument("--list", action="store_true", help="List admitted catalog names")
    parser.add_argument("--in-hand", action="store_true")
    parser.add_argument("--playable", action="store_true", help="Explicitly assert playable in this example")
    parser.add_argument("--json", action="store_true", help="Include every nonzero indexed effect and term feature")
    for argument in ("cost", "damage", "block", "hits", "play-count"):
        parser.add_argument(f"--{argument}", type=float, help=f"Explicit current {argument}; omitted means unknown")
    args = parser.parse_args()
    if args.list:
        catalog = Catalog()
        print("\n".join(sorted(row["name"] for row in catalog.cards.values()
                               if (row["engine_id"], False) in catalog.card_descriptors)))
        return 0
    numeric = {field: value for argument, field in (
        ("cost", "current_cost"), ("damage", "damage"), ("block", "block"),
        ("hits", "hits"), ("play_count", "play_count"))
        if (value := getattr(args, argument)) is not None}
    try:
        example = build_example(args.card, upgraded=args.upgraded, numeric=numeric,
                                in_hand=args.in_hand, playable=args.playable)
    except (ValueError, UnsupportedSemantics) as exc:
        parser.error(str(exc))
    print(json.dumps(example, indent=2, allow_nan=False) if args.json else display(example))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
