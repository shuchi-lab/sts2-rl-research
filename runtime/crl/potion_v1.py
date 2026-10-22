"""potion_rl_v1 (OPT-IN): intentional potion use on the build08 engine, a victory-only potion bill.

Nothing here changes an existing contract. ``crl.features/v2``/``v3``, every older reward, context,
trainer and checkpoint keep their meaning; a worker reset without ``allow_potions`` is byte for byte
the original observation (``potion_rl_v1/evidence``).

Actions. :class:`PotionContext` resets every fight with ``allow_potions``: each manual potion use the
engine allows is a legal option ``use_potion`` carrying the PHYSICAL inventory slot (engine action
``hand + 1 + slot``), the potion identity in that slot and its target. Automatic-usage potions
(Fairy in a Bottle) are not manual actions in the game; the worker keeps them in ``excluded`` and
the engine spends them itself at a fatal hit (never charged, logged as ``auto_consumed``).

Features (``crl.features/v3-potion-v2``, :class:`PotionAdapter`): v3 unchanged, plus

* candidate potion identity per option (shared 16-wide potion embedding in the model);
* the source potion of an open potion selection screen (the bottle is already gone);
* a policy admissibility mask (v2): an outside-roster card OFFERED on a GeneratedCardToHand screen
  (from a potion or a card such as Splash) stays a legal option with its engine ordinal and raw row,
  but the policy never scores, explores or bootstraps through it; it declines via another
  candidate or the real skip. Owned foreign cards still raise ForeignCardState, and a screen
  with no admissible option is ``NoAdmissibleOption``;
* the actual potion capacity (worker ``potion_capacity``); capacity beyond the represented slot
  array is refused, never counted as full;
* :data:`POTION_GLOBAL_FEATURES`: act / room class, working Frond refill, inventory full, empty
  slots, final boss, lambda, the accumulated bill, charged uses, the charge a use would incur now,
  and the public round / extra-turn context (``RoundNumber``; an extra turn does not raise it).
  The worker's ``RoundNumber`` boundary (a v3 encoder limitation) is accepted ONLY here, because it
  is encoded. Every other boundary, foreign card and encoder limitation stays an explicit
  ``unsupported`` failure.

Reward (:class:`PotionReward`, id :data:`REWARD_ID`). For each successful intentional use, inspect the
inventory immediately BEFORE it: charge 0 if held == capacity, if the fight is the final (Act 3)
boss, or if Delicate Frond is owned and Sozu is not (Frond refills open slots before the next
combat; Sozu refuses every procure); otherwise ``potion_lambda``. The bill accumulates per fight
and is subtracted ONLY from a natural victory's terminal reward. Every intermediate reward is 0.
Death is the boss-act-v2 death reward with no bill; the 100-turn limit stays -2, terminal, no bill;
max_steps truncation stays 0 and bootstrapped. The uncapped formula can rank a low-HP win with
several charged uses BELOW death; that is the requested objective and is reported, not capped.
"""

from __future__ import annotations

import dataclasses
import gzip
import hashlib
import json
import math
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable

import numpy as np

from . import paths
from .env import ActItem, DONE_STATUSES, EnvConfig, Episode, Policy, Step, Transition
from .expansion_turn100_v1 import (ALLOWED_TURNS, BOSS_ACT_V2_LOSS_PROFILE, LOSS_PROFILES, TURN_LIMIT_STATUS,
                                   Turn100Context, Turn100TieredReward, summarize_turn100, turn100_env_config,
                                   turn_fields)
from .features import (Adapter, EncoderLimitation, ForeignCardState, OPTION_KINDS, UnsupportedState)
from .reward import episode_metrics
from .replay import REJECTED_STATUSES
from .vocab import slog
from .worker import WorkerError

# v2 (2026-10-03 follow-up): policy admissibility mask for outside-roster OFFERED generated cards,
# actual-capacity checks, explicit final-boss floor. Network widths are unchanged from v1.
POTION_FEATURE_SCHEMA = "crl.features/v3-potion-v2"
POTION_ACTIONS_SCHEMA = "crl.potion_actions/v2"  # worker hello potion_actions.schema
REWARD_ID = "combat-potion-turn100-boss-act-v2-victory-bill-v1"
REWARD_SCHEMA = "crl.reward_spec/potion-v1"
REWARD_CONTRACT_SCHEMA = "crl.potion_v1_reward_contract/v1"
DEFAULT_POTION_LAMBDA = 0.5

POTION_OPTION_KINDS = OPTION_KINDS + ("use_potion",)
ACTS = (1, 2, 3)
ROOMS = ("normal", "event", "elite", "boss")
POTION_GLOBAL_FEATURES = (
    "act_1", "act_2", "act_3", "room_normal", "room_event", "room_elite", "room_boss",  # 7
    "working_frond_refill", "inventory_full", "empty_slots",  # 3 -> the proposed 10
    "final_boss", "potion_lambda", "bill_accumulated", "charged_uses", "use_charge_now",  # bill
    "round_number", "extra_turns_taken", "in_extra_turn",  # round / extra-turn context
)
# Exemption reasons in reporting priority order (every applicable reason is recorded).
EXEMPT_REASONS = ("inventory_full", "final_boss", "delicate_frond_refill")
FROND_ENTRY, SOZU_ENTRY = "DELICATE_FROND", "SOZU"
# Potions with no in-combat case in PotionEffects.Apply (engine/whole_game_build08): a manual use
# would only empty the slot. All are other characters' pool potions plus the Automatic Fairy.
# tests/test_potion_v1.py re-derives this set from the engine source.
UNMODELLED_COMBAT_POTIONS = frozenset((
    "BoneBrew", "CosmicConcoction", "EssenceOfDarkness", "FairyInABottle", "FocusPotion", "GhostInAJar",
    "KingsCourage", "PoisonPotion", "PotionOfCapacity", "PotionOfDoom", "PotOfGhouls", "StarPotion",
))
UNSUPPORTED_KINDS = ("foreign_card", "no_admissible_option", "encoder_limitation", "other_unsupported")
# Selection kinds whose answer ACQUIRES the chosen generated card (Episode.SelectionPile "generated").
# CurseOfKnowledge is also "generated" but applies a power; it is not masked (a foreign one still fails).
ACQUIRING_GENERATED_KINDS = frozenset(("GeneratedCardToHand",))
# Every Act 3 boss in this corpus (train 51 / val / test) is at TotalFloor 48, a single-boss final act.
# Final-boss status is asserted ONLY for that state; another Act 3 boss floor is refused, not generalised.
FINAL_BOSS_ACT, FINAL_BOSS_FLOOR = 3, 48


