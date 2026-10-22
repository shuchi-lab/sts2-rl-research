"""Turn100-v1: opt-in successor of the ``combat-tiered-v1`` contract with a terminal turn limit.

Everything here is NEW and opt-in; ``crl.expansion``, ``crl.expansion_train``,
``data_preparation.reward_v2``, ``crl.env``, the worker and every existing checkpoint keep
their frozen meaning.

Turn budget (zero-based raw counter). The pinned simulator starts every fight at
``state.Turn = 0`` (CombatFactory.cs:388) and increments it after the enemy phase
(CombatEngine.cs:2537), so the raw counter of ordinary turn ``n`` is ``n - 1``. The worker
(RlWorker ``Session.Refresh``) truncates with ``reason=max_turns`` when ``state.Turn >
max_turns`` at a decision. A public budget of ``allowed_turns = 100`` player turns therefore
needs worker ``max_turns = 99`` (:func:`worker_max_turns`): decisions at raw counters 0..99 are
allowed and the next unresolved decision (raw counter >= 100) is cut. The old production
setting ``max_turns = 100`` allowed 101 turns.

Rule (reward id ``combat-tiered-turn100-v1``):

* win / death: exactly ``TieredReward(loss_lambda)`` (same 214-card vocabulary, same win
  formulas, unclipped x = pre-heal terminal HP / entry HP, death = ``-loss_lambda``). A step the
  engine reports terminal never reaches ``Refresh``, so a win/death resolved by the step taken at
  the last allowed decision keeps its outcome even if its counter has already moved past it.
* ``turn_limit``: worker ``status=truncated, reason=max_turns`` with every decision taken at a
  raw counter <= ``max_turns`` and the final counter > ``max_turns`` (checked from the Rollout
  trace or the transition turn deltas; a skipped counter is allowed, a decision past the cap is
  not). Reward ``-2.0`` (independent of lambda), RL TERMINAL: its last transition becomes
  ``done=True, next_obs=None`` (no bootstrap). Diagnostic outcome ``turn_limit``, not a death.
* ``max_steps`` truncation (worker checks ``steps >= max_steps`` BEFORE the turn limit) stays
  an action-budget boundary: reward 0, bootstrapped, reported, validation-disqualifying.
* error / unsupported / terminal_other stay hard failures (never -2, never stored).

The optional ``boss-act-v1`` loss profile has reward id
``combat-tiered-turn100-boss-act-loss-v1``: death = -1 for normal/event/elite and
Act 1 bosses, -0.6 for Act 2 bosses and -0.2 for Act 3 bosses. Wins and turn limits
keep the rules above. The successor boss-act-v2 changes only Act 2 boss death
to -0.4, with reward id combat-tiered-turn100-boss-act-loss-v2. The training
entry point defaults to v2; direct
reward/context construction retains uniform defaults for historical evaluation.

Replay: :class:`Turn100Replay` stores turn-limit episodes in the existing ``loss`` (negative)
stratum, counts them separately and marks the terminal row as :class:`TurnLimitTransition`.

Validation: a turn-limit episode contributes -2 to the mean validation reward and does not
disqualify a checkpoint; errors and max_steps truncations still disqualify.

Demonstrations: the 50,000 recorded episodes were made with worker ``max_turns = 100``.
:func:`demo_turn_admission` is a narrow adapter: it admits a recorded natural win/loss under
worker ``max_turns = 99`` only when every recorded decision sits at raw counter <= 99 (the lower
cap would never have fired: ``Refresh`` runs only at decisions, and a terminal step skips it)
and every other environment field is identical. Crossing episodes, other caps, max_steps or other
env mismatches, unknown statuses and malformed counters are refused. The cached record is never
modified; the translation is recorded per admission.
"""

from __future__ import annotations

import dataclasses
import hashlib
import math
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path

from .env import EnvConfig, Episode, Transition
from .expansion import DemoMismatch, ExpansionContext, demo_episode, production_env_config, summarize_expansion
from .replay import REJECTED_STATUSES, StratifiedReplay, validate_episode

from data_preparation.reward_v2 import TieredReward

