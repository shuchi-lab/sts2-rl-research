"""Original all-boss-free Battle policy factory; unrelated experiment stages omitted."""

from __future__ import annotations

class FineTunedBattlePolicy:
    """Factory of a ``run_strategy_v1.frozen_battle.FrozenBattlePolicy`` whose fights keep the all-boss ledger."""

    @staticmethod
    def make(ctx, model, blob, identity):
        from run_strategy_v1.frozen_battle import FrozenBattlePolicy

        from .reward import AllBossFreeLedger

        class _Policy(FrozenBattlePolicy):
            def begin_fight(self, fight_id, act_ordinal, room, entry_hp):
                f = super().begin_fight(fight_id, act_ordinal, room, entry_hp)
                f.pstate.ledger = AllBossFreeLedger(self.ctx.potion.potion_lambda)
                return f

        return _Policy(ctx, model, blob, identity)
