"""rs1 typed records shared by the bridge client, frozen battle policy, strategy features, reward,
trajectory and learner. Worker replies are parsed and validated here: an unknown enum, identity or
schema fails closed (:class:`ContractError`).

Rulings (recorded in the implementation progress log):

* ``Candidate`` carries ``(id, kind, public, instance_ids)``. The engine action/target behind a
  candidate stays inside the bridge, which binds it to the run and decision and executes it
  atomically; the client only ever names the candidate id. ``instance_ids`` are bridge-owned deck
  instance ids the candidate refers to (selection/smith), never model features.
* ``PublicRunObservation`` adds ``screen`` (phase-specific public context) and ``map_meta``
  (act ordinal/region, current node, current boss icon) to the planned fields.
* ``BattleEvent`` adds ``fight_n``, ``floor``, ``entry_hp``, ``entered_terminal``, ``dummy_escaped``.
* ``StepReply`` adds ``status``, ``counters``, ``auto_actions``, ``battle`` and the raw reply.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from typing import Any

WORKER_PROTOCOL = "rs1/worker_protocol/v2"
REPLY_SCHEMA = "rs1/step_reply/v1"
OBSERVATION_SCHEMA = "rs1/public_run_observation/v2"
# The decision flow a worker exposes, a config trains under and a checkpoint was learned on. v2: reward
# gold/relic pickup, free-slot potions and card-offer opening are automatic bridge bookkeeping; only a full
# belt (one complete choice), a special card and each card offer (all cards + skip + native alternatives)
# are decisions, and a skip is final for its offer. The 2026-10-05 pilot ran v1 ("rs1/decision_flow/v1-menu",
# implicit then): its reward screen was a model menu. Neither flow's checkpoints/replay are valid for the other.
DECISION_FLOW = "rs1/decision_flow/v2-auto-reward"
LEGACY_DECISION_FLOW = "rs1/decision_flow/v1-menu"
EVENT_TABLE_SCHEMA = "rs1/event_page_options/v1"
REWARD_ID = "run-strategy-act-win-v1"

OWNERS = ("battle", "strategy", "none")
STATUSES = ("running", "complete", "unsupported", "error", "limit")
OUTCOME_KINDS = ("natural", "unsupported", "error", "limit")
ROOMS = ("normal", "event", "elite", "boss")
FIGHT_OUTCOMES = ("victory", "death", "event_escape")
PHASES = ("Combat", "CardReward", "Map", "Rest", "Shop", "RelicReward", "Complete", "Event", "Ancient",
          "TransformSelect", "Treasure", "CrystalSphere", "BundleSelect", "FakeMerchant")
CANDIDATE_KINDS = (
    "map_node", "card_take", "card_skip", "card_sacrifice", "card_reroll",
    "reward_gold", "reward_potion", "reward_relic", "reward_special_card", "reward_open_cards", "reward_leave",
    "potion_discard", "potion_use", "shop_card", "shop_relic", "shop_potion", "shop_remove", "shop_leave",
    "rest_heal", "rest_smith", "rest_clone", "rest_lift", "rest_dig", "rest_hatch", "rest_cook", "rest_kindle",
    "rest_leave", "proceed", "event_option", "ancient_relic", "select_card", "grid_card", "grid_done", "bundle",
    "treasure_open", "treasure_relic", "treasure_leave", "sphere_cell",
    # decision flow v2 (appended: the indices above are unchanged)
    "potion_replace", "potion_use_accept", "potion_skip_incoming",
)
# Bookkeeping kinds the v2 flow only executes automatically (reported in auto_actions): a strategy decision
# displaying one is a contract violation, never a learned choice. Kept above so no kind index is remapped.
AUTOMATIC_ONLY_KINDS = frozenset({"reward_gold", "reward_potion", "reward_relic", "reward_open_cards", "reward_leave",
                                  "treasure_open", "treasure_relic", "treasure_leave"})


class ContractError(RuntimeError):
    """A violation of the rs1 contract (stale decision, unknown identity, malformed reply...)."""


class UnsupportedStrategyState(ContractError):
    """A displayed public value the strategy encoder cannot represent faithfully (e.g. an enchantment or
    Tinker modifier outside the known engine enums): the run ends as a visible ``unsupported`` outcome,
    never a clipped or zero-filled embedding."""

    def __init__(self, kind: str, message: str):
        super().__init__(f"{kind}: {message}")
        self.kind = kind


def _need(cond: bool, msg: str) -> None:
    if not cond:
        raise ContractError(msg)


@dataclass(frozen=True)
class Candidate:
    id: str
    kind: str
    public: dict
    instance_ids: tuple[int, ...] = ()

    @staticmethod
    def parse(d: dict) -> "Candidate":
        _need(d.get("kind") in CANDIDATE_KINDS, f"unknown candidate kind {d.get('kind')!r} ({d.get('id')})")
        _need(d["kind"] not in AUTOMATIC_ONLY_KINDS,
              f"bookkeeping kind {d['kind']!r} ({d.get('id')}) displayed as a decision under {DECISION_FLOW}")
        _need(isinstance(d.get("id"), str) and d["id"], "candidate without id")
        pub = d.get("public") or {}
        inst = (int(pub["instance_id"]),) if "instance_id" in pub else ()
        return Candidate(d["id"], d["kind"], pub, inst)


@dataclass(frozen=True)
class PublicRunObservation:
    run_id: str
    decision_id: str
    phase: str
    scalars: dict
    deck: tuple
    relics: tuple
    potions: tuple
    map_nodes: tuple
    map_edges: tuple
    history: dict
    candidates: tuple[Candidate, ...]
    screen: dict = field(default_factory=dict)
    map_meta: dict = field(default_factory=dict)

    @staticmethod
    def parse(d: dict) -> "PublicRunObservation":
        _need(d.get("schema") == OBSERVATION_SCHEMA, f"observation schema {d.get('schema')!r}")
        _need(d.get("phase") in PHASES, f"unknown phase {d.get('phase')!r}")
        cands = tuple(Candidate.parse(c) for c in d["candidates"])
        _need(len(cands) >= 2, "a strategy decision needs at least two displayed candidates")
        _need(len({c.id for c in cands}) == len(cands), "duplicate candidate ids")
        m = d["map"]
        meta = {k: v for k, v in m.items() if k not in ("nodes", "edges")}
        n = len(m["nodes"])
        edges = tuple((int(a), int(b)) for a, b in m["edges"])
        _need(all(0 <= a < n and 0 <= b < n for a, b in edges), "map edge with a missing endpoint")
        return PublicRunObservation(
            run_id=d["run_id"], decision_id=d["decision_id"], phase=d["phase"], scalars=d["scalars"],
            deck=tuple(d["deck"]), relics=tuple(d["relics"]), potions=tuple(d["potions"]),
            map_nodes=tuple(m["nodes"]), map_edges=edges, history=d["history"], candidates=cands,
            screen=d.get("screen") or {}, map_meta=meta)

    def candidate(self, cid: str) -> Candidate:
        for c in self.candidates:
            if c.id == cid:
                return c
        raise ContractError(f"{self.decision_id}: candidate {cid} not displayed")


@dataclass(frozen=True)
class BattleObservation:
    run_id: str
    fight_id: str
    entry: dict  # fight_n, act_ordinal, room, entry_hp, entry_max_hp, floor, battle_decision, battle_steps
    raw: dict  # the complete original Session-shaped reply (obs/decision/status/reason/entry_hp/...)


@dataclass(frozen=True)
class BattleChoice:
    option_id: str  # typed id: kind:action:target[:potion_slot]
    ordinal: int
    decision_id: int  # the Session decision counter the choice answers


@dataclass(frozen=True)
class BattleEvent:
    fight_id: str
    act_ordinal: int
    room: str
    outcome: str
    hp_terminal: int
    fight_n: int = 0
    floor: int = 0
    entry_hp: int = 0
    entered_terminal: bool = False
    dummy_escaped: bool = False
    battle_decisions: int = 0
    # The original Session terminal reply written by the pinned writer on the retained combat state
    # (None only for a fight that ended during combat entry, when E never acted).
    terminal_reply: dict | None = field(default=None, compare=False, repr=False)

    @staticmethod
    def parse(d: dict) -> "BattleEvent":
        _need(d.get("closed") is True, f"battle event for an open fight {d.get('fight_id')}")
        _need(d.get("room") in ROOMS, f"unknown room {d.get('room')!r}")
        _need(d.get("outcome") in FIGHT_OUTCOMES, f"unknown fight outcome {d.get('outcome')!r}")
        _need(1 <= int(d["act_ordinal"]) <= 3, f"act ordinal {d['act_ordinal']}")
        tr = d.get("terminal_reply")
        _need((tr is None) == (int(d.get("battle_decisions", 0)) == 0),
              f"{d['fight_id']}: a fight E acted in must carry its terminal Session reply (and only then)")
        if tr is not None:
            _need(tr.get("reason") == "engine_terminal" and tr.get("status") in ("win", "loss", "terminal_other")
                  and tr.get("obs") is not None and tr["obs"]["legal"] == [], f"{d['fight_id']}: malformed terminal reply")
        return BattleEvent(d["fight_id"], int(d["act_ordinal"]), d["room"], d["outcome"], int(d["hp_terminal"]),
                           int(d["fight_n"]), int(d["floor"]), int(d["entry_hp"]), bool(d["entered_terminal"]),
                           bool(d.get("dummy_escaped", False)), int(d.get("battle_decisions", 0)), tr)


@dataclass(frozen=True)
class RunOutcome:
    run_id: str
    kind: str  # natural | unsupported | error | limit
    reason: str
    won: bool
    battles: tuple = ()
    detail: str | None = None
    hp: int = 0
    floor: int = 0
    act_ordinal: int = 0

    @property
    def natural(self) -> bool:
        return self.kind == "natural"

    @staticmethod
    def parse(run_id: str, d: dict) -> "RunOutcome":
        _need(d.get("kind") in OUTCOME_KINDS, f"unknown outcome kind {d.get('kind')!r}")
        return RunOutcome(run_id, d["kind"], d["reason"], bool(d["won"]), tuple(d.get("fights") or ()),
                          d.get("detail"), int(d.get("hp", 0)), int(d.get("floor", 0)), int(d.get("act_ordinal", 0)))


@dataclass(frozen=True)
class StrategyTransition:
    obs: Any  # StrategyObs (features) of the decision
    candidate_id: str
    reward: float
    next_obs: Any  # StrategyObs or None at a terminal
    terminal: bool


@dataclass(frozen=True)
class NStepTransition:
    obs: Any
    candidate_id: str
    reward: float
    next_obs: Any
    terminal: bool
    decisions: int  # how many decisions the return spans (1..n)
    run_id: str = ""
    discount: float = 1.0  # gamma ** decisions (1 under gamma1)


@dataclass(frozen=True)
class StepReply:
    run_id: str
    decision_id: str
    owner: str
    status: str
    observation: Any  # PublicRunObservation | BattleObservation | None
    battle_events: tuple[BattleEvent, ...]
    outcome: RunOutcome | None
    counters: dict
    auto_actions: tuple = ()
    raw: dict | None = None

    @staticmethod
    def parse(r: dict) -> "StepReply":
        _need(r.get("ok") is True, f"worker error: {r.get('type')}: {r.get('message')}")
        _need(r.get("schema") == REPLY_SCHEMA, f"reply schema {r.get('schema')!r}")
        _need(r.get("owner") in OWNERS, f"unknown owner {r.get('owner')!r}")
        _need(r.get("status") in STATUSES, f"unknown status {r.get('status')!r}")
        events = tuple(BattleEvent.parse(e) for e in r["battle_events"])
        obs = None
        if r["owner"] == "battle":
            b = r["battle"]
            _need(b["room"] in ROOMS, f"unknown room {b['room']!r}")
            obs = BattleObservation(r["run_id"], b["fight_id"], {k: v for k, v in b.items() if k != "raw"}, b["raw"])
        elif r["owner"] == "strategy":
            obs = PublicRunObservation.parse(r["strategy"])
            _need(obs.decision_id == r["decision_id"] and obs.run_id == r["run_id"], "observation/reply id mismatch")
        outcome = RunOutcome.parse(r["run_id"], r["outcome"]) if r.get("outcome") else None
        _need((r["status"] == "running") == (outcome is None), "outcome present iff the run ended")
        _need((r["status"] == "running") == (r["owner"] != "none"), "a running run must have an owner")
        return StepReply(r["run_id"], r["decision_id"], r["owner"], r["status"], obs, events, outcome,
                         r["counters"], tuple(r.get("auto_actions") or ()), r)


@dataclass(frozen=True)
class SeedManifest:
    base: int
    train: tuple[int, ...]
    validation: tuple[int, ...]
    diagnostic: tuple[int, ...]
    reserved_test: tuple[int, ...]

    def digest(self) -> str:
        return hashlib.sha256(json.dumps([self.base, self.train, self.validation, self.diagnostic,
                                          self.reserved_test]).encode()).hexdigest()