REWARD_ID = "combat-tiered-turn100-v1"
REWARD_SCHEMA = "crl.reward_spec/v3-turn100-budget"
REWARD_CONTRACT_SCHEMA = "crl.expansion_turn100_reward_contract/v2"
UNIFORM_LOSS_PROFILE = "uniform"
BOSS_ACT_LOSS_PROFILE = "boss-act-v1"
BOSS_ACT_REWARD_ID = "combat-tiered-turn100-boss-act-loss-v1"
BOSS_ACT_V2_LOSS_PROFILE = "boss-act-v2"
BOSS_ACT_V2_REWARD_ID = "combat-tiered-turn100-boss-act-loss-v2"
# Explicit scan presets keep the historical v1/v2 contracts unchanged.
BOSS_ACT_SCAN_PROFILES = {
    "boss-act-scan-a2m08": (-0.8, -0.2),
    "boss-act-scan-a3m01": (-0.6, -0.1),
    "boss-act-scan-a3m04": (-0.6, -0.4),
}
BOSS_ACT_LOSS_PROFILES = (BOSS_ACT_LOSS_PROFILE, BOSS_ACT_V2_LOSS_PROFILE) + tuple(BOSS_ACT_SCAN_PROFILES)
LOSS_PROFILES = (UNIFORM_LOSS_PROFILE,) + BOSS_ACT_LOSS_PROFILES


def boss_act_loss_rewards(loss_profile: str = BOSS_ACT_LOSS_PROFILE) -> dict:
    """Fixed signed death rewards; no-argument calls preserve the v1 contract."""
    if loss_profile not in BOSS_ACT_LOSS_PROFILES:
        raise ValueError(f"not a boss-act loss profile: {loss_profile}")
    act2, act3 = BOSS_ACT_SCAN_PROFILES.get(
        loss_profile, (-0.4 if loss_profile == BOSS_ACT_V2_LOSS_PROFILE else -0.6, -0.2))
    return {"normal": -1.0, "event": -1.0, "elite": -1.0,
            "boss_act1": -1.0,
            "boss_act2": act2,
            "boss_act3": act3}


ALLOWED_TURNS = 100  # public budget: ordinary player turns a fight may use
TURN_LIMIT_REWARD = -2.0
TURN_LIMIT_STATUS = "turn_limit"
OUTCOMES = ("win", "loss", TURN_LIMIT_STATUS, "truncated")  # "truncated" = max_steps only
TURN100_STATUS_STRATUM = {"win": "win", "loss": "loss", TURN_LIMIT_STATUS: "loss", "truncated": "other"}
LEGACY_DEMO_MAX_TURNS = 100  # env_config.max_turns of the recorded demonstrations (old 101-turn budget)
DEMO_ADAPTER_ID = "turn100-demo-turn-admission/v1"


def worker_max_turns(allowed_turns: int) -> int:
    """Worker ``max_turns`` (largest raw decision counter) for ``allowed_turns`` ordinary turns."""
    if not isinstance(allowed_turns, int) or isinstance(allowed_turns, bool) or allowed_turns < 1:
        raise ValueError(f"allowed_turns must be a positive int, got {allowed_turns!r}")
    return allowed_turns - 1


def turn_budget(allowed_turns: int = ALLOWED_TURNS) -> dict:
    w = worker_max_turns(allowed_turns)
    return {
        "allowed_turns": allowed_turns, "worker_max_turns": w,
        "raw_turn_counter": "zero-based state.Turn: CombatFactory.cs:388 sets Turn=0, CombatEngine.cs:2537 "
                            "increments it after the enemy phase; ordinary turn = raw counter + 1",
        "allowed_raw_decision_counters": [0, w], "cutoff_raw_counter": f">= {w + 1}",
        "worker_rule": "RlWorker Session.Refresh at each decision: steps >= max_steps -> truncated/max_steps, "
                       "else state.Turn > max_turns -> truncated/max_turns (worker unchanged)",
        "terminal_priority": "a step the engine reports terminal (win/death) never reaches Refresh: it keeps "
                             "its natural outcome even if its counter is past the last allowed one",
        "turn_limit_meaning": f"the player finished ordinary turn {allowed_turns} (raw {w}) unresolved and the "
                              "next decision is cut",
    }


def turn100_env_config(allowed_turns: int = ALLOWED_TURNS) -> EnvConfig:
    """The production environment with the worker turn cap for ``allowed_turns`` (default 100 -> 99)."""
    return dataclasses.replace(production_env_config(), max_turns=worker_max_turns(allowed_turns))


