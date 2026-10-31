"""Print small, source-derived mechanism examples; no battle is simulated."""
import argparse
import copy
import json
from pathlib import Path
import sys

PACKAGE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGE.parent))
from startup_mechanisms_v1.encoding import encode
from startup_mechanisms_v1.schema import GLOBAL_FIELDS, MECHANISMS, SCHEMA


def load_scenarios():
    programs = json.loads((PACKAGE / "examples/card_programs.json").read_text())["cards"]
    scenarios = json.loads((PACKAGE / "examples/scenarios.json").read_text())["scenarios"]
    for scenario in scenarios:
        scenario["cards"] = [copy.deepcopy(programs[c["name"]]) | c for c in scenario["cards"]]
    return scenarios


def report(scenario, result):
    return dict(
        id=scenario["id"], mechanism=scenario["mechanism"], note=scenario["note"],
        cards=[dict(name=c["name"], pile=c["pile"], roles=r.astype(int).tolist())
               for c, r in zip(scenario["cards"], result.card_roles)],
        global_raw=result.global_raw.tolist(),
        global_encoded=[round(float(v), 6) for v in result.global_encoded],
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mechanism", choices=("all", *MECHANISMS), default="all")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    rows = []
    for scenario in load_scenarios():
        if args.mechanism not in ("all", scenario["mechanism"]):
            continue
        result = encode(scenario["cards"], scenario["buffs"], scenario["legal_hand_indices"])
        rows.append(report(scenario, result))
    if args.json:
        print(json.dumps(dict(schema=SCHEMA, scenarios=rows), indent=2))
        return
    print(f"{SCHEMA}: 16 role flags per card; 24 shared combat values.")
    print("Role groups: Rupture | Demon Form | Exhaust/Ashen | Hellraiser.")
    print("Each group: [core, producer, payoff, supply].")
    for row in rows:
        print(f"\n{row['id']}: {row['note']}")
        for card in row["cards"]:
            if card["pile"] == "hand":
                print(f"  {card['name']}: {card['roles']}")
        m = MECHANISMS.index(row["mechanism"])
        print("  Status [build_enabled, online, payoff_in_hand, payoff_legal_now]:",
              [int(v) for v in row["global_raw"][4*m:4*m+4]])
        for i in range(16, 24):
            if row["global_raw"][i] != 0:
                print(f"  [{i}] {GLOBAL_FIELDS[i]}: {row['global_raw'][i]:g} -> {row['global_encoded'][i]:g}")


if __name__ == "__main__":
    main()
