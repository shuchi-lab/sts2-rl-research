"""``run-strategy-act-win-v1`` (Task 6): the strategic reward, separate from E's battle reward.

A natural fight victory pays by act ordinal (``CurrentActIndex + 1``, never the region id) and room:
ordinary / elite / event fights 0.1 / 0.2 / 0.4, the act boss 1 / 2 / 5. Every other legitimate
transition pays 0 -- death, a Battleworn Dummy escape (the event shows DEFEAT), every menu, the
Architect's HP-0 completion. Each explicit fight id is paid at most once; one instance per run.
No E combat reward or potion bill ever enters this value.
"""
from __future__ import annotations

import math

from .contracts import REWARD_ID, ROOMS, BattleEvent, ContractError

ORDINARY = {1: 0.1, 2: 0.2, 3: 0.4}
BOSS = {1: 1.0, 2: 2.0, 3: 5.0}


def describe() -> dict:
    return {"reward_id": REWARD_ID, "ordinary_elite_event_victory": ORDINARY, "boss_victory": BOSS,
            "act": "CurrentActIndex + 1 (region ids Overgrowth/Underdocks/Hive/Glory are not acts)",
            "zero": ["death", "event_escape (Battleworn Dummy DEFEAT page)", "every noncombat transition",
                     "The Architect HP-0 completion", "run completion bonus: none"],
            "once": "each fight id (run_id:fNNN) is paid at most once", "gamma": 1.0,
            "excluded": "E's combat reward and potion bill are never added"}


def fight_value(e: BattleEvent) -> float:
    if e.room not in ROOMS:
        raise ContractError(f"unknown room {e.room!r}")
    if e.act_ordinal not in ORDINARY:
        raise ContractError(f"act ordinal {e.act_ordinal} outside 1..3")
    if e.outcome != "victory":
        return 0.0
    return BOSS[e.act_ordinal] if e.room == "boss" else ORDINARY[e.act_ordinal]


class StrategyReward:
    """One per run: consume each reply's closed-fight events exactly once."""

    def __init__(self, run_id: str):
        self.run_id = run_id
        self.paid: dict[str, float] = {}
        self.total = 0.0

    def consume(self, events) -> float:
        r = 0.0
        for e in events:
            if not isinstance(e, BattleEvent):
                raise ContractError(f"not a BattleEvent: {e!r}")
            if not e.fight_id.startswith(self.run_id + ":"):
                raise ContractError(f"fight {e.fight_id} does not belong to run {self.run_id}")
            if e.fight_id in self.paid:
                raise ContractError(f"fight {e.fight_id} reported twice")
            v = fight_value(e)
            self.paid[e.fight_id] = v
            r += v
        if not math.isfinite(r):
            raise ContractError("nonfinite reward")
        self.total += r
        return r

    def summary(self) -> dict:
        return {"total": self.total, "fights_paid": sum(v > 0 for v in self.paid.values()),
                "fights_seen": len(self.paid)}