@dataclass(frozen=True)
class Turn100TieredReward:
    loss_lambda: float = 1.0
    allowed_turns: int = ALLOWED_TURNS
    turn_limit_reward: float = TURN_LIMIT_REWARD
    loss_profile: str = UNIFORM_LOSS_PROFILE

    def __post_init__(self):
        TieredReward(self.loss_lambda)  # same lambda validation
        worker_max_turns(self.allowed_turns)
        if not math.isfinite(self.turn_limit_reward):
            raise ValueError("turn_limit_reward must be finite")
        if self.loss_profile not in LOSS_PROFILES:
            raise ValueError(f"loss_profile must be one of {LOSS_PROFILES}")
        if self.loss_profile in BOSS_ACT_LOSS_PROFILES and self.loss_lambda != 1.0:
            raise ValueError(f"{self.loss_profile} has fixed loss rewards; use loss_lambda=1 or loss_profile=uniform")

    @property
    def reward_id(self) -> str:
        if self.loss_profile in BOSS_ACT_SCAN_PROFILES:
            return "combat-tiered-turn100-" + self.loss_profile
        if self.loss_profile == BOSS_ACT_V2_LOSS_PROFILE:
            return BOSS_ACT_V2_REWARD_ID
        return BOSS_ACT_REWARD_ID if self.loss_profile == BOSS_ACT_LOSS_PROFILE else REWARD_ID

    @property
    def base(self) -> TieredReward:
        """Uniform source-data reward; the running loss profile is applied separately."""
        return TieredReward(self.loss_lambda)

    @property
    def worker_max_turns(self) -> int:
        return worker_max_turns(self.allowed_turns)

    def terminal_reward(self, status, *, act, room_type, entry_hp, hp_terminal, max_hp_terminal=None) -> float:
        if status == TURN_LIMIT_STATUS:
            # Same class/HP validation as the base reward; the value is the fixed turn-limit reward.
            self.base.components("truncated", act=act, room_type=room_type, entry_hp=entry_hp,
                                 hp_terminal=hp_terminal, max_hp_terminal=max_hp_terminal)
            return float(self.turn_limit_reward)
        if status not in ("win", "loss"):
            raise ValueError(f"not a terminal turn100 outcome: {status}")
        value = self.base.terminal_reward(status, act=act, room_type=room_type, entry_hp=entry_hp,
                                         hp_terminal=hp_terminal, max_hp_terminal=max_hp_terminal)
        if status == "loss" and self.loss_profile in BOSS_ACT_LOSS_PROFILES:
            key = f"boss_act{act}" if room_type == "boss" else room_type
            return boss_act_loss_rewards(self.loss_profile)[key]
        return value

    def describe(self) -> dict:
        base = self.base.describe()
        out = {
            "schema": REWARD_SCHEMA, "reward_id": REWARD_ID,
            "loss_lambda": float(self.loss_lambda),
            "x": base["x"], "clipping": base["clipping"], "hp_boundary": base["hp_boundary"],
            "win": base["win"], "loss": base["loss"], "intermediate": 0.0,
            "turn_limit": {
                "name": "unresolved after the allowed turns", **turn_budget(self.allowed_turns),
                "reward": float(self.turn_limit_reward), "lambda_dependent": False,
                "trigger": f"worker status=truncated reason=max_turns: decisions at raw counters <= "
                           f"{self.worker_max_turns}, final counter > {self.worker_max_turns}",
                "terminal": "done=True, no bootstrap", "diagnostic_outcome": TURN_LIMIT_STATUS,
                "is_death": False,
            },
            "truncation": "max_steps only: zero reward, not death; bootstrap from final observation",
            "failures": "error/unsupported/terminal_other rejected, never rewarded",
            "base_reward": base,
            "implementation_module": "crl.expansion_turn100_v1",
            "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        }
        if self.loss_profile in BOSS_ACT_LOSS_PROFILES:
            loss = boss_act_loss_rewards(self.loss_profile)
            out.update(schema="crl.reward_spec/v4-turn100-boss-act-loss", reward_id=self.reward_id,
                       loss_profile=self.loss_profile, loss=loss, loss_lambda_applies=False)
            # The frozen uniform components remain the integrity check for recorded source data.
            out["recorded_data_reward"] = base
            out["base_reward"] = {**base, "schema": "crl.reward_spec/v3-boss-act-loss",
                                  "reward_id": self.reward_id.replace("turn100-", ""),
                                  "loss_profile": self.loss_profile, "loss_lambda_applies": False,
                                  "loss": loss, "relabel": "recompute from outcome and act/room class",
                                  "implementation_module": "crl.expansion_turn100_v1",
                                  "implementation_sha256": out["implementation_sha256"]}
        return out


class TurnLimitTransition(Transition):
    """The terminal row of a turn-limit episode (a plain Transition; the type marks it for audits)."""


def decision_turns(ep: Episode) -> list[int] | None:
    """Raw turn counter of every decision taken, from the Rollout trace or the transition deltas.

    None when neither is available (transitions dropped and no trace)."""
    if ep.trace is not None:
        if len(ep.trace) != ep.steps:
            raise ValueError(f"{ep.template_id}/{ep.env_seed}: trace has {len(ep.trace)} rows, {ep.steps} steps")
        return [int(row[0]) for row in ep.trace]
    if ep.steps == 0:
        return []
    if len(ep.transitions) != ep.steps:
        return None
    out, t = [], ep.turns
    for tr in reversed(ep.transitions):  # delta_turn = next counter - this counter
        t -= tr.delta_turn
        out.append(t)
    return out[::-1]


def check_turn_counters(ep: Episode, max_turns: int) -> list[int]:
    """Decision counters of ``ep`` after checking them against the declared rules (ValueError otherwise)."""
    where = f"{ep.template_id}/{ep.env_seed}"
    turns = decision_turns(ep)
    if turns is None:
        raise ValueError(f"{where}: no trace or transitions to check the turn counters ({ep.steps} steps)")
    if not turns:
        if ep.turns != 0:
            raise ValueError(f"{where}: zero-decision episode at raw turn {ep.turns}, expected 0")
        return turns
    if turns[0] != 0:
        raise ValueError(f"{where}: first decision at raw turn {turns[0]}, expected 0")
    if any(b < a for a, b in zip(turns, turns[1:])) or ep.turns < turns[-1]:
        raise ValueError(f"{where}: non-monotonic turn counters {turns[-5:]} -> {ep.turns}")
    if turns[-1] > max_turns:
        raise ValueError(f"{where}: decision at raw turn {turns[-1]} > worker max_turns {max_turns}")
    return turns


def outcome_of(ep: Episode, max_turns: int, max_steps: int) -> str:
    """win / loss / turn_limit / truncated (max_steps) / a rejected failure status (boundary-checked)."""
    if ep.status in REJECTED_STATUSES:
        return ep.status
    where = f"{ep.template_id}/{ep.env_seed}"
    if ep.status == TURN_LIMIT_STATUS:
        return TURN_LIMIT_STATUS
    if ep.status == "truncated" and ep.reason not in ("max_turns", "max_steps"):
        raise ValueError(f"{where}: unknown truncation reason {ep.reason!r}")
    if ep.status not in ("win", "loss", "truncated"):
        raise ValueError(f"{where}: unknown status {ep.status!r}")
    check_turn_counters(ep, max_turns)
    if ep.status != "truncated":
        return ep.status
    if ep.reason == "max_steps":
        if ep.steps < max_steps:
            raise ValueError(f"{where}: max_steps truncation after {ep.steps} < {max_steps} steps")
        return "truncated"
    if ep.steps >= max_steps:
        raise ValueError(f"{where}: max_turns at {ep.steps} steps; the worker reports max_steps first")
    if ep.steps == 0 or ep.turns <= max_turns:
        raise ValueError(f"{where}: max_turns truncation at raw turn {ep.turns} after {ep.steps} steps; "
                         f"expected a counter > {max_turns} after at least one decision")
    return TURN_LIMIT_STATUS


def turn_fields(ep: Episode, raw_status: str, max_turns: int) -> dict:
    """Raw counters and the ordinary-turn reading of a finished episode (for logs)."""
    try:
        turns = decision_turns(ep)
    except ValueError:
        turns = None
    last = turns[-1] if turns else None
    out = {"raw_turn_counter": ep.turns, "last_decision_raw_turn": last, "worker_max_turns": max_turns}
    if raw_status == "truncated" or ep.status == TURN_LIMIT_STATUS:
        out["human_turn"] = None if last is None else last + 1
        out["turn_reading"] = "truncated at a decision: human_turn = last turn the player acted in (raw + 1)"
        if ep.reason == "max_turns":
            out["human_turns_completed"] = ep.turns
    else:
        out["human_turn"] = ep.turns + 1 if ep.steps > 0 else 0
        out["turn_reading"] = "human_turn = final raw counter + 1 (0 = zero-action start)"
    return out


class Turn100Context(ExpansionContext):
    """ExpansionContext whose reward is :class:`Turn100TieredReward`; ``self.tiered`` stays the base reward."""

    def __init__(self, templates, cfg: EnvConfig | None = None, *, loss_lambda: float = 1.0,
                 classes: dict[str, tuple[int, str]] | None = None, catalog: dict | None = None,
                 allowed_turns: int = ALLOWED_TURNS, loss_profile: str = UNIFORM_LOSS_PROFILE):
        self.turn100 = Turn100TieredReward(loss_lambda, allowed_turns, loss_profile=loss_profile)
        templates = list(templates)
        cfg = cfg or turn100_env_config(allowed_turns)
        if cfg.max_turns != self.turn100.worker_max_turns:
            raise ValueError(f"{allowed_turns} allowed turns need worker max_turns == "
                             f"{self.turn100.worker_max_turns}, got {cfg.max_turns}")
        super().__init__(templates, cfg, loss_lambda=loss_lambda, classes=classes, catalog=catalog)

    def reward_contract(self) -> dict:
        out = {
            "schema": REWARD_CONTRACT_SCHEMA,
            "training_reward": REWARD_ID,
            "loss_lambda": float(self.turn100.loss_lambda),
            "turn_budget": turn_budget(self.turn100.allowed_turns),
            "allowed_turns": self.turn100.allowed_turns,
            "worker_max_turns": self.turn100.worker_max_turns,
            "turn_limit_reward": float(self.turn100.turn_limit_reward),
            "x": "pre-postcombat-heal terminal HP / combat-entry HP, unclipped",
            "relabel": "win/loss terminal transitions relabelled with TieredReward; a max_turns episode's last "
                       "transition becomes terminal (done, no next state) with turn_limit_reward; max_steps "
                       "truncation stays 0 and bootstrapped; all before learner replay insertion",
            "replay": "turn_limit episodes stored in the loss (negative) stratum, counted separately",
            "validation_score": "mean turn100 reward over all validation episodes (turn_limit counts -2); "
                                "errors or max_steps truncations disqualify; turn_limit does not",
            "demonstrations": f"{DEMO_ADAPTER_ID}: recorded max_turns {LEGACY_DEMO_MAX_TURNS} win/loss admitted "
                              f"only if every decision is at raw counter <= {self.turn100.worker_max_turns}",
            "legacy_metrics": "crl.reward/preheal reward_preheal and HP metrics, reported separately, not trained on",
        }
        if self.turn100.loss_profile in BOSS_ACT_LOSS_PROFILES:
            out.update(schema="crl.expansion_turn100_reward_contract/v3-boss-act-loss",
                       training_reward=self.turn100.reward_id, loss_profile=self.turn100.loss_profile,
                       loss_rewards=boss_act_loss_rewards(self.turn100.loss_profile), loss_lambda_applies=False,
                       relabel="validate frozen uniform source components, then recompute win/loss terminal "
                               "rewards from act/room class before replay; turn_limit=-2 terminal; "
                               "max_steps=0 bootstrapped")
        return out

    def provenance(self) -> dict:
        out = super().provenance()
        out["reward"] = self.turn100.describe()
        out["reward_contract"] = self.reward_contract()
        return out

    def outcome(self, ep: Episode) -> str:
        return outcome_of(ep, self.cfg.max_turns, self.cfg.max_steps)

    def mark_outcome(self, ep: Episode) -> str:
        """Set ``ep.status`` to ``turn_limit`` for a max_turns episode (reason stays max_turns)."""
        o = self.outcome(ep)
        if o == TURN_LIMIT_STATUS:
            ep.status = TURN_LIMIT_STATUS
        return o

    def tiered_reward(self, ep: Episode) -> float | None:
        """Turn100 value of a finished episode (0 for max_steps truncation, None for failures)."""
        o = self.outcome(ep)
        if o == TURN_LIMIT_STATUS:
            act, room = self.classify(ep.template_id)
            m = ep.metrics
            return self.turn100.terminal_reward(o, act=act, room_type=room, entry_hp=m.entry_hp,
                                                hp_terminal=m.hp_terminal, max_hp_terminal=m.max_hp_terminal)
        if o == "truncated":
            return 0.0
        if o not in ("win", "loss"):
            return None
        act, room = self.classify(ep.template_id)
        m = ep.metrics
        return self.turn100.terminal_reward(o, act=act, room_type=room, entry_hp=m.entry_hp,
                                            hp_terminal=m.hp_terminal, max_hp_terminal=m.max_hp_terminal)

    def relabel(self, ep: Episode) -> float | None:
        """Turn100 terminal reward on ``ep``'s transitions; a max_turns episode becomes terminal."""
        if ep.status == TURN_LIMIT_STATUS:
            raise ValueError(f"{ep.template_id}/{ep.env_seed}: already relabelled")
        o = self.outcome(ep)
        if o != TURN_LIMIT_STATUS:
            return super().relabel(ep)  # dynamic tiered_reward applies the profile; max_steps stays bootstrapped
        r = self.tiered_reward(ep)
        trans = ep.transitions
        if not trans:
            raise ValueError(f"{ep.template_id}/{ep.env_seed}: max_turns episode without transitions")
        for i, t in enumerate(trans):
            if t.done or t.reward != 0.0 or t.next_obs is None:
                raise ValueError(f"{ep.template_id}/{ep.env_seed}: max_turns transition {i} "
                                 f"done={t.done} reward={t.reward} next_obs={'set' if t.next_obs is not None else None}")
        last = trans[-1]
        trans[-1] = TurnLimitTransition(obs=last.obs, action=last.action, reward=r, next_obs=None,
                                        done=True, delta_turn=last.delta_turn)
        ep.status = TURN_LIMIT_STATUS
        return r


# ---------------------------------------------------------------------------
# Demonstrations: narrow admission adapter for the recorded max_turns=100 episodes
# ---------------------------------------------------------------------------

class DemoTurnBudgetMismatch(DemoMismatch):
    """A recorded episode cannot be admitted under the running turn budget."""


def _int(x) -> bool:
    return isinstance(x, int) and not isinstance(x, bool)


def demo_turn_admission(rec: dict, cfg: EnvConfig, allowed_turns: int = ALLOWED_TURNS) -> dict:
    """Admission record for one recorded episode under ``allowed_turns`` (raises DemoTurnBudgetMismatch).

    Admits a recorded natural win/loss whose recorded env differs from the running env at most in
    ``max_turns`` (recorded LEGACY_DEMO_MAX_TURNS -> running worker_max_turns(allowed_turns)) and
    whose decisions all sit at raw counters <= the running cap. Under the worker rule the lower cap
    is evaluated only at decisions (Refresh) and never after a terminal step, so it could not have
    intervened. Never modifies ``rec``."""
    eid = rec.get("episode_id")

    def bad(msg):
        return DemoTurnBudgetMismatch(f"recorded episode {eid}: {msg}")

    w = worker_max_turns(allowed_turns)
    run_env = asdict(cfg)
    if cfg.max_turns != w:
        raise bad(f"running worker max_turns {cfg.max_turns} != {w} for {allowed_turns} allowed turns")
    rec_env = rec.get("env_config")
    if not isinstance(rec_env, dict) or set(rec_env) != set(run_env):
        raise bad(f"env_config keys {sorted(rec_env) if isinstance(rec_env, dict) else rec_env!r} != running")
    diff = sorted(k for k in run_env if k != "max_turns" and rec_env[k] != run_env[k])
    if diff:
        raise bad(f"env_config differs beyond max_turns: {[(k, rec_env[k], run_env[k]) for k in diff]}")
    rec_cap = rec_env["max_turns"]
    if rec_cap not in (w, LEGACY_DEMO_MAX_TURNS) or not _int(rec_cap):
        raise bad(f"recorded max_turns {rec_cap!r}; only {LEGACY_DEMO_MAX_TURNS} (translated) or {w} admitted")
    if rec.get("status") not in ("win", "loss") or rec.get("error") is not None:
        raise bad(f"status {rec.get('status')!r} error {rec.get('error')!r}; only recorded natural win/loss "
                  "(records carry no worker truncation reason)")

    decisions = rec.get("decisions")
    steps, n_dec = rec.get("steps"), rec.get("n_decisions")
    if not isinstance(decisions, list) or not _int(steps) or steps != n_dec or steps != len(decisions):
        raise bad(f"steps {steps!r} / n_decisions {n_dec!r} / decisions "
                  f"{len(decisions) if isinstance(decisions, list) else decisions!r} inconsistent")
    if steps > cfg.max_steps:
        raise bad(f"{steps} steps > max_steps {cfg.max_steps}")
    turns = []
    for i, d in enumerate(decisions):
        t = d.get("turn") if isinstance(d, dict) else None
        obs_t = (d.get("obs_raw") or {}).get("turn") if isinstance(d, dict) else None
        if not _int(t) or t != obs_t:
            raise bad(f"decision {i}: malformed turn {t!r} (observation turn {obs_t!r})")
        turns.append(t)
    term, term_obs = rec.get("terminal_turn"), (rec.get("terminal_obs_raw") or {}).get("turn")
    if not _int(term) or term != rec.get("turns") or term != term_obs:
        raise bad(f"terminal_turn {term!r} / turns {rec.get('turns')!r} / terminal observation {term_obs!r} differ")
    if turns:
        if turns[0] != 0:
            raise bad(f"first decision at raw turn {turns[0]}, expected 0")
        if any(b < a for a, b in zip(turns, turns[1:])) or term < turns[-1]:
            raise bad(f"non-monotonic raw turn counters (last {turns[-3:]}, terminal {term})")
        if turns[-1] > w:
            raise bad(f"crossing episode: decision at raw turn {turns[-1]} (ordinary turn {turns[-1] + 1}) > "
                      f"worker max_turns {w}; the {allowed_turns}-turn cap would have truncated it")
    elif term != 0:
        raise bad(f"zero-decision record at raw turn {term}, expected 0")
    return {
        "adapter": DEMO_ADAPTER_ID,
        "recorded_max_turns": rec_cap, "running_worker_max_turns": w, "allowed_turns": allowed_turns,
        "translated": rec_cap != w,
        "decisions": steps, "max_decision_raw_turn": turns[-1] if turns else None, "terminal_raw_turn": term,
        "human_turn": term + 1 if turns else 0,
    }


def turn100_demo_episode(rec: dict, row: dict, ctx: Turn100Context, *, template_sha256: str,
                         build_digest: str) -> tuple[Episode, float, dict]:
    """(Episode, turn100 value, admission record) for a recorded win/loss under the running turn budget.

    The source value is validated against combat-tiered-v1 before applying the running profile.
    All other checks of ``demo_episode`` (episode,
    template, seed, status, component, build digest, template hash, class, stored reward components,
    encoder/legal options) are unchanged; only ``env_config.max_turns`` is translated, and only after
    :func:`demo_turn_admission`."""
    adm = demo_turn_admission(rec, ctx.cfg, ctx.turn100.allowed_turns)
    view = {**rec, "env_config": asdict(ctx.cfg)} if adm["translated"] else rec  # shallow copy; cache untouched
    ep, value = demo_episode(view, row, ctx, template_sha256=template_sha256, build_digest=build_digest)
    if ctx.turn100.loss_profile != UNIFORM_LOSS_PROFILE:
        value = ctx.relabel(ep)  # only the newly constructed Episode changes; stored records/cache stay intact
    again = ctx.tiered_reward(ep)  # also re-checks the counters through the transition deltas
    if again is None or not math.isclose(again, value, rel_tol=1e-12, abs_tol=1e-12):
        raise DemoMismatch(f"recorded episode {rec.get('episode_id')}: turn100 value {again} != {value}")
    return ep, value, adm


# ---------------------------------------------------------------------------
# Replay and summaries
# ---------------------------------------------------------------------------

class Turn100Replay(StratifiedReplay):
    """StratifiedReplay that also stores ``turn_limit`` episodes, in the ``loss`` stratum."""

    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.episodes_by_outcome = {o: 0 for o in OUTCOMES}
        self._order_turn_limit: list[int] = []  # turn-limit terminal rows per stored episode
        self.turn_limit_rows_live = 0

    def add_episode(self, ep: Episode) -> bool:
        if ep.status not in TURN100_STATUS_STRATUM:
            self.rejected[ep.status] += 1
            return False
        if not ep.transitions:
            self.rejected["empty"] += 1
            return False
        validate_turn100_episode(ep)
        s = TURN100_STATUS_STRATUM[ep.status]
        self.rings[s].extend(ep.transitions)
        self.order.append((s, len(ep.transitions)))
        n_tl = sum(isinstance(t, TurnLimitTransition) for t in ep.transitions)
        self._order_turn_limit.append(n_tl)
        self.turn_limit_rows_live += n_tl
        self.episodes_added[s] += 1
        self.episodes_by_outcome[ep.status] += 1
        while len(self) > self.capacity and self.order_head < len(self.order):
            os_, n = self.order[self.order_head]
            self.rings[os_].popleft(n)
            self.turn_limit_rows_live -= self._order_turn_limit[self.order_head]
            self.order_head += 1
        return True


def validate_turn100_episode(ep: Episode) -> None:
    """``validate_episode`` plus the turn-limit shape (terminal last row carrying the turn-limit reward)."""
    if ep.status != TURN_LIMIT_STATUS:
        if any(isinstance(t, TurnLimitTransition) for t in ep.transitions):
            raise ValueError(f"{ep.template_id}/{ep.env_seed}: {ep.status} episode holds a turn-limit row")
        validate_episode(ep)
        return
    if ep.reason != "max_turns":
        raise ValueError(f"{ep.template_id}/{ep.env_seed}: turn_limit episode with reason {ep.reason!r}")
    validate_episode(dataclasses.replace(ep, status="loss"))  # terminal-last shape, legal actions, finite
    last = ep.transitions[-1]
    if not isinstance(last, TurnLimitTransition) or last.reward != TURN_LIMIT_REWARD:
        raise ValueError(f"{ep.template_id}/{ep.env_seed}: turn_limit episode not relabelled "
                         f"(last reward {last.reward})")
    if any(isinstance(t, TurnLimitTransition) for t in ep.transitions[:-1]):
        raise ValueError(f"{ep.template_id}/{ep.env_seed}: turn-limit row before the last transition")


def summarize_turn100(eps: list[Episode], ctx: Turn100Context, selection=None, seed: int = 0) -> dict:
    """summarize_expansion over outcome-marked episodes, with the turn100 objective and outcome counts."""
    for e in eps:
        if e.status not in REJECTED_STATUSES and e.status != TURN_LIMIT_STATUS:
            ctx.mark_outcome(e)
    out = summarize_expansion(eps, ctx, selection, seed)
    out["turn100_reward"] = {**out.pop("tiered_reward"), "reward_id": ctx.turn100.reward_id,
                             "turn_limit_reward": float(ctx.turn100.turn_limit_reward)}
    if ctx.turn100.loss_profile != UNIFORM_LOSS_PROFILE:
        out["turn100_reward"].update(loss_profile=ctx.turn100.loss_profile,
                                    loss_rewards=boss_act_loss_rewards(ctx.turn100.loss_profile))
    out["turn100_groups"] = out.pop("tiered_groups")
    st = Counter(e.status for e in eps)
    out["turn_limit"] = st[TURN_LIMIT_STATUS]
    out["max_steps_truncations"] = st["truncated"]  # == out["truncations"] once turn limits are marked
    out["outcome_counts"] = {"win": st["win"], "loss": st["loss"], TURN_LIMIT_STATUS: st[TURN_LIMIT_STATUS],
                             "truncated_max_steps": st["truncated"], "errors": out["errors"]}
    out["turn_budget"] = {"allowed_turns": ctx.turn100.allowed_turns, "worker_max_turns": ctx.cfg.max_turns}
    return out


def validation_records(eps: list[Episode], ctx: Turn100Context, *, round_index: int, checkpoint_ref: dict,
                       jobs: list[tuple[str, int]] | None = None) -> tuple[list[dict], list[dict]]:
    """(per-episode records, compact traces of non-natural episodes) for one validation round.

    Call before :func:`summarize_turn100` (``status`` is still the worker status). A record whose
    outcome cannot be established is kept with ``outcome="invalid"`` and its error, never dropped."""
    index = {j: i for i, j in enumerate(jobs)} if jobs is not None else {}
    records, traces = [], []
    for e in eps:
        raw_status = e.status
        try:
            outcome, problem = ctx.outcome(e), None
        except ValueError as exc:
            outcome, problem = "invalid", str(exc)
        reward = None
        if outcome in ("win", "loss", "truncated", TURN_LIMIT_STATUS):
            act, room = ctx.classify(e.template_id)
            reward = (0.0 if outcome == "truncated" else
                      ctx.turn100.terminal_reward(outcome, act=act, room_type=room, entry_hp=e.metrics.entry_hp,
                                                  hp_terminal=e.metrics.hp_terminal,
                                                  max_hp_terminal=e.metrics.max_hp_terminal))
        rec = {"round": round_index, "job": index.get((e.template_id, e.env_seed)),
               "template_id": e.template_id, "env_seed": e.env_seed, "raw_status": raw_status,
               "outcome": outcome, "reason": e.reason, "error": e.error or problem, "steps": e.steps,
               **turn_fields(e, raw_status, ctx.cfg.max_turns), "turn100_reward": reward,
               "legacy_reward_preheal": e.metrics.reward_preheal if e.metrics else None,
               "entry_hp": e.metrics.entry_hp if e.metrics else None,
               "hp_terminal": e.metrics.hp_terminal if e.metrics else None,
               "checkpoint": checkpoint_ref}
        rec["trace_retained"] = outcome not in ("win", "loss")
        records.append(rec)
        if rec["trace_retained"]:
            traces.append({**rec, "trace_columns": ["raw_turn", "kind", "action", "target", "n_legal", "hp",
                                                    "block", "energy"], "trace": e.trace})
    return records, traces


def load_turn100_checkpoint(path: Path, ctx: Turn100Context, *, device: str = "cpu"):
    """Strict load of a turn100-v1 checkpoint (exact reward, lambda, turn budget, build, vocab)."""
    from .checkpoint import load
    if not isinstance(ctx, Turn100Context):
        raise TypeError("turn100-v1 checkpoints load only into a Turn100Context")
    model, blob = load(Path(path), ctx, device=device)
    contract = blob["meta"].get("reward_contract")
    if contract != ctx.reward_contract():
        raise ValueError(f"{path}: checkpoint reward contract {contract} != running {ctx.reward_contract()}")
    return model, blob
