#!/usr/bin/env python3
"""Verify release integrity and recompute the recorded benchmark (stdlib only)."""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys


ROOT = Path(__file__).resolve().parents[1]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_json(path: str):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def checked_path(relative: str) -> Path:
    path = (ROOT / relative).resolve()
    require(path.is_relative_to(ROOT), f"Path outside release: {relative}")
    require(path.is_file(), f"Missing file: {relative}")
    return path


def verify_hashes() -> int:
    seen = set()
    for line in (ROOT / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        expected, relative = line.split("  ", 1)
        require(relative not in seen, f"Duplicate checksum entry: {relative}")
        require(len(expected) == 64, f"Malformed checksum: {relative}")
        require(sha256(checked_path(relative)) == expected, f"Hash mismatch: {relative}")
        seen.add(relative)
    required = {
        "README.md", "VERSION", "models/choice.pt", "models/battle.pt",
        "results/runs.jsonl", "results/validation_seeds.json", "results/summary.json",
        "results/model_identities.json", "results/evaluation_config.json",
        "scripts/evaluate.py", "scripts/verify_release.py",
    }
    require(required <= seen, f"Incomplete checksum manifest: {sorted(required - seen)}")
    identities = read_json("results/model_identities.json")
    for role in ("choice", "battle"):
        item = identities[role]
        path = checked_path(item["release_file"])
        require(path.stat().st_size == item["bytes"], f"Wrong {role} checkpoint size")
        require(sha256(path) == item["sha256"], f"Wrong {role} checkpoint identity")
    return len(seen)


def close(actual: float, expected: float, label: str) -> None:
    require(math.isclose(actual, expected, rel_tol=1e-12, abs_tol=1e-12),
            f"Incorrect {label}: {actual} != {expected}")


def verify_evidence() -> dict:
    provenance = read_json("results/SOURCE_PROVENANCE.json")
    for relative, identity in provenance["packaged_files"].items():
        path = checked_path(f"results/{relative}")
        require(path.stat().st_size == identity["bytes"], f"Evidence size mismatch: {relative}")
        require(sha256(path) == identity["sha256"], f"Evidence hash mismatch: {relative}")
    summary = read_json("results/summary.json")
    seed_manifest = read_json("results/validation_seeds.json")
    config = read_json("results/evaluation_config.json")
    rows = [json.loads(line) for line in
            (ROOT / "results/runs.jsonl").read_text(encoding="utf-8").splitlines() if line]
    seeds = seed_manifest["seeds"]
    require(len(seeds) == len(set(seeds)) == seed_manifest["count"] == 256,
            "Expected exactly 256 unique validation seeds")
    require(len(rows) == 256, "Expected exactly 256 run outcomes")
    require(seed_manifest["namespace"] == "validation" and not seed_manifest["reserved_test_used"],
            "Cohort must remain validation")
    for index, (seed, row) in enumerate(zip(seeds, rows)):
        require(row["seed_index"] == index and row["seed"] == seed,
                f"Seed/order mismatch at row {index}")
        require(row["engine_seed"] == f"RS1V{int(seed):016X}", f"Engine seed mismatch at {index}")
        require(type(row["won"]) is bool, f"Nonboolean won value at {index}")
        expected_win = row["outcome_kind"] == "natural" and row["outcome_reason"] == "run_victory"
        require(row["won"] == expected_win == row["boss3_won"], f"Inconsistent victory at {index}")
        require((row["return"] is None) == (row["outcome_kind"] != "natural"),
                f"Incorrect undefined reward handling at {index}")
    outcomes = dict(sorted(Counter(f'{r["outcome_kind"]}:{r["outcome_reason"]}' for r in rows).items()))
    expected = {"natural:run_victory": 68, "natural:death_in_combat": 184,
                "limit:battle_turn_limit": 4}
    require(outcomes == expected == summary["outcomes_by_kind_and_reason"], "Outcome counts disagree")
    counts = summary["counts"]
    for field in ("planned", "admitted", "observed"):
        require(counts[field] == len(rows), f"Incorrect {field} denominator")
    for field, expected_count in {"run_victories": 68, "natural_deaths": 184,
            "natural_endings": 252, "undefined_endings": 4, "battle_turn_limits": 4,
            "errors": 0, "unsupported": 0, "missing": 0, "never_admitted": 0,
            "admitted_unresolved": 0}.items():
        require(counts[field] == expected_count, f"Incorrect {field}")
    wins = sum(row["won"] for row in rows)
    close(wins / len(rows), summary["win_rate"], "win rate")
    close(100 * wins / len(rows), summary["win_rate_percent"], "win percent")
    for field, value in summary["progression"].items():
        require(sum(bool(row[field]) for row in rows) == value, f"Incorrect progression: {field}")
    rewards = [row["return"] for row in rows if row["outcome_kind"] == "natural"]
    require(len(rewards) == summary["reward"]["natural_only"]["denominator"], "Reward denominator")
    close(statistics.mean(rewards), summary["reward"]["natural_only"]["mean"], "mean reward")
    for label, group in (
        ("natural_endings", [r for r in rows if r["outcome_kind"] == "natural"]),
        ("victories", [r for r in rows if r["won"]]),
    ):
        report = summary["remaining_hp"][label]
        require(len(group) == report["denominator"], f"{label} HP denominator")
        hp = [r["last_battle_terminal_hp"] for r in group]
        close(statistics.mean(hp), report["mean_hp"], f"{label} mean HP")
        close(statistics.median(hp), report["median_hp"], f"{label} median HP")
        close(statistics.mean(r["last_battle_terminal_hp"] / r["last_battle_terminal_max_hp"]
                              for r in group), report["mean_hp_fraction"], f"{label} HP fraction")
    require((config["character"], config["ascension"], config["acts"]) == ("Ironclad", 2, 3),
            "Unexpected benchmark scope")
    require(config["limits"]["battle_turns"] == 100, "Unexpected turn limit")
    selection = summary["selection_context"]
    require(selection["choice_checkpoints"] * selection["battle_checkpoints"] == selection["pairings"] == 1260,
            "Selection context mismatch")
    require(selection["independent_final_test"] is False, "Must not relabel as held-out test")
    return {"status": "verified", "runs": len(rows), "wins": wins,
            "win_rate_percent": 100 * wins / len(rows), "deaths": 184,
            "battle_turn_limits": 4, "scope": "selected simulator validation"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-only", action="store_true",
                        help="Verify result-file provenance and arithmetic; skip runtime/model file hashes")
    args = parser.parse_args()
    try:
        result = verify_evidence()
        if not args.evidence_only:
            result["files_hash_verified"] = verify_hashes()
        print(json.dumps(result, indent=2))
        return 0
    except (ValueError, OSError, KeyError, TypeError) as exc:
        print(f"Verification failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
