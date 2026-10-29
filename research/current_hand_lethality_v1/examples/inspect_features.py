"""Inspect a saved native Bash/Strike example without loading a simulator."""

import argparse
import json
from pathlib import Path
import sys

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from research.current_hand_lethality_v1.encoding import encode
from research.current_hand_lethality_v1.schema import (
    ACTION_FIELDS, ENEMY_FIELDS, FULL_ACTION_FIELDS, FULL_ENEMY_FIELDS,
    MASKED_ENEMY_INDICES, PROFILES, SCHEMA,
)


def build_example(profile="masked_lethality"):
    fixture = json.loads(Path(__file__).with_name("bash_strike.json").read_text(encoding="utf-8"))
    enemies, actions = encode(fixture["observation"], fixture["base_enemy_features"],
                              fixture["base_action_features"], profile=profile)
    return {
        "schema": SCHEMA,
        "profile": profile,
        "fixture_id": fixture["case_id"],
        "scope": "Inspection of saved native output; no new search or policy inference.",
        "masked_enemy_columns": list(MASKED_ENEMY_INDICES),
        "enemy_field_order": list(FULL_ENEMY_FIELDS),
        "action_field_order": list(FULL_ACTION_FIELDS),
        "added_enemy_fields": list(ENEMY_FIELDS),
        "added_action_fields": list(ACTION_FIELDS),
        "enemies": [{"index": i, "current_hp": fixture["observation"]["enemies"][i]["hp"],
                     "base_values_before_mask": fixture["base_enemy_features"][i],
                     "added_values": row[-2:].tolist(), "full_values": row.tolist()}
                    for i, row in enumerate(enemies)],
        "actions": [{"option": i, "label": fixture["action_labels"][i],
                     "target": fixture["observation"]["legal"][i]["target"],
                     "added_values": row[-4:].tolist(), "full_values": row.tolist()}
                    for i, row in enumerate(actions)],
        "native_paths": fixture["native_paths"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=PROFILES, default="masked_lethality")
    parser.add_argument("--json", action="store_true", help="Print labeled vectors as JSON")
    args = parser.parse_args()
    result = build_example(args.profile)
    if args.json:
        print(json.dumps(result, indent=2))
        return
    print(f"Current-hand lethality v1 | {result['profile']}")
    print("Recorded native example: Bash + Strike, 3 energy, enemy 15 HP / 0 block.")
    print(result["scope"])
    print("Enemy columns 1 (max_hp) and 2 (hp_fraction): masked to 0.")
    print("Enemy added columns 30..31: " + ", ".join(ENEMY_FIELDS))
    print("  Enemy 0: " + str(result["enemies"][0]["added_values"]))
    print("Action added columns 6..9: " + ", ".join(ACTION_FIELDS))
    for action in result["actions"]:
        print(f"  Option {action['option']} {action['label']}: {action['added_values']}")
    print("Recorded paths: Bash -> Strike wins; Strike -> Bash leaves 1 enemy HP.")
    print("Incomplete negative results mean unknown; non-card options have zero added fields.")


if __name__ == "__main__":
    main()