class NoAdmissibleOption(UnsupportedState):
    """Every legal option of a generated screen offers an outside-roster card and no legal skip exists."""


def unsupported_kind(exc: BaseException) -> str:
    if isinstance(exc, NoAdmissibleOption):
        return "no_admissible_option"
    if isinstance(exc, ForeignCardState):
        return "foreign_card"
    if isinstance(exc, EncoderLimitation):
        return "encoder_limitation"
    return "other_unsupported"


# ---------------------------------------------------------------------------
# Reward
# ---------------------------------------------------------------------------

def check_lambda(x) -> float:
    if isinstance(x, bool) or not isinstance(x, (int, float)) or not math.isfinite(x) or x < 0:
        raise ValueError(f"potion_lambda must be a finite nonnegative number, got {x!r}")
    return float(x)


def use_charge(potion_lambda: float, *, held: int, capacity: int, act: int, room: str,
               frond: bool, sozu: bool) -> tuple[float, list[str]]:
    """(charge, exemption reasons) for one intentional use, from the inventory BEFORE it."""
    if not 0 < held <= capacity:
        raise ValueError(f"a use needs 0 < held <= capacity, got held {held} capacity {capacity}")
    if act not in ACTS or room not in ROOMS:
        raise ValueError(f"unsupported combat class act={act} room={room}")
    reasons = []
    if held == capacity:
        reasons.append("inventory_full")
    if act == 3 and room == "boss":
        reasons.append("final_boss")
    if frond and not sozu:
        reasons.append("delicate_frond_refill")
    return (0.0 if reasons else float(potion_lambda)), reasons


