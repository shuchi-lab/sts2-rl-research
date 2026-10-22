"""Frozen identities and fixed experiment constants (no torch / crl import).

``pins.json`` was generated read-only from the crossplay manifest and the source E checkpoint's own
metadata; :func:`check_pins` re-verifies its internal consistency. Every gate that compares a running
file against a pin lives here so that one table governs the whole package.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent
ROOT = PACKAGE.parent  # combat_rl/
PINS_PATH = PACKAGE / "pins.json"
PINS: dict = json.loads(PINS_PATH.read_text(encoding="utf-8"))

SCHEMA_PREFIX = "act1ft"
SOURCE_E = PINS["source_e"]
STRATEGY = PINS["strategy"]
SNAPSHOT_ATTEMPTS = (5000, 15000, 30000, 55000)

# Budget (SPEC section 0/7)
START_FIGHTS = 35_000
NEW_FIGHTS = 20_000
FINAL_FIGHTS = START_FIGHTS + NEW_FIGHTS
CHECKPOINT_EVERY = 2_500
CHECKPOINT_LABELS = tuple(range(START_FIGHTS + CHECKPOINT_EVERY, FINAL_FIGHTS + 1, CHECKPOINT_EVERY))

# Seeds / namespaces (SPEC sections 3, 4, 9). All new.
SOURCE_SEED_BASE = 2026100801
TRAIN_SEED_BASE = 2026100802
SPLIT_SEED = 2026100803
HELDOUT_EVAL_SEED = 2026100804
E2E_SEED_BASE = 2026100805
VAL_SEED = 2026100404  # the ORIGINAL validation seed (never changed)
SOURCE_SEED_PREFIX = "A1FS"
E2E_SEED_PREFIX = "A1FE"
TRAIN_SEED_TAG = "act1ft-train"
HELDOUT_PERCENT = 15
E2E_SEEDS = 512
DEFAULT_SOURCE_RUNS = 2048
HELDOUT_EVAL_SEEDS_PER_TEMPLATE = 4

# Sampling (SPEC section 4)
REACH_FRACTION = 0.70
CELL_TARGETS = {  # (act group, room) -> rows of the 20,000
    ("act1", "boss"): 6400, ("act1", "elite"): 3200, ("act1", "normal"): 6400,
    ("act23", "boss"): 1600, ("act23", "elite"): 800, ("act23", "normal"): 1600,
}
REACHED, FAILED = "reached_act2", "failed_before_act2"

# Exploration / learner (SPEC section 7)
EPS_START, EPS_END, EPS_DECAY_FIGHTS = 0.20, 0.05, 10_000
REPLAY_GATE_TRANSITIONS = 2_000
LATER_ACT_GUARD_FIGHTS = 14  # selection guard: later-act wins >= baseline - 14 (of 688 fights, ~2.0 pp)


def sha256_file(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def check_pins(p: dict = PINS) -> None:
    if p.get("schema") != "act1ft/pins/v1":
        raise ValueError("pins.json schema")
    e = p["source_e"]
    if (e["parameters"], e["episodes"], e["updates"], e["validation_round"], e["configuration"]) != \
            (597_365, START_FIGHTS, 58_918, 14, "E"):
        raise ValueError(f"source E pins are not the selected Windows E35k: {e}")
    if e["has_optimizer"] or e["has_target"]:
        raise ValueError("source pins claim an optimizer/target state; the actual exact35k blob has none")
    snaps = p["strategy"]["snapshots"]
    if tuple(s["attempts"] for s in snaps) != SNAPSHOT_ATTEMPTS or any(s["n_step"] != 5 for s in snaps):
        raise ValueError("strategy snapshots are not n5-uniform 5k/15k/30k/55k")
    if len({s["file_sha256"] for s in snaps}) != 4 or len({s["run_id"] for s in snaps}) != 1:
        raise ValueError("strategy snapshots are not four distinct files of one run")
    if sum(CELL_TARGETS.values()) != NEW_FIGHTS:
        raise ValueError("cell targets do not sum to the 20,000 new fights")
    act1 = sum(v for (g, _), v in CELL_TARGETS.items() if g == "act1")
    boss = sum(v for (_, r), v in CELL_TARGETS.items() if r == "boss")
    elite = sum(v for (_, r), v in CELL_TARGETS.items() if r == "elite")
    if (act1, boss, elite) != (16_000, 8_000, 4_000):
        raise ValueError("cell targets are not 80 % Act 1 / 40 % boss / 20 % elite")


def strategy_package_problems(package_dir: Path) -> list[str]:
    """Files of the imported run_strategy_v1 that differ from (or are missing against) the pinned
    deployment that trained the n5-uniform snapshots."""
    out = []
    for name, want in sorted(STRATEGY["package_sha256"].items()):
        p = Path(package_dir) / name
        if not p.is_file():
            out.append(f"missing {name}")
        elif sha256_file(p) != want:
            out.append(f"changed {name}")
    return out


def require_strategy_package() -> Path:
    """Production gate: the importable run_strategy_v1 must be the exact pinned strategy deployment."""
    import run_strategy_v1
    d = Path(run_strategy_v1.__file__).resolve().parent
    bad = strategy_package_problems(d)
    if bad:
        raise RuntimeError(f"run_strategy_v1 at {d} is not the pinned n5-uniform deployment ({len(bad)} files: "
                           f"{bad[:6]}); stage {STRATEGY['package_exact_copy_relative']}")
    return d


def code_pin_problems(root: Path = ROOT) -> list[str]:
    """The source E run's 28 code pins against the running sources."""
    out = []
    for rel, want in sorted(SOURCE_E["code_sha256"].items()):
        p = Path(root) / rel
        got = sha256_file(p) if p.is_file() else None
        if got != want:
            out.append(f"{rel}: {got} != {want}")
    return out


check_pins()
