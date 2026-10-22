"""Battle-width A-F architecture profiles (no torch / crl import: safe for queues before the environment is set).

Source of truth: experiments/trials/battle-width-af-v1/SPEC.txt. :mod:`battle_width_scan_v1.model` builds
these; tests/test_model.py re-derives every count and input width by actual construction.
"""
from __future__ import annotations

MODEL_SCHEMA = "bw1/model/v1"
WIDTH_KEYS = ("card_hidden", "card_dim", "pile_dim", "eff_hidden", "eff_dim", "enemy_hidden", "enemy_dim",
              "state_dim", "head")
BASELINE = {"card_hidden": 128, "card_dim": 64, "pile_dim": 64, "eff_hidden": 64, "eff_dim": 32,
            "enemy_hidden": 128, "enemy_dim": 64, "state_dim": 256, "head": 128}  # == crl.model.QNet
PROFILES = {
    "A": dict(BASELINE),
    "B": {**BASELINE, "state_dim": 512},
    "C": {**BASELINE, "head": 256},
    "D": {**BASELINE, "state_dim": 512, "head": 256},
    "E": {**BASELINE, "eff_hidden": 128, "eff_dim": 64, "enemy_hidden": 256, "enemy_dim": 128},
    "F": {**BASELINE, "card_hidden": 256, "card_dim": 128, "pile_dim": 128},
}
CONFIGURATIONS = tuple(PROFILES)
# Trainable parameters under the production context (build08, 214 cards, crl.features/v3-potion-v2, potion
# options, enemy count, no dueling). A-D are the SPEC's numbers; E and F were derived by construction:
# E = A + 8,928 effect enc + 47,168 enemy enc + 160 * 256 state input + 64 * 128 candidate input;
# F = A + 41,920 card enc + 172,928 pile projections + 448 * 256 state input + 64 * 128 candidate input.
EXPECTED_PARAMETERS = {"A": 492_117, "B": 940_373, "C": 595_669, "D": 1_076_693, "E": 597_365, "F": 829_845}
# (state encoder input incl. enemy count, candidate head input) under the production context.
EXPECTED_INPUTS = {"A": (853, 422), "B": (853, 678), "C": (853, 422), "D": (853, 678), "E": (1013, 486),
                   "F": (1301, 486)}


def profile(configuration: str) -> dict:
    if configuration not in PROFILES:
        raise ValueError(f"unknown battle-width configuration {configuration!r} (one of {', '.join(PROFILES)})")
    return dict(PROFILES[configuration])


def expected_parameters(configuration: str) -> int:
    profile(configuration)
    return EXPECTED_PARAMETERS[configuration]


def expected_inputs(configuration: str) -> tuple[int, int]:
    profile(configuration)
    return EXPECTED_INPUTS[configuration]


def configuration_of(widths: dict) -> str | None:
    """The profile letter whose nine widths equal ``widths`` (missing keys = baseline), else None."""
    w = {k: widths.get(k, BASELINE[k]) for k in WIDTH_KEYS}
    return next((c for c, p in PROFILES.items() if p == w), None)