@dataclass(frozen=True)
class PotionReward:
    potion_lambda: float = DEFAULT_POTION_LAMBDA
    allowed_turns: int = ALLOWED_TURNS
    loss_profile: str = BOSS_ACT_V2_LOSS_PROFILE

    def __post_init__(self):
        check_lambda(self.potion_lambda)
        if self.loss_profile not in LOSS_PROFILES:
            raise ValueError(f"loss_profile must be one of {LOSS_PROFILES}")
        self.base  # validates the turn budget / profile

    @property
    def base(self) -> Turn100TieredReward:
        """Base terminal reward: turn100 with the loss profile (uniform profile: death -1)."""
        return Turn100TieredReward(1.0, self.allowed_turns, loss_profile=self.loss_profile)

    def charge(self, **kw) -> tuple[float, list[str]]:
        return use_charge(self.potion_lambda, **kw)

    def terminal(self, outcome: str, *, act, room, entry_hp, hp_terminal, max_hp_terminal=None,
                 bill: float = 0.0) -> dict:
        """Components of a finished fight: base, bill, the realized penalty and the final reward."""
        if not math.isfinite(bill) or bill < 0:
            raise ValueError(f"bill must be finite and nonnegative, got {bill}")
        if outcome == "truncated":  # max_steps: 0 and bootstrapped, no bill
            base = 0.0
        else:
            base = self.base.terminal_reward(outcome, act=act, room_type=room, entry_hp=entry_hp,
                                             hp_terminal=hp_terminal, max_hp_terminal=max_hp_terminal)
        realized = float(bill) if outcome == "win" else 0.0
        return {"outcome": outcome, "base_reward": float(base), "contingent_bill": float(bill),
                "realized_penalty": realized, "final_reward": float(base) - realized}

    @property
    def reward_id(self) -> str:
        if self.loss_profile == BOSS_ACT_V2_LOSS_PROFILE and self.allowed_turns == ALLOWED_TURNS:
            return REWARD_ID
        return f"combat-potion-turn{self.allowed_turns}-{self.loss_profile}-victory-bill-v1"

    def describe(self) -> dict:
        return {
            "schema": REWARD_SCHEMA, "reward_id": self.reward_id, "potion_lambda": float(self.potion_lambda),
            "base": self.base.describe(),
            "use_charge": {
                "when": "each successful intentional use (a legal use_potion step without an engine exception)",
                "inventory": "inspected immediately BEFORE the use: held = nonzero usable slots, capacity = "
                             "min(MaxPotionSlots, slot array) (the observation's potions list)",
                "rules": {"held == capacity": 0.0, "final boss (Act 3 boss)": 0.0,
                          "Delicate Frond owned and Sozu not owned": 0.0, "otherwise": "potion_lambda"},
                "not_charged": "Automatic use (Fairy in a Bottle, spent by the engine); selection answers of a "
                               "potion's own screen (the use was charged once, at the use)",
                "relics_without_discount": "other potion relics only change real mechanics/capacity; no generic "
                                           "discount is invented",
            },
            "bill": "sum of the fight's charges",
            "win": "base win reward - bill (unclipped; can fall below the death reward)",
            "loss": "base death reward (loss profile), no bill", "turn_limit": "base -2, terminal, no bill",
            "max_steps": "0, bootstrapped, no bill", "intermediate": 0.0,
            "failures": "error/unsupported/terminal_other never rewarded or stored",
            "implementation_module": "crl.potion_v1",
            "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        }


# ---------------------------------------------------------------------------
# Per-fight ledger and public context
# ---------------------------------------------------------------------------

@dataclass
class PotionLedger:
    potion_lambda: float
    uses: list[dict] = field(default_factory=list)  # successful intentional uses
    failed_uses: list[dict] = field(default_factory=list)  # chosen uses whose step raised in the engine
    auto_consumed: list[dict] = field(default_factory=list)  # potions that left a slot without a use step
    bill: float = 0.0

    @property
    def charged_uses(self) -> int:
        return sum(u["charge"] > 0 for u in self.uses)

    def record_use(self, rec: dict) -> None:
        self.uses.append(rec)
        self.bill += rec["charge"]

    def summary(self) -> dict:
        ex = Counter(u["exempt_reasons"][0] for u in self.uses if u["exempt_reasons"])
        ex_any = Counter(r for u in self.uses for r in u["exempt_reasons"])
        return {"potion_lambda": self.potion_lambda, "uses": len(self.uses), "charged_uses": self.charged_uses,
                "exempt_uses": len(self.uses) - self.charged_uses,
                "exempt_by_primary_reason": {r: ex[r] for r in EXEMPT_REASONS},
                "exempt_reason_mentions": {r: ex_any[r] for r in EXEMPT_REASONS},
                "contingent_bill": self.bill, "failed_uses": len(self.failed_uses),
                "auto_consumed": len(self.auto_consumed)}


class PotionEnvState:
    """One fight's ledger, class, and the turn/round tracker for the extra-turn flag."""

    def __init__(self, potion_lambda: float, act: int, room: str):
        self.ledger = PotionLedger(potion_lambda)
        self.act, self.room = act, room
        self._turn = None
        self._lag = 0
        self._prev_turn_lag = 0
        self.unsupported_kind: str | None = None
        self.last_potion: str | None = None
        # One record per decision whose generated screen offered outside-roster cards (policy-masked).
        self.foreign_offers: list[dict] = []

    def round_context(self, obs: dict) -> tuple[int, int, bool]:
        """(round number, extra turns taken, this turn is an extra turn), checked for consistency."""
        rnd, turn = obs["round_number"], obs["turn"]
        lag = turn + 1 - rnd
        if rnd < 1 or lag < 0:
            raise UnsupportedState(f"round {rnd} at raw turn {turn}: more rounds than turns")
        if self._turn is None or turn != self._turn:
            if self._turn is not None and turn < self._turn:
                raise UnsupportedState(f"turn counter went back {self._turn} -> {turn}")
            self._prev_turn_lag = 0 if self._turn is None else self._lag
            self._turn, self._lag = turn, lag
        elif lag != self._lag:
            raise UnsupportedState(f"round number changed inside raw turn {turn}")
        return rnd, lag, lag > self._prev_turn_lag


def inventory(obs: dict) -> tuple[int, int]:
    """(held, ACTUAL capacity). The worker lists min(MaxPotionSlots, slot array) slots; a capacity the
    list cannot represent (e.g. 7 on the 6-wide array) is refused rather than counted as full."""
    pots = obs["potions"]
    cap = obs.get("potion_capacity")
    if not isinstance(cap, int) or obs.get("potion_slot_array") is None:
        raise UnsupportedState("observation lacks the potion_actions/v2 capacity fields; rebuild the worker")
    if cap > obs["potion_slot_array"] or len(pots) != cap:
        raise UnsupportedState(f"potion capacity {cap} beyond the represented inventory ({len(pots)} listed, "
                               f"slot array {obs['potion_slot_array']})")
    return sum(p != 0 for p in pots), cap


# ---------------------------------------------------------------------------
# Features
# ---------------------------------------------------------------------------

class PotionAdapter(Adapter):
    """crl.features/v3-potion-v2: v3 + potion candidates, source potion, potion/bill/round context."""

    potion_options = True

    def __init__(self, vocab, hello: dict, *, frond_relic_id: int, sozu_relic_id: int):
        super().__init__(vocab, hello, schema="v3")
        pa = hello.get("potion_actions")
        if not pa or pa.get("schema") != POTION_ACTIONS_SCHEMA:
            raise UnsupportedState(f"worker has no {POTION_ACTIONS_SCHEMA} potion contract; rebuild the worker")
        self.potion_contract = {k: v for k, v in pa.items() if k != "potions"}
        self.potion_defs = {int(i): {"name": n, "usage": u, "targets_enemy": bool(te)} for i, n, u, te in pa["potions"]}
        missing = sorted(set(self.potion_defs) - set(vocab.potion_index))
        if missing:
            raise UnsupportedState(f"engine potions {missing} outside the potion vocabulary")
        names = {d["name"]: i for i, d in self.potion_defs.items()}
        unknown = sorted(UNMODELLED_COMBAT_POTIONS - set(names))
        if unknown:
            raise UnsupportedState(f"unmodelled potion list names unknown potions {unknown}")
        self.unmodelled_ids = frozenset(names[n] for n in UNMODELLED_COMBAT_POTIONS)
        self.automatic_ids = frozenset(i for i, d in self.potion_defs.items() if d["usage"] == "Automatic")
        self.frond_relic_id, self.sozu_relic_id = int(frond_relic_id), int(sozu_relic_id)
        self.option_kinds = POTION_OPTION_KINDS
        self.encoded_boundaries = frozenset(("RoundNumber",))

    @property
    def dims(self) -> dict:
        d = super().dims
        d["global_extra"] += len(POTION_GLOBAL_FEATURES)
        return d

    def encode(self, obs: dict, entry_hp: int, pctx: PotionEnvState | None = None) -> "Obs":  # noqa: F821
        if pctx is None:
            raise ValueError("crl.features/v3-potion-v2 needs the fight's PotionEnvState")
        if obs.get("allow_potions") is not True or "round_number" not in obs:
            raise UnsupportedState("observation is not from a potion-mode reset (allow_potions)")
        held, cap = inventory(obs)
        sel = obs["selection"]
        self._sel_kind = sel["kind_name"] if sel else None
        self._blocked: list[dict] = []
        ob = super().encode(obs, entry_hp)
        v = self.v
        pots = obs["potions"]
        ctr = dict(zip(self.hello_counters, obs["player"]["counters"]))
        if cap != ctr["max_potion_slots"]:
            raise UnsupportedState(f"potion capacity {cap} != MaxPotionSlots counter {ctr['max_potion_slots']}")
        enemies_targetable = [ev["targetable"] for ev in obs["view"]["enemies"]]
        legal = obs["legal"]
        O = len(legal)
        opt_pot = np.zeros(O, dtype=np.int32)
        opt_slot = np.full(O, -1, dtype=np.int32)
        used_slots = set()
        for i, o in enumerate(legal):
            if o["kind"] != "use_potion":
                continue
            slot, pid = o.get("potion_slot"), o.get("potion_id")
            if not isinstance(slot, int) or not 0 <= slot < cap or pid == 0 or pots[slot] != pid:
                raise UnsupportedState(f"potion option slot {slot} id {pid} does not match usable inventory {pots}")
            if o["action"] != ob.n_hand + 1 + slot:
                raise UnsupportedState(f"potion option action {o['action']} != hand {ob.n_hand} + 1 + slot {slot}")
            d = self.potion_defs[pid]
            if pid in self.automatic_ids:
                raise UnsupportedState(f"automatic potion {d['name']} offered as a manual use")
            if pid in self.unmodelled_ids:
                raise UnsupportedState(f"potion {d['name']} has no in-combat engine effect; a use would only "
                                       "empty its slot")
            t = o["target"]
            if d["targets_enemy"]:
                if t < 0 and any(enemies_targetable):
                    raise UnsupportedState(f"targeted potion {d['name']} without a target")
                if t >= 0 and not enemies_targetable[t]:
                    raise UnsupportedState(f"potion target {t} is not targetable")
            elif t >= 0:
                raise UnsupportedState(f"untargeted potion {d['name']} with target {t}")
            opt_pot[i] = v.potion_index[pid]
            opt_slot[i] = slot
            used_slots.add(slot)
        admissible = np.ones(O, dtype=bool)
        if self._blocked:  # outside-roster OFFERED generated cards: legal, policy-inadmissible
            for b in self._blocked:
                admissible[b["o"]] = False
            if not admissible.any():
                raise NoAdmissibleOption(
                    f"every option of the {sel['kind_name']} screen offers an outside-roster card and no legal skip "
                    f"exists: {[(b['def_id'], b['entry']) for b in self._blocked]}")
            pctx.foreign_offers.append({
                "decision": None, "raw_turn": obs["turn"], "selection_kind": sel["kind_name"],
                "source_card_def_id": sel["source_card_def_id"], "source_potion_id": sel["source_potion_id"],
                "candidates": [{"o": o["o"], "kind": o["kind"], "action": o["action"],
                                "def_id": int(o["card"][0]) if o["kind"] == "select" else None,
                                "admissible": bool(admissible[o["o"]])} for o in legal],
                "excluded": self._blocked, "skip_available": any(o["kind"] == "skip_selection" for o in legal),
                "chosen": None})
        if sel is None:  # combat decision: every held manual potion must be a legal option
            manual = {s for s, p in enumerate(pots) if p != 0 and p not in self.automatic_ids}
            if manual != used_slots:
                raise UnsupportedState(f"held manual potions in slots {sorted(manual)} but legal uses for "
                                       f"{sorted(used_slots)}")
            auto_held = sum(p in self.automatic_ids for p in pots if p)
            if obs["excluded_potion_options"] < auto_held:
                raise UnsupportedState("an automatic potion is neither legal nor reported as excluded")
        elif used_slots:
            raise UnsupportedState("potion use offered during a selection screen")
        src = sel["source_potion_id"] if sel else 0
        if src and src not in v.potion_index:
            raise UnsupportedState(f"selection source potion {src} not in vocabulary")

        relics = {r for r, _ in obs["relics"]}
        frond, sozu = self.frond_relic_id in relics, self.sozu_relic_id in relics
        act, room = pctx.act, pctx.room
        final = act == 3 and room == "boss"
        lam = pctx.ledger.potion_lambda
        now = use_charge(lam, held=held, capacity=cap, act=act, room=room, frond=frond, sozu=sozu)[0] if held else 0.0
        rnd, lag, extra = pctx.round_context(obs)
        g = [float(act == a) for a in ACTS] + [float(room == r) for r in ROOMS]
        g += [float(frond and not sozu), float(cap > 0 and held == cap), float(slog(cap - held)),
              float(final), lam, float(pctx.ledger.bill), float(slog(pctx.ledger.charged_uses)), now,
              float(slog(rnd)), float(slog(lag)), float(extra)]
        glob = np.concatenate([ob.glob, np.asarray(g, dtype=np.float32)])
        return dataclasses.replace(ob, glob=glob, opt_potion_idx=opt_pot, opt_potion_slot=opt_slot,
                                   sel_source_potion=int(v.potion_index[src]) if src else 0,
                                   opt_admissible=admissible)

    def _select_candidate_row(self, o: dict):
        """An outside-roster card OFFERED on an acquiring generated screen is policy-inadmissible: its
        features are padding (it is never scored), its raw row stays in the observation and the log.
        Anything else -- an owned/pile card, a non-acquiring screen -- keeps ``card_row``'s
        ForeignCardState."""
        raw = o["card"]
        def_id = int(raw[0])
        if (o.get("pile") == "generated" and self._sel_kind in ACQUIRING_GENERATED_KINDS and def_id > 0
                and def_id not in self.v.card_index):
            from .features import EXPECTED_CARD_FIELDS, F
            if len(raw) != len(EXPECTED_CARD_FIELDS) or int(raw[F["Enchantment"]]) not in self.ench_values:
                raise UnsupportedState(f"malformed offered card row {raw}")
            self._blocked.append({"o": o["o"], "action": o["action"], "def_id": def_id,
                                  "entry": self.card_entries.get(def_id, "?"), "upgraded": bool(raw[F["Upgraded"]]),
                                  "raw_card": list(raw)})
            return 0, 0, np.zeros(len(self.card_features), dtype=np.float32)
        return self.card_row(raw)

    @property
    def hello_counters(self) -> tuple:
        from .features import EXPECTED_COUNTERS
        return EXPECTED_COUNTERS

    def describe(self) -> dict:
        d = super().describe()
        d.update(
            schema=POTION_FEATURE_SCHEMA, base_schema=d["schema"],
            global_features=d["global_features"] + list(POTION_GLOBAL_FEATURES),
            potion_global_features=list(POTION_GLOBAL_FEATURES),
            option_kinds=list(POTION_OPTION_KINDS), dims=self.dims,
            option_potion="candidate potion identity (vocab potion index) and physical slot per use_potion option",
            selection_source_potion="PendingSelection.SourcePotionId as a potion identity (0 = none)",
            encoded_boundaries=sorted(self.encoded_boundaries),
            potion_contract=self.potion_contract,
            unmodelled_combat_potions=sorted(UNMODELLED_COMBAT_POTIONS),
            frond_relic_id=self.frond_relic_id, sozu_relic_id=self.sozu_relic_id,
            rejected=d["rejected"] + ["potion option/inventory mismatch", "unmodelled potion use",
                                      "automatic potion offered manually",
                                      "potion capacity beyond the represented slot array",
                                      "generated screen whose every option is an outside-roster offer (no skip)"],
            policy_admissibility={
                "field": "opt_admissible [O] bool, applied as model mask & opt_mask",
                "inadmissible": "a legal select option of a " + "/".join(sorted(ACQUIRING_GENERATED_KINDS))
                                + " screen whose OFFERED card is outside the card vocabulary (the 214 roster)",
                "game_legality": "unchanged: the option stays in the legal list with its engine ordinal; the raw "
                                 "card row is kept in the observation and the per-fight foreign_offers log",
                "inadmissible_features": "card index/enchantment 0 and zero features (never scored)",
                "enforced_by": ["QNet mask (greedy, Double DQN a*, reference and fast learner)",
                                "epsilon/random exploration", "heuristic and mixed warm-up",
                                "rollout boundary check", "replay validation", "learner action check"],
                "still_failing": ["outside-roster card in a pile, hand, queue or as a screen's source (owned)",
                                  "non-acquiring screens (CurseOfKnowledge) with an outside-roster card",
                                  "all options inadmissible and no legal skip (NoAdmissibleOption)"],
            },
            capacity="potion_capacity = MaxPotionSlots, must equal the listed usable slots and fit the slot array",
        )
        return d


# ---------------------------------------------------------------------------
# Context, decode and rollout
# ---------------------------------------------------------------------------

@dataclass
class PotionEpisode(Episode):
    potion_ledger: PotionLedger | None = None
    unsupported_kind: str | None = None
    last_potion: str | None = None
    foreign_offers: list | None = None

    def summary(self) -> dict:
        out = super().summary()
        out["potion"] = self.potion_ledger.summary() if self.potion_ledger else None
        out["unsupported_kind"] = self.unsupported_kind
        out["last_potion_used"] = self.last_potion
        fo = self.foreign_offers or []
        out["foreign_offers"] = {"screens": len(fo), "excluded_options": sum(len(x["excluded"]) for x in fo),
                                 "excluded_def_ids": sorted({b["def_id"] for x in fo for b in x["excluded"]}),
                                 "chosen_kinds": dict(Counter(x["chosen"]["kind"] for x in fo if x["chosen"]))}
        return out


class PotionContext(Turn100Context):
    """Turn100 context with potion-mode resets, the potion adapter and the victory-only bill."""

    def __init__(self, templates, cfg: EnvConfig | None = None, *, potion_lambda: float = DEFAULT_POTION_LAMBDA,
                 classes: dict | None = None, catalog: dict | None = None, allowed_turns: int = ALLOWED_TURNS,
                 loss_profile: str = BOSS_ACT_V2_LOSS_PROFILE):
        self.potion = PotionReward(potion_lambda, allowed_turns, loss_profile)
        super().__init__(templates, cfg or turn100_env_config(allowed_turns), loss_lambda=1.0, classes=classes,
                         catalog=catalog, allowed_turns=allowed_turns, loss_profile=loss_profile)
        try:
            for t in self.templates.values():
                act, room = self.classify(t["template_id"])
                floor = t["engine_input"].get("TotalFloor")
                if act == FINAL_BOSS_ACT and room == "boss" and floor != FINAL_BOSS_FLOOR:
                    raise ValueError(f"{t['template_id']}: Act 3 boss at TotalFloor {floor}; final-boss status is "
                                     f"established only for floor {FINAL_BOSS_FLOOR} in this corpus")
            by_entry = {r["Entry"]: r["Id"] for r in self.catalog["relics"]}
            self.frond_relic_id, self.sozu_relic_id = by_entry[FROND_ENTRY], by_entry[SOZU_ENTRY]
            self.adapter = PotionAdapter(self.vocab, self.worker.hello, frond_relic_id=self.frond_relic_id,
                                         sozu_relic_id=self.sozu_relic_id)
        except BaseException:
            self.worker.close()
            raise

    @property
    def potion_lambda(self) -> float:
        return self.potion.potion_lambda

    def reward_contract(self) -> dict:
        return {
            "schema": REWARD_CONTRACT_SCHEMA, "training_reward": self.potion.reward_id,
            "potion_lambda": float(self.potion.potion_lambda), "loss_profile": self.turn100.loss_profile,
            "allowed_turns": self.turn100.allowed_turns, "worker_max_turns": self.turn100.worker_max_turns,
            "base_contract": {**Turn100Context.reward_contract(self),
                              "demonstrations": "not used by potion_v1 (see below)"},
            "relabel": "fresh potion-mode episodes only: terminal win = base - bill, loss = base, turn_limit = -2 "
                       "terminal, max_steps = 0 bootstrapped; before replay insertion",
            "demonstrations": "none: recorded potion-disabled demonstrations are not admitted",
            "unsupported": "never stored; counted by kind (foreign_card / encoder_limitation / other)",
        }

    def provenance(self) -> dict:
        out = super().provenance()
        out["reward"] = self.potion.describe()
        out["reward_contract"] = self.reward_contract()
        out["potion_actions"] = {"allow_potions": True, **self.adapter.potion_contract}
        out["final_boss_contract"] = {"act": FINAL_BOSS_ACT, "total_floor": FINAL_BOSS_FLOOR,
                                      "scope": "single Act 3 boss per run (this corpus, ascension 1-2); other Act 3 "
                                               "boss floors are refused at context construction"}
        return out

    def episode_state(self, template_id: str) -> PotionEnvState:
        act, room = self.classify(template_id)
        return PotionEnvState(self.potion.potion_lambda, act, room)

    def components(self, ep: PotionEpisode) -> dict | None:
        """Reward components of a finished episode; None for failures."""
        o = self.outcome(ep)
        if o not in ("win", "loss", TURN_LIMIT_STATUS, "truncated"):
            return None
        act, room = self.classify(ep.template_id)
        m = ep.metrics
        bill = ep.potion_ledger.bill if ep.potion_ledger else 0.0
        out = self.potion.terminal(o, act=act, room=room, entry_hp=m.entry_hp, hp_terminal=m.hp_terminal,
                                   max_hp_terminal=m.max_hp_terminal, bill=bill)
        if ep.potion_ledger:
            out.update({k: v for k, v in ep.potion_ledger.summary().items() if k != "potion_lambda"})
        if o == "win":
            death = self.potion.terminal("loss", act=act, room=room, entry_hp=m.entry_hp, hp_terminal=0)
            out["below_death_reward"] = out["final_reward"] < death["final_reward"]
        return out

    def tiered_reward(self, ep: Episode) -> float | None:
        c = self.components(ep)
        return None if c is None else c["final_reward"]

    def death_reward(self, template_id: str) -> float:
        act, room = self.classify(template_id)
        return self.potion.base.terminal_reward("loss", act=act, room_type=room, entry_hp=1, hp_terminal=0)


def decode_potion(ctx: PotionContext, r: dict, pstate: PotionEnvState) -> Step:
    """crl.env.decode for potion mode: the PotionAdapter with the fight's ledger/round context."""
    status = r["status"]
    obs_raw = r.get("obs")
    turn = obs_raw["turn"] if obs_raw else 0
    err = r.get("error")
    obs = None
    if status in ("running", "truncated") and obs_raw is not None:
        try:
            obs = ctx.adapter.encode(obs_raw, r["entry_hp"], pstate)
            if pstate.foreign_offers and pstate.foreign_offers[-1]["decision"] is None:
                pstate.foreign_offers[-1]["decision"] = r["decision"]
        except UnsupportedState as e:
            status, err = "unsupported", f"{type(e).__name__}: {e}"
            pstate.unsupported_kind = unsupported_kind(e)
    if status != "running" and status != "error" and r.get("foreign_rng_streams", 0):
        status, err = "error", f"foreign RNG streams {r['foreign_rng_streams']}"
    if r.get("purity_violations", 0):
        status, err = "error", f"observer purity violations {r['purity_violations']}"
    st = Step(raw=r, status=status, obs=obs, done=status in DONE_STATUSES, truncated=status == "truncated",
              turn=turn, error=err)
    if status != "running":
        p = obs_raw["player"] if obs_raw else {"hp": 0, "max_hp": r["entry_max_hp"]}
        relics = [x[0] for x in obs_raw["relics"]] if obs_raw else []
        st.metrics = episode_metrics(ctx.reward, status, r["entry_hp"], r["entry_max_hp"], p["hp"], p["max_hp"], relics)
    return st


def account_step(ctx: PotionContext, pstate: PotionEnvState, prev_raw: dict, option: int, r: dict) -> None:
    """Ledger bookkeeping for one executed step: the chosen use (charged from the inventory BEFORE
    it) and any potion that left a slot without being the chosen one (auto consumption)."""
    obs = prev_raw["obs"]
    o = obs["legal"][option]
    new = r.get("obs")
    chosen_slot = None
    if o["kind"] == "use_potion":
        held, cap = inventory(obs)
        relics = {x for x, _ in obs["relics"]}
        charge, reasons = ctx.potion.charge(held=held, capacity=cap, act=pstate.act, room=pstate.room,
                                            frond=ctx.frond_relic_id in relics, sozu=ctx.sozu_relic_id in relics)
        name = ctx.adapter.potion_defs[o["potion_id"]]["name"]
        rec = {"decision": prev_raw["decision"], "raw_turn": obs["turn"], "slot": o["potion_slot"],
               "potion_id": o["potion_id"], "potion": name, "target": o["target"], "held_before": held,
               "capacity": cap, "charge": charge, "exempt_reasons": reasons,
               "bill_before": pstate.ledger.bill}
        if r["status"] == "error" and r.get("reason") == "engine_exception":
            pstate.ledger.failed_uses.append({**rec, "charge": 0.0, "error": r.get("error")})
        else:
            pstate.ledger.record_use(rec)
            pstate.last_potion = name
        chosen_slot = o["potion_slot"]
    if new is not None:
        for slot, (a, b) in enumerate(zip(obs["potions"], new["potions"])):
            if a != 0 and b != a and slot != chosen_slot:
                pstate.ledger.auto_consumed.append({
                    "decision": prev_raw["decision"], "raw_turn": obs["turn"], "slot": slot, "potion_id": a,
                    "potion": ctx.adapter.potion_defs[a]["name"], "replaced_by": b,
                    "usage": ctx.adapter.potion_defs[a]["usage"], "during": o["kind"]})


class PotionRollout:
    """crl.env.Rollout for potion mode: per-fight ledger, terminal rows unrewarded until relabel."""

    def __init__(self, ctx: PotionContext, n_parallel: int = 16):
        self.ctx, self.n = ctx, n_parallel

    def run(self, jobs: Iterable[tuple[str, int]], policy: Policy, *, policy_seed: int = 0,
            keep_transitions: bool = False, trace: bool = False,
            on_episode: Callable[[PotionEpisode], None] | None = None,
            on_tick: Callable[[int], None] | None = None) -> list[PotionEpisode]:
        ctx, cfg = self.ctx, self.ctx.cfg
        jobs_iter = iter(jobs)
        slots: dict[int, dict] = {}
        done_eps: list[PotionEpisode] = []

        def start(slot: int) -> bool:
            while True:
                try:
                    tid, seed = next(jobs_iter)
                except StopIteration:
                    return False
                r = ctx.worker.reset(slot, tid, seed, max_steps=cfg.max_steps, max_turns=cfg.max_turns,
                                     rng_mode=cfg.rng_mode, purity_check=cfg.purity_check, allow_potions=True)
                ps = ctx.episode_state(tid)
                st = decode_potion(ctx, r, ps)
                rng = np.random.default_rng(paths.derive_seed(policy_seed, f"policy|{tid}", seed))
                slots[slot] = {"tid": tid, "seed": seed, "rng": rng, "step": st, "trans": [], "oor": 0,
                               "ps": ps, "trace": [] if trace else None}
                if st.ended:
                    finish(slot, st)
                    continue
                return True

        def finish(slot: int, st: Step) -> None:
            s = slots.pop(slot)
            ep = PotionEpisode(
                template_id=s["tid"], env_seed=s["seed"], status=st.status, reason=st.raw.get("reason"),
                error=st.error, metrics=st.metrics, steps=st.raw["steps"], turns=st.turn,
                transitions=s["trans"], out_of_roster_card_states=s["oor"], trace=s["trace"],
                potion_ledger=s["ps"].ledger, unsupported_kind=s["ps"].unsupported_kind if st.status == "unsupported" else None,
                last_potion=s["ps"].last_potion, foreign_offers=s["ps"].foreign_offers,
            )
            if on_episode:
                on_episode(ep)
            if not keep_transitions:
                ep.transitions = []
            done_eps.append(ep)

        for slot in range(self.n):
            if not start(slot):
                break
        while slots:
            items = [ActItem(k, s["step"].obs, s["step"].raw, s["rng"]) for k, s in slots.items()]
            choices = policy(items)
            req = []
            for it, a in zip(items, choices):
                if not 0 <= a < it.obs.n_options:
                    raise ValueError(f"policy chose {a} of {it.obs.n_options} options")
                if it.obs.opt_admissible is not None and not it.obs.opt_admissible[a]:
                    raise ValueError(f"policy chose legal but inadmissible option {a} "
                                     f"({it.raw['obs']['legal'][a]}) in {slots[it.slot]['tid']}")
                req.append((it.slot, it.raw["decision"], int(a)))
            results = ctx.worker.step_many(req)
            for (slot, _, a), r in zip(req, results):
                s = slots[slot]
                prev: Step = s["step"]
                fo = s["ps"].foreign_offers
                if fo and fo[-1]["chosen"] is None and fo[-1]["decision"] == prev.raw["decision"]:
                    o = prev.raw["obs"]["legal"][a]
                    fo[-1]["chosen"] = {"o": a, "kind": o["kind"], "action": o["action"],
                                        "def_id": int(o["card"][0]) if o["kind"] == "select" else None}
                account_step(ctx, s["ps"], prev.raw, a, r)
                st = decode_potion(ctx, r, s["ps"])
                if s["trace"] is not None:
                    o = prev.raw["obs"]["legal"][a]
                    p = prev.raw["obs"]["player"]
                    s["trace"].append([prev.turn, o["kind"], o["action"], o["target"], len(prev.raw["obs"]["legal"]),
                                       p["hp"], p["block"], p["energy"]])
                s["oor"] += int(prev.obs.out_of_roster_cards > 0)
                if keep_transitions or on_episode:
                    s["trans"].append(Transition(
                        obs=prev.obs, action=a, reward=0.0,  # terminal value set by PotionContext.relabel
                        next_obs=None if st.done else st.obs, done=st.done,
                        delta_turn=max(0, st.turn - prev.turn) if st.obs is not None or st.done else 0,
                    ))
                s["step"] = st
                if st.ended:
                    finish(slot, st)
                    start(slot)
            if on_tick:
                on_tick(len(req))
        return done_eps


# ---------------------------------------------------------------------------
# Policies and summaries
# ---------------------------------------------------------------------------

class MixedBehaviour:
    """Warm-up behaviour on the current engine: per decision, uniform legal (hierarchical, potions
    included) with probability ``p_random``, else the fixed heuristic (which never drinks)."""

    def __init__(self, heuristic, p_random: float = 0.5):
        from .policies import _hierarchical
        if not 0 <= p_random <= 1:
            raise ValueError("p_random must be in [0, 1]")
        self.h, self.p, self._rand = heuristic, float(p_random), _hierarchical
        self.name = f"mixed(heuristic/v1, uniform_legal p={self.p})"

    def __call__(self, items: list[ActItem]) -> list[int]:
        from .policies import admissible
        out = []
        for it in items:
            allowed = admissible(it)
            if it.rng.random() < self.p:
                out.append(self._rand(it.raw["obs"]["legal"], it.rng, allowed))
            else:
                out.append(self.h.choose(it.raw["obs"], allowed))
        return out


def potion_records(eps: list[PotionEpisode], ctx: PotionContext) -> list[dict]:
    """One flat record per episode (outcome, base/bill/final, uses with reasons, failures)."""
    out = []
    for e in eps:
        raw_status = e.status
        try:
            outcome = ctx.outcome(e)
            problem = None
        except ValueError as exc:
            outcome, problem = "invalid", str(exc)
        comp = ctx.components(e) if outcome in ("win", "loss", TURN_LIMIT_STATUS, "truncated") else None
        act, room = ctx.classify(e.template_id)
        out.append({
            "template_id": e.template_id, "env_seed": e.env_seed, "act": act, "room_type": room,
            "raw_status": raw_status, "outcome": outcome, "reason": e.reason, "error": e.error or problem,
            "unsupported_kind": e.unsupported_kind, "last_potion_used": e.last_potion, "steps": e.steps,
            **turn_fields(e, raw_status, ctx.cfg.max_turns),
            "entry_hp": e.metrics.entry_hp if e.metrics else None,
            "hp_terminal": e.metrics.hp_terminal if e.metrics else None,
            "reward": comp, "potion_uses": e.potion_ledger.uses if e.potion_ledger else [],
            "auto_consumed": e.potion_ledger.auto_consumed if e.potion_ledger else [],
            "failed_uses": e.potion_ledger.failed_uses if e.potion_ledger else [],
            "foreign_offers": e.foreign_offers or [],
        })
    return out


def _mean(xs) -> float | None:
    xs = list(xs)
    return float(np.mean(xs)) if xs else None


def summarize_potion(eps: list[PotionEpisode], ctx: PotionContext, selection=None, seed: int = 0) -> dict:
    """summarize_turn100 (whose objective is now the potion final reward) plus base/bill/use accounting,
    unsupported counts by kind, and the two validation scores."""
    out = summarize_turn100(eps, ctx, selection, seed)  # marks turn_limit outcomes
    out["potion_reward"] = {**out.pop("turn100_reward"), "reward_id": ctx.potion.reward_id,
                            "potion_lambda": ctx.potion.potion_lambda}
    out["potion_groups"] = out.pop("turn100_groups")
    comps = [(e, ctx.components(e)) for e in eps]
    scored = [(e, c) for e, c in comps if c is not None]
    wins = [c for _, c in scored if c["outcome"] == "win"]
    uses = [u for e in eps if e.potion_ledger for u in e.potion_ledger.uses]
    unsup = Counter(e.unsupported_kind or "other_unsupported" for e in eps if e.status == "unsupported")
    hard = Counter(e.status for e in eps if e.status in ("error", "terminal_other"))
    death_or_score = [c["final_reward"] if c else ctx.death_reward(e.template_id) for e, c in comps
                      if c is not None or e.status == "unsupported"]
    out["potion"] = {
        "base_reward_mean": _mean(c["base_reward"] for _, c in scored),
        "final_reward_mean": _mean(c["final_reward"] for _, c in scored),
        "contingent_bill_mean_all": _mean(c["contingent_bill"] for _, c in scored),
        "realized_penalty_sum": float(sum(c["realized_penalty"] for _, c in scored)),
        "realized_penalty_mean_wins": _mean(c["realized_penalty"] for c in wins),
        "wins_with_penalty": sum(c["realized_penalty"] > 0 for c in wins),
        "wins_below_death_reward": sum(bool(c.get("below_death_reward")) for c in wins),
        "uses": len(uses), "charged_uses": sum(u["charge"] > 0 for u in uses),
        "exempt_by_primary_reason": dict(Counter(u["exempt_reasons"][0] for u in uses if u["exempt_reasons"])),
        "exempt_reason_mentions": dict(Counter(r for u in uses for r in u["exempt_reasons"])),
        "uses_by_potion": dict(Counter(u["potion"] for u in uses).most_common()),
        "episodes_with_use": sum(bool(e.potion_ledger and e.potion_ledger.uses) for e in eps),
        "auto_consumed": dict(Counter(a["potion"] for e in eps if e.potion_ledger for a in e.potion_ledger.auto_consumed)),
        "failed_uses": sum(len(e.potion_ledger.failed_uses) for e in eps if e.potion_ledger),
        "unsupported_by_kind": {k: unsup[k] for k in UNSUPPORTED_KINDS},
        "unsupported": sum(unsup.values()),
        "foreign_offer_screens": sum(len(e.foreign_offers or []) for e in eps),
        "foreign_offer_fights": sum(bool(e.foreign_offers) for e in eps),
        "foreign_offer_excluded_options": sum(len(x["excluded"]) for e in eps for x in (e.foreign_offers or [])),
        "foreign_offer_excluded_ids": dict(Counter(f"{b['def_id']}:{b['entry']}" for e in eps
                                                   for x in (e.foreign_offers or []) for b in x["excluded"])),
        "foreign_offer_chosen_kinds": dict(Counter(x["chosen"]["kind"] for e in eps
                                                   for x in (e.foreign_offers or []) if x["chosen"])),
        "unsupported_last_potion": dict(Counter(e.last_potion for e in eps if e.status == "unsupported")),
        "hard_failures": dict(hard),
        "score_excluding_unsupported": _mean(c["final_reward"] for _, c in scored),
        "score_unsupported_as_death": _mean(death_or_score),
    }
    return out
