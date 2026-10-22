"""Original context and policy compatibility contract; unrelated checkpoint loader omitted."""

from __future__ import annotations

from pathlib import Path

from . import constants as C

CONTEXT_KW = {"potion_lambda": 0.5, "allowed_turns": 100, "loss_profile": "boss-act-v2"}

def context_problems(ctx, expected: dict = C.BATTLE) -> list[str]:
    """The running context is the all-boss-free reward + adapter + ledger family with E's roster/features."""
    from act1_battle_finetune_v1.reward import (AllBossFreeAdapter, AllBossFreePotionContext, AllBossFreeReward,
                                                REWARD_ID)
    p = []
    if not isinstance(ctx, AllBossFreePotionContext):
        p.append(f"context {type(ctx).__name__} is not AllBossFreePotionContext")
    if not isinstance(getattr(ctx, "potion", None), AllBossFreeReward):
        p.append("context reward is not AllBossFreeReward")
    if not isinstance(getattr(ctx, "adapter", None), AllBossFreeAdapter):
        p.append("context adapter is not AllBossFreeAdapter")
    rid = getattr(getattr(ctx, "potion", None), "reward_id", None)
    if rid != REWARD_ID or rid != expected["reward_id"]:
        p.append(f"context reward id {rid!r} != {expected['reward_id']!r}")
    try:
        if len(ctx.vocab.card_ids) != expected["cards"]:
            p.append(f"vocabulary has {len(ctx.vocab.card_ids)} cards, not {expected['cards']}")
        if ctx.adapter.describe()["schema"] != expected["feature_schema"]:
            p.append(f"feature schema {ctx.adapter.describe()['schema']!r} != {expected['feature_schema']}")
    except Exception as e:  # an unreadable context is refused, never assumed compatible
        p.append(f"context unreadable: {type(e).__name__}: {e}")
    return p

def make_context():
    """The GUARDED all-boss-free context over the actual prebuilt worker (native; the Windows host only)."""
    from act1_battle_finetune_v1 import isolation
    from act1_battle_finetune_v1.reward import AllBossFreePotionContext
    from crl.expansion_turn100_v1 import turn100_env_config
    return isolation.guarded(lambda: AllBossFreePotionContext([], turn100_env_config(100), **CONTEXT_KW))

def policy_problems(policy) -> list[str]:
    """The policy, its context and its per-fight ledger are one consistent all-boss-free family, frozen."""
    from act1_battle_finetune_v1.reward import AllBossFreeLedger
    p = context_problems(policy.ctx)
    if any(q.requires_grad for q in policy.model.parameters()):
        p.append("a Battle parameter requires grad")
    try:
        f = policy.begin_fight("rs62-identity-probe", 1, "boss", 80)
        if not isinstance(f.pstate.ledger, AllBossFreeLedger):
            p.append(f"fight ledger {type(f.pstate.ledger).__name__} is not AllBossFreeLedger")
        if f.pstate.ledger.potion_lambda != policy.ctx.potion.potion_lambda:
            p.append("fight ledger lambda differs from the context reward lambda")
    except Exception as e:
        p.append(f"begin_fight failed: {type(e).__name__}: {e}")
    return p
