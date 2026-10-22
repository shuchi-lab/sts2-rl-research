"""All-boss potion-charge waiver (``act1_battle_finetune_v1``): the potion_v1 victory-only bill with every
successful intentional use in ANY boss fight (Act 1, Act 2 or Act 3) exempt.

The shared ``potion_quality_training_v1.reward`` (Act 2/3 boss free, the source E35k contract) is imported
and subclassed, never edited. One function, :func:`all_boss_free_charge`, governs all three consumers, so
the charge an observation shows always equals the bill the ledger pays:

* :class:`AllBossFreeReward.charge` -- used by the shared ``crl.potion_v1.account_step`` (live online,
  validation, evaluation and in-run frozen battles) through ``ctx.potion.charge``;
* :class:`AllBossFreeAdapter` -- ``use_charge_now`` (one float replaced in place; feature schema
  ``crl.features/v3-potion-v2``, widths and ``describe()`` unchanged, hence the same feature digest);
* :class:`AllBossFreeLedger` -- reports the new reasons.

Everything else is unchanged: lambda 0.5, inventory-full and Delicate-Frond-without-Sozu exemptions,
bill subtracted only from a natural victory, boss-act-v2 death rewards, the 100-turn -2 terminal,
max_steps 0 bootstrapped, no clipping.
"""
from __future__ import annotations

import dataclasses
import hashlib
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from crl.potion_v1 import ACTS, ROOMS, PotionAdapter, PotionLedger, check_lambda, inventory
from potion_quality_training_v1.reward import (USE_CHARGE_NOW, BossFreePotionContext, BossFreePotionReward)

REWARD_ID = "combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1"
SOURCE_REWARD_ID = "combat-potion-turn100-boss-act-v2-victory-bill-a2a3-boss-free-v1"
REWARD_SCHEMA = "act1ft/reward_spec/potion-all-boss-free-v1"
REWARD_CONTRACT_SCHEMA = "act1ft/reward_contract/v1"
ACT1_BOSS_REASON, ACT2_BOSS_REASON = "act1_boss", "act2_boss"
EXEMPT_REASONS = ("inventory_full", "final_boss", ACT2_BOSS_REASON, ACT1_BOSS_REASON, "delicate_frond_refill")


def implementation_sha256() -> str:
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def all_boss_free_charge(potion_lambda: float, *, held: int, capacity: int, act: int, room: str,
                         frond: bool, sozu: bool) -> tuple[float, list[str]]:
    """(charge, exemption reasons) for one intentional use, from the inventory BEFORE it."""
    if not 0 < held <= capacity:
        raise ValueError(f"a use needs 0 < held <= capacity, got held {held} capacity {capacity}")
    if act not in ACTS or room not in ROOMS:
        raise ValueError(f"unsupported combat class act={act} room={room}")
    reasons = []
    if held == capacity:
        reasons.append("inventory_full")
    if room == "boss":
        reasons.append({3: "final_boss", 2: ACT2_BOSS_REASON, 1: ACT1_BOSS_REASON}[act])
    if frond and not sozu:
        reasons.append("delicate_frond_refill")
    return (0.0 if reasons else float(check_lambda(potion_lambda))), reasons


def original_bill_charge(use: dict, potion_lambda: float) -> float:
    """Counterfactual SOURCE-contract charge of a recorded use (diagnostic relabel only): a use whose only
    exemption under this rule is ``act1_boss`` would have been charged lambda under the Act 2/3 rule."""
    reasons = list(use.get("exempt_reasons") or [])
    if reasons == [ACT1_BOSS_REASON]:
        return float(potion_lambda)
    return float(use["charge"])


@dataclass(frozen=True)
class AllBossFreeReward(BossFreePotionReward):
    def charge(self, **kw) -> tuple[float, list[str]]:
        return all_boss_free_charge(self.potion_lambda, **kw)

    @property
    def reward_id(self) -> str:
        base = super().reward_id
        if base == SOURCE_REWARD_ID:
            return REWARD_ID
        return base.replace("-a2a3-boss-free-v1", "-all-boss-free-v1")

    def describe(self) -> dict:
        d = super().describe()
        d["use_charge"] = {**d["use_charge"], "rules": {
            "held == capacity": 0.0, "Act 3 boss (final boss)": 0.0, "Act 2 boss": 0.0, "Act 1 boss": 0.0,
            "Delicate Frond owned and Sozu not owned": 0.0, "otherwise": "potion_lambda"}}
        source_id = BossFreePotionReward.reward_id.fget(self)  # the parent's id (describe() reads self.reward_id)
        if self.reward_id == source_id:
            raise AssertionError("all-boss reward id coincides with the source reward id")
        d.update(schema=REWARD_SCHEMA, reward_id=self.reward_id, source_reward_id=source_id,
                 source_schema=d["schema"],
                 change="every successful intentional use in ANY boss fight (Act 1, 2 or 3) is free (reasons "
                        f"'{ACT1_BOSS_REASON}' / '{ACT2_BOSS_REASON}' / 'final_boss'); everything else unchanged "
                        "from the source Act 2/3 boss-free contract",
                 bill_features="use_charge_now / bill_accumulated / charged_uses follow this rule (feature schema "
                               "crl.features/v3-potion-v2 and widths unchanged)",
                 exempt_reasons=list(EXEMPT_REASONS),
                 implementation_module="act1_battle_finetune_v1.reward",
                 implementation_sha256=implementation_sha256(),
                 source_implementation_sha256=d["implementation_sha256"])
        return d


