"""Terminal reward and HP metrics.

Default (``preheal``): victory -> (terminal combat HP / combat-entry HP)^2, death -> -1,
every other step 0. Terminal combat HP is the engine's HP at CombatEngine.Step's terminal,
BEFORE run-side postcombat hooks (the collector's documented boundary), so Burning Blood's
heal is not in it and cannot be counted twice. Healing during combat can push the ratio above
1; it is not clipped. Entry HP must be positive (templates with 0 are excluded upstream).

Optional (``postheal``): the same formula on HP after Burning Blood, which the run engine
applies as ``min(maxHP, hp + 6)`` (RunRewardGenerator.ApplyChosenCheeseAndBurningBlood).
Only Burning Blood is modelled. If a fight holds another relic that acts at that boundary
(Chosen Cheese, Black Blood, Meat on the Bone), the post-heal value is reported as None and
using it as the training reward is an error, rather than inventing a generic end-hook model.

Truncation is not death: it yields 0 reward and is marked for bootstrapping.
"""

from __future__ import annotations

from dataclasses import dataclass

VARIANTS = ("preheal", "postheal")
BURNING_BLOOD = "BURNING_BLOOD"
BURNING_BLOOD_HEAL = 6
UNMODELLED_POSTCOMBAT_HP_RELICS = ("CHOSEN_CHEESE", "BLACK_BLOOD", "MEAT_ON_THE_BONE")


class RewardSpec:
    def __init__(self, catalog: dict, variant: str = "preheal"):
        if variant not in VARIANTS:
            raise ValueError(f"reward variant must be one of {VARIANTS}")
        self.variant = variant
        by_entry = {r["Entry"]: r["Id"] for r in catalog["relics"]}
        self.burning_blood = by_entry[BURNING_BLOOD]
        self.unmodelled = {by_entry[e]: e for e in UNMODELLED_POSTCOMBAT_HP_RELICS}

    def describe(self) -> dict:
        return {
            "variant": self.variant,
            "victory": "(hp_terminal / entry_hp) ** 2" + (" with hp after Burning Blood" if self.variant == "postheal" else ""),
            "death": -1.0,
            "intermediate": 0.0,
            "truncation": "0, bootstrapped (not terminal)",
            "burning_blood_relic_id": self.burning_blood,
            "burning_blood_heal": f"min(max_hp, hp + {BURNING_BLOOD_HEAL})",
            "unmodelled_postcombat_hp_relics": self.unmodelled,
            "clipping": "none",
        }


@dataclass
class EpisodeMetrics:
    status: str
    entry_hp: int
    entry_max_hp: int
    hp_terminal: int
    max_hp_terminal: int
    hp_postheal: int | None
    reward_preheal: float
    reward_postheal: float | None
    postheal_unmodelled_relics: list[str]

    @property
    def hp_loss(self) -> int:
        return self.entry_hp - self.hp_terminal

    @property
    def retained_ratio(self) -> float:
        return self.hp_terminal / self.entry_hp

    def as_dict(self) -> dict:
        d = dict(self.__dict__)
        d["hp_loss"] = self.hp_loss
        d["retained_ratio"] = self.retained_ratio
        return d


def episode_metrics(spec: RewardSpec, status: str, entry_hp: int, entry_max_hp: int,
                    hp: int, max_hp: int, relic_ids: list[int]) -> EpisodeMetrics:
    if entry_hp <= 0:
        raise ValueError("entry HP must be positive")
    unmodelled = sorted(spec.unmodelled[r] for r in set(relic_ids) if r in spec.unmodelled)
    if status == "win":
        pre = (hp / entry_hp) ** 2
        if unmodelled:
            post_hp, post = None, None
        else:
            post_hp = min(max_hp, hp + BURNING_BLOOD_HEAL) if spec.burning_blood in relic_ids else hp
            post = (post_hp / entry_hp) ** 2
    elif status == "loss":
        pre, post, post_hp = -1.0, -1.0, hp
    else:  # truncated / error / terminal_other / unsupported: no terminal reward
        pre, post, post_hp = 0.0, (None if unmodelled else 0.0), None
    return EpisodeMetrics(status, entry_hp, entry_max_hp, hp, max_hp, post_hp, pre, post, unmodelled)


def training_reward(spec: RewardSpec, m: EpisodeMetrics) -> float:
    if spec.variant == "preheal":
        return m.reward_preheal
    if m.reward_postheal is None:
        raise ValueError(f"postheal reward undefined with relics {m.postheal_unmodelled_relics}")
    return m.reward_postheal
