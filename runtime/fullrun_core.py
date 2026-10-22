from __future__ import annotations

from collections import Counter

from datetime import datetime, timezone

import hashlib

import json

import math

import os

from pathlib import Path, PurePosixPath, PureWindowsPath

import re

import time

CELL_SCHEMA = "bwc1/fullrun/cell/v1"

REWARD_ID = "combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1"

BLOCK_SIZE = 256

NAMESPACE_TAGS = {"primary": "bwc1-fullrun-screen", "secondary": "bwc1-fullrun-choiceB",
                  "diagnostic": "bwc1-fullrun-diagnostic"}

POTION_BILL_FEATURES = ("bill_accumulated", "charged_uses", "use_charge_now")

class ContractViolation(ValueError):
    """A declared execution contract is violated: a visible operational error, never a fallback."""

def utc(value: str) -> datetime:
    d = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if d.tzinfo is None:
        raise ValueError("UTC time needs an offset")
    return d.astimezone(timezone.utc)

def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()

def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def write_json(path, value, *, exclusive=False):
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    if exclusive:
        with p.open("x", encoding="utf-8") as f:
            json.dump(value, f, indent=2, allow_nan=False)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        return
    temp = p.with_name(p.name + f".{os.getpid()}.tmp")
    with temp.open("w", encoding="utf-8") as f:
        json.dump(value, f, indent=2, allow_nan=False)
        f.write("\n")
        f.flush()
    replace_with_retry(temp, p)

def cell_id(choice_id: str, model: dict, block: int, *, diagnostic: bool = False) -> str:
    tail = "diag" if diagnostic else f"b{block:02d}"
    return f"{choice_id}__{model['id']}__{tail}"

def cell_hard_stop(manifest: dict) -> float:
    return utc(manifest["final_deadline_utc"]).timestamp() - manifest["cell_admission_hard_stop_s"]

def parse_jsonl(path) -> list:
    p = Path(path)
    if not p.exists():
        return []
    rows = []
    for i, line in enumerate(p.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as e:
            raise ValueError(f"{p.name}: partial/corrupt line {i}; retained, never silently retried") from e
    return rows

def checked_rows(rows: list, seeds: list) -> list:
    observed = [r["seed"] for r in rows]
    if len(set(observed)) != len(observed) or not set(observed) <= set(seeds):
        raise ValueError("duplicate or unexpected retained seed; replay refused")
    return rows

def run_flags(record: dict) -> dict:
    o = record["outcome"]
    battles = o.get("battles") or []
    max_act = max([int(o.get("act_ordinal") or 0)] + [int(f.get("act_ordinal") or 0) for f in battles])
    boss = {a: [f for f in battles if int(f.get("act_ordinal") or 0) == a and f.get("room") == "boss"] for a in (1, 2, 3)}
    return {"won": bool(o.get("won")) and o.get("kind") == "natural", "act2": max_act >= 2, "act3": max_act >= 3,
            **{f"boss{a}_entered": bool(boss[a]) for a in (1, 2, 3)},
            **{f"boss{a}_won": any(f.get("outcome") == "victory" for f in boss[a]) for a in (1, 2, 3)}}

def summarize_cell(records: list, admissions: list, seeds: list) -> dict:
    """Fixed attempted denominator = the 256 planned seeds; every non-win (undefined, error, missing) counts."""
    N = len(seeds)
    if not 1 <= N <= BLOCK_SIZE or len(set(seeds)) != N:
        raise ValueError("a release run needs 1..256 distinct planned seeds")
    checked_rows(records, seeds)
    checked_rows(admissions, seeds)
    rec_seeds = {r["seed"] for r in records}
    adm_seeds = {r["seed"] for r in admissions}
    if not rec_seeds <= adm_seeds:
        raise ValueError("an outcome exists for a seed that was never admitted")
    kinds = Counter(r["outcome"]["kind"] for r in records)
    reasons = Counter(f"{r['outcome']['kind']}:{r['outcome']['reason']}" for r in records if r["outcome"]["kind"] != "natural")
    flags = [run_flags(r) for r in records]
    count = lambda k: sum(f[k] for f in flags)
    natural = [r["return"] for r in records if r["outcome"]["kind"] == "natural" and r.get("return") is not None]
    unsupported = sum(v for k, v in reasons.items() if k.startswith("unsupported:"))
    turn_limit = sum(v for k, v in reasons.items() if k.startswith("limit:"))
    errors = kinds.get("error", 0)
    out = {"attempted": N, "denominator": N, "admitted": len(adm_seeds), "observed": len(rec_seeds),
           "missing": N - len(rec_seeds), "never_admitted": N - len(adm_seeds),
           "admitted_unresolved": len(adm_seeds - rec_seeds), "natural": kinds.get("natural", 0),
           "undefined": sum(v for k, v in kinds.items() if k != "natural"), "unsupported": unsupported,
           "turn_or_step_limit": turn_limit, "errors": errors, "undefined_by_reason": dict(reasons),
           "battle_turn_limit": reasons.get("limit:battle_turn_limit", 0),
           "run_victories": count("won"), "win_rate_fixed": count("won") / N,
           "act2": count("act2"), "act3": count("act3"),
           **{k: count(k) for k in ("boss1_entered", "boss1_won", "boss2_entered", "boss2_won",
                                    "boss3_entered", "boss3_won")},
           "act3_boss_entry_rate_fixed": count("boss3_entered") / N,
           "natural_only_return": {"denominator": len(natural),
                                   "mean": sum(natural) / len(natural) if natural else None,
                                   "note": "descriptive; selected denominator = natural endings only"},
           "complete": len(rec_seeds) == N}
    return out

def bill_feature_indices(glob_width: int, potion_features: tuple) -> dict:
    n = len(potion_features)
    return {f: glob_width - n + potion_features.index(f) for f in POTION_BILL_FEATURES}

def boss_bill_violations(glob, room: str, ledger_bill: float, ledger_charged: int, potion_features: tuple,
                         expected_width: int | None = None) -> list[str]:
    """All Acts' boss decisions must encode zero bill/charges and keep a zero ledger."""
    width = len(glob)
    p = []
    if expected_width is not None and width != expected_width:
        p.append(f"global feature width {width} != model-declared {expected_width}")
        return p
    for name, value in (("bill", ledger_bill), ("charged_uses", ledger_charged)):
        if not math.isfinite(float(value)):
            p.append(f"nonfinite ledger {name}")
    if room != "boss":
        return p
    for f, i in bill_feature_indices(width, potion_features).items():
        if float(glob[i]) != 0.0:
            p.append(f"boss feature {f}={float(glob[i])!r} is not zero")
    if float(ledger_bill) != 0.0 or int(ledger_charged) != 0:
        p.append(f"boss ledger bill={ledger_bill!r} charged_uses={ledger_charged!r} not zero")
    return p

REPLACE_RETRY_DELAYS_S = (0.01, 0.02, 0.05, 0.1, 0.15, 0.25, 0.4)
TRANSIENT_REPLACE_WINERRORS = (5, 32, 33)

def replace_with_retry(src, dst, *, replace=None, sleep=None, delays=REPLACE_RETRY_DELAYS_S):
    """Bounded retry of Windows access/sharing violations; persistent errors surface."""
    replace, sleep = replace or os.replace, sleep or time.sleep
    for attempt in range(len(delays) + 1):
        try:
            replace(src, dst)
            return attempt
        except PermissionError as exc:
            if getattr(exc, "winerror", None) not in TRANSIENT_REPLACE_WINERRORS or attempt == len(delays):
                raise
            sleep(delays[attempt])