class AllBossFreeLedger(PotionLedger):
    def summary(self) -> dict:
        out = super().summary()
        ex = Counter(u["exempt_reasons"][0] for u in self.uses if u["exempt_reasons"])
        ex_any = Counter(r for u in self.uses for r in u["exempt_reasons"])
        out["exempt_by_primary_reason"] = {r: ex[r] for r in EXEMPT_REASONS}
        out["exempt_reason_mentions"] = {r: ex_any[r] for r in EXEMPT_REASONS}
        return out


class AllBossFreeAdapter(PotionAdapter):
    """v3-potion-v2 with ``use_charge_now`` from the all-boss rule (one float replaced; same layout)."""

    def encode(self, obs: dict, entry_hp: int, pctx=None):
        ob = super().encode(obs, entry_hp, pctx)
        held, cap = inventory(obs)
        relics = {r for r, _ in obs["relics"]}
        frond, sozu = self.frond_relic_id in relics, self.sozu_relic_id in relics
        now = all_boss_free_charge(pctx.ledger.potion_lambda, held=held, capacity=cap, act=pctx.act, room=pctx.room,
                                   frond=frond, sozu=sozu)[0] if held else 0.0
        glob = np.array(ob.glob, dtype=np.float32, copy=True)
        glob[USE_CHARGE_NOW] = now
        return dataclasses.replace(ob, glob=glob)


def _install(ctx) -> None:
    ctx.potion = AllBossFreeReward(ctx.potion.potion_lambda, ctx.potion.allowed_turns, ctx.potion.loss_profile)
    ctx.adapter = AllBossFreeAdapter(ctx.vocab, ctx.worker.hello, frond_relic_id=ctx.frond_relic_id,
                                     sozu_relic_id=ctx.sozu_relic_id)


def _contract(ctx, base: dict) -> dict:
    return {**base, "schema": REWARD_CONTRACT_SCHEMA, "training_reward": ctx.potion.reward_id,
            "source_reward_contract_schema": base["schema"],
            "potion_bill": "all-boss-free: Act 1, Act 2 and Act 3 boss uses never charged; lambda elsewhere; "
                           "inventory-full and Delicate Frond without Sozu exemptions kept",
            "relabel": "fresh online and validation fights only: charges by this rule at each use, terminal win = "
                       "base - bill, loss = base (boss-act-v2), turn_limit = -2 terminal, max_steps = 0 "
                       "bootstrapped; before replay insertion",
            "demonstrations": "none (no recorded episode is admitted)"}


class AllBossFreePotionContext(BossFreePotionContext):
    """The shared Act 2/3 boss-free context with the all-boss reward, adapter and ledger installed."""

    def __init__(self, templates, cfg=None, **kw):
        super().__init__(templates, cfg, **kw)
        try:
            _install(self)
        except BaseException:
            self.worker.close()
            raise

    def episode_state(self, template_id: str):
        ps = super().episode_state(template_id)
        ps.ledger = AllBossFreeLedger(self.potion.potion_lambda)
        return ps

    def reward_contract(self) -> dict:
        return _contract(self, super().reward_contract())


def source_reward_describe(potion_lambda: float = 0.5, allowed_turns: int = 100,
                           loss_profile: str = "boss-act-v2") -> dict:
    """The SOURCE E35k reward entry as computed from the pinned shared reward bytes (no worker)."""
    return BossFreePotionReward(potion_lambda, allowed_turns, loss_profile).describe()


def new_reward_describe(potion_lambda: float = 0.5, allowed_turns: int = 100,
                        loss_profile: str = "boss-act-v2") -> dict:
    return AllBossFreeReward(potion_lambda, allowed_turns, loss_profile).describe()
