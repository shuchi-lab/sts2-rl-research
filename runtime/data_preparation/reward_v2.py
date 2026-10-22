"""The user's tiered terminal reward, independent of the archived preheal runtime.

Keep raw outcome/HP and (reward_base, loss_coefficient). A later lambda sweep is
reward_base - loss_lambda * loss_coefficient, without simulating the fight again.
Only terminal wins/deaths receive reward. A timeout is a bootstrap boundary, not death.
"""
from dataclasses import dataclass
import hashlib
import math
from pathlib import Path

REWARD_ID = "combat-tiered-v1"
ROOMS = frozenset(("normal", "event", "elite", "boss"))


@dataclass(frozen=True)
class TieredReward:
    loss_lambda: float = 1.0

    def __post_init__(self):
        if not math.isfinite(self.loss_lambda) or self.loss_lambda < 0:
            raise ValueError("loss_lambda must be finite and nonnegative")

    def components(self, status, *, act, room_type, entry_hp, hp_terminal, max_hp_terminal=None):
        if act not in (1, 2, 3) or room_type not in ROOMS:
            raise ValueError(f"Unsupported combat class: act={act}, room_type={room_type}")
        if status not in ("win", "loss", "running", "truncated"):
            raise ValueError(f"Not a valid training outcome: {status}")
        if not math.isfinite(entry_hp) or entry_hp <= 0:
            raise ValueError("entry_hp must be positive and finite")
        if not math.isfinite(hp_terminal) or hp_terminal < 0:
            raise ValueError("hp_terminal must be nonnegative and finite")
        if max_hp_terminal is not None and (not math.isfinite(max_hp_terminal) or max_hp_terminal <= 0):
            raise ValueError("max_hp_terminal must be positive and finite when supplied")
        x = hp_terminal / entry_hp
        base = 0.0
        if status == "win":
            if room_type == "boss":
                base = 2.0 if act == 3 else 1.0
            elif room_type == "elite":
                base = (1.0 + x * x) / 2.0
            else:
                base = x * x
        return {"reward_base": float(base), "loss_coefficient": float(status == "loss"), "x": float(x)}

    def terminal_reward(self, status, *, act, room_type, entry_hp, hp_terminal, max_hp_terminal=None):
        c = self.components(status, act=act, room_type=room_type, entry_hp=entry_hp,
                            hp_terminal=hp_terminal, max_hp_terminal=max_hp_terminal)
        return c["reward_base"] - self.loss_lambda * c["loss_coefficient"]

    def describe(self):
        return {
            "schema": "crl.reward_spec/v2", "reward_id": REWARD_ID,
            "loss_lambda": float(self.loss_lambda),
            "x": "hp_terminal / entry_hp", "clipping": "none",
            "hp_boundary": "engine terminal before run-side postcombat healing; in-combat healing retained",
            "win": {"normal": "x**2", "event": "x**2", "elite": "(1+x**2)/2",
                    "boss_act1": "1", "boss_act2": "1", "boss_act3": "2"},
            "loss": "-loss_lambda", "intermediate": 0.0,
            "truncation": "zero reward, not death; bootstrap from final observation",
            "relabel": "reward_base - loss_lambda * loss_coefficient",
            "implementation_module": "data_preparation.reward_v2",
            "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "legacy_reward": "crl.reward/preheal remains unchanged for original checkpoint compatibility",
        }
