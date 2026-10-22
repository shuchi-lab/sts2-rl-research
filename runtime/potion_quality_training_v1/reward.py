"""Boss-free potion bill (``potion_quality_training_v1``): the potion_v1 victory-only bill with every
successful intentional use in an Act 2 boss or Act 3 boss fight exempt.

Everything else is the unchanged :class:`crl.potion_v1.PotionReward`: lambda 0.5 per charged use, the
full-inventory and Delicate-Frond-without-Sozu exemptions, the bill subtracted only from a natural
victory, boss-act-v2 death rewards, the 100-turn -2 terminal, max_steps 0 bootstrapped, no clipping.

The rule is carried by three small subclasses so that every consumer sees ONE rule:

* :class:`BossFreePotionReward.charge` -- used by the shared ``crl.potion_v1.account_step`` for live
  online fights, fresh warm-up and validation (through ``ctx.potion.charge``) and by the cache's
  re-encoding of recorded demonstrations;
* :class:`BossFreePotionAdapter` -- the ``use_charge_now`` observation feature (the shared encoder calls
  the module-level ``use_charge``); ``bill_accumulated`` / ``charged_uses`` already follow the ledger.
  The feature schema, widths and ``describe()`` stay ``crl.features/v3-potion-v2``;
* :class:`BossFreePotionContext` -- installs both, a ledger that reports the new reason, and a new reward
  contract. The reward id / schema / implementation hash enter provenance, checkpoints and the cache.
"""
from __future__ import annotations

import dataclasses
import hashlib
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from crl.potion_v1 import (ACTS, DEFAULT_POTION_LAMBDA, POTION_GLOBAL_FEATURES, ROOMS, PotionAdapter,
                           PotionContext, PotionLedger, PotionReward, check_lambda, inventory)

REWARD_ID = "combat-potion-turn100-boss-act-v2-victory-bill-a2a3-boss-free-v1"
REWARD_SCHEMA = "pqt1/reward_spec/potion-boss-free-v1"
REWARD_CONTRACT_SCHEMA = "pqt1/reward_contract/v1"
ACT2_BOSS_REASON = "act2_boss"
FREE_BOSS_ACTS = (2, 3)
EXEMPT_REASONS = ("inventory_full", "final_boss", ACT2_BOSS_REASON, "delicate_frond_refill")
USE_CHARGE_NOW = POTION_GLOBAL_FEATURES.index("use_charge_now") - len(POTION_GLOBAL_FEATURES)  # index from the end


def implementation_sha256() -> str:
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def boss_free_charge(potion_lambda: float, *, held: int, capacity: int, act: int, room: str,
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
    if act == 2 and room == "boss":
        reasons.append(ACT2_BOSS_REASON)
    if frond and not sozu:
        reasons.append("delicate_frond_refill")
    return (0.0 if reasons else float(check_lambda(potion_lambda))), reasons


@dataclass(frozen=True)
class BossFreePotionReward(PotionReward):
    def charge(self, **kw) -> tuple[float, list[str]]:
        return boss_free_charge(self.potion_lambda, **kw)

    @property
    def reward_id(self) -> str:
        base = super().reward_id
        if base == "combat-potion-turn100-boss-act-v2-victory-bill-v1":
            return REWARD_ID
        return base.replace("-victory-bill-v1", "-victory-bill-a2a3-boss-free-v1")

    def describe(self) -> dict:
        d = super().describe()
        d["use_charge"] = {**d["use_charge"], "rules": {
            "held == capacity": 0.0, "Act 3 boss (final boss)": 0.0, "Act 2 boss": 0.0,
            "Delicate Frond owned and Sozu not owned": 0.0, "otherwise": "potion_lambda"}}
        d.update(schema=REWARD_SCHEMA, reward_id=self.reward_id, base_reward_id=super().reward_id,
                 change="every successful intentional use in an Act 2 boss or Act 3 boss fight is free "
                        f"(reason '{ACT2_BOSS_REASON}' / 'final_boss'); everything else unchanged",
                 bill_features="use_charge_now / bill_accumulated / charged_uses follow this rule "
                               "(feature schema crl.features/v3-potion-v2 and widths unchanged)",
                 exempt_reasons=list(EXEMPT_REASONS),
                 implementation_module="potion_quality_training_v1.reward",
                 implementation_sha256=implementation_sha256(),
                 base_implementation_sha256=d["implementation_sha256"])
        return d


class BossFreeLedger(PotionLedger):
    def summary(self) -> dict:
        out = super().summary()
        ex = Counter(u["exempt_reasons"][0] for u in self.uses if u["exempt_reasons"])
        ex_any = Counter(r for u in self.uses for r in u["exempt_reasons"])
        out["exempt_by_primary_reason"] = {r: ex[r] for r in EXEMPT_REASONS}
        out["exempt_reason_mentions"] = {r: ex_any[r] for r in EXEMPT_REASONS}
        return out


class BossFreePotionAdapter(PotionAdapter):
    """v3-potion-v2 with ``use_charge_now`` from the boss-free rule (one float replaced; same layout)."""

    def encode(self, obs: dict, entry_hp: int, pctx=None):
        ob = super().encode(obs, entry_hp, pctx)
        held, cap = inventory(obs)
        relics = {r for r, _ in obs["relics"]}
        frond, sozu = self.frond_relic_id in relics, self.sozu_relic_id in relics
        now = boss_free_charge(pctx.ledger.potion_lambda, held=held, capacity=cap, act=pctx.act, room=pctx.room,
                               frond=frond, sozu=sozu)[0] if held else 0.0
        glob = np.array(ob.glob, dtype=np.float32, copy=True)
        glob[USE_CHARGE_NOW] = now
        return dataclasses.replace(ob, glob=glob)


class BossFreePotionContext(PotionContext):
    def __init__(self, templates, cfg=None, *, potion_lambda: float = DEFAULT_POTION_LAMBDA, **kw):
        super().__init__(templates, cfg, potion_lambda=potion_lambda, **kw)
        try:
            self.potion = BossFreePotionReward(self.potion.potion_lambda, self.potion.allowed_turns,
                                               self.potion.loss_profile)
            self.adapter = BossFreePotionAdapter(self.vocab, self.worker.hello, frond_relic_id=self.frond_relic_id,
                                                 sozu_relic_id=self.sozu_relic_id)
        except BaseException:
            self.worker.close()
            raise

    def episode_state(self, template_id: str):
        ps = super().episode_state(template_id)
        ps.ledger = BossFreeLedger(self.potion.potion_lambda)
        return ps

    def reward_contract(self) -> dict:
        base = super().reward_contract()
        return {**base, "schema": REWARD_CONTRACT_SCHEMA, "training_reward": self.potion.reward_id,
                "base_reward_contract_schema": base["schema"],
                "potion_bill": "boss-free: Act 2 boss and Act 3 boss uses never charged; lambda elsewhere; "
                               "inventory-full and Delicate Frond without Sozu exemptions kept",
                "relabel": "fresh online, fresh warm-up, validation AND recorded demonstrations: charges recomputed "
                           "by this rule (demonstrations re-encoded from their raw responses under this context); "
                           "terminal win = base - bill, loss = base (boss-act-v2), turn_limit = -2 terminal, "
                           "max_steps = 0 bootstrapped; before replay insertion. No stored label feeds TD.",
                "demonstrations": "potion_quality_mix_v1 compatibility order (50,000), validated by the bank's strict "
                                  "reader under its canonical OLD context, then re-encoded under this context"}
