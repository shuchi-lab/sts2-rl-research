"""Python environment over the interactive worker.

``CombatEnv`` is the single-fight API (reset(template, seed) -> observation + legal options;
step(option) -> next state, outcome, truncation, reward). ``Rollout`` drives many fights in
one worker process with batched policy calls, which is what collection, training and
evaluation use. Both return the same :class:`Step` records.

Discounting: gamma ** (turn_after - turn_before). Steps inside one player turn, including
card-selection micro-steps, have delta 0 and are not discounted.
"""

from __future__ import annotations

import gzip
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable

import numpy as np

from . import paths
from .features import Adapter, Obs, UnsupportedState
from .reward import EpisodeMetrics, RewardSpec, episode_metrics, training_reward
from .vocab import Vocab, build_vocab
from .worker import Worker, build_digest

DONE_STATUSES = ("win", "loss", "terminal_other")
# First-combat scope validated against the 14 retained starts and the 1,400-fight probe.
# The full 253-ID vocabulary remains available, but an unreviewed effect stops training.
FIRST_PLAYER_EFFECTS = frozenset(("Weak", "Strength", "Shrink", "Frail"))
FIRST_ENEMY_EFFECTS = frozenset(("Vulnerable", "Strength", "Thorns", "Ravenous", "Stunned"))
# Effect scope -> (vocabulary schema, feature schema). "three-act-v1" is the full public-state
# encoder (crl.vocab/v3-three-act + crl.features/v3); "three-act-214-v1" is the same encoder over
# exactly the 214 retained card identities; the other two keep the v2 contract.
EFFECT_SCOPES = {
    "first-audited-v1": ("crl.vocab/v2", "crl.features/v2"),
    "exploratory": ("crl.vocab/v2", "crl.features/v2"),
    "three-act-v1": ("crl.vocab/v3-three-act", "crl.features/v3"),
    "three-act-214-v1": ("crl.vocab/v3-three-act-214", "crl.features/v3"),
}
# Scopes whose card vocabulary is the restricted roster: starting decks must already fit it.
ROSTER_ONLY_SCOPES = frozenset(("three-act-214-v1",))


@dataclass
class EnvConfig:
    max_steps: int = 600
    max_turns: int = 60
    rng_mode: str = "training"
    reward_variant: str = "preheal"
    purity_check: bool = False
    # "exploratory" permits mapped, not yet audited effects (v2 encoder); "three-act-v1" selects
    # the full-catalog vocabulary and the v3 public-state encoder; "three-act-214-v1" the v3
    # encoder over the 214-card roster (non-roster starting cards rejected, generated ones fail).
    effect_scope: str = "first-audited-v1"


class Context:
    """Worker process + vocabulary + adapter + reward spec + loaded templates."""

    def __init__(self, templates: Iterable[dict], cfg: EnvConfig | None = None, catalog: dict | None = None):
        self.cfg = cfg or EnvConfig()
        if self.cfg.effect_scope not in EFFECT_SCOPES:
            raise ValueError(f"unknown effect scope {self.cfg.effect_scope}")
        self.catalog = catalog if catalog is not None else paths.read_json(paths.CATALOG)
        self.worker = Worker()
        try:
            self.vocab: Vocab = build_vocab(self.worker.hello, self.catalog, scope=self.cfg.effect_scope)
            self.adapter = Adapter(self.vocab, self.worker.hello)
        except BaseException:
            self.worker.close()
            raise
        expected = EFFECT_SCOPES[self.cfg.effect_scope]
        got = (self.vocab.schema, self.adapter.describe()["schema"])
        if got != expected:
            raise AssertionError(f"scope {self.cfg.effect_scope} built {got}, expected {expected}")
        self.reward = RewardSpec(self.catalog, self.cfg.reward_variant)
        self.templates: dict[str, dict] = {}
        try:
            for t in templates:
                if self.cfg.effect_scope in ROSTER_ONLY_SCOPES:
                    self._check_roster_deck(t)
                self.templates[t["template_id"]] = t
                self.worker.add_template(t["template_id"], t["engine_input"])
        except BaseException:
            self.worker.close()
            raise

    def _check_roster_deck(self, template: dict) -> None:
        names = {c["Id"]: c["Entry"] for c in self.catalog["cards"]}
        foreign = [(i, d["DefId"], names.get(d["DefId"], "?")) for i, d in enumerate(template["engine_input"]["Deck"])
                   if d["DefId"] not in self.vocab.card_index]
        if foreign:
            raise ValueError(f"template {template['template_id']}: starting Deck has {len(foreign)} card(s) outside "
                             f"the 214-card roster (index, DefId, entry): {foreign}")

    @property
    def encoder_scope(self) -> dict:
        """What this context encodes: scope, vocabulary and feature schema, worker observation."""
        return {
            "effect_scope": self.cfg.effect_scope,
            "vocab_schema": self.vocab.schema,
            "feature_schema": self.adapter.describe()["schema"],
            "observation_schema": self.worker.hello["observation_schema"],
            "vocab_sizes": self.vocab.sizes,
        }

    def provenance(self) -> dict:
        h = self.worker.hello
        out = {
            "encoder_scope": self.encoder_scope,
            "worker_protocol": h["protocol"],
            "engine_assembly_sha256": h["engine_assembly_sha256"],
            "cds_worker_assembly_sha256": h["cds_worker_assembly_sha256"],
            "rl_worker_assembly_sha256": h["rl_worker_assembly_sha256"],
            "worker_build_digest": build_digest(),
            "catalog_sha256": self.vocab.catalog_sha256,
            "vocab_digest": self.vocab.describe()["digest"],
            "feature_schema": self.adapter.describe(),
            "feature_schema_digest": paths.digest(self.adapter.describe()),
            "reward": self.reward.describe(),
            "env_config": self.cfg.__dict__,
            "terminal_boundary": h["terminal_boundary"],
            "seed_derivation": h["seed_derivation"],
        }
        if self.cfg.effect_scope in ROSTER_ONLY_SCOPES:  # older scopes keep their exact keys
            x = self.vocab.extra
            out["card_vocabulary"] = {
                "roster_path": str(paths.ROSTER), "roster_sha256": x["roster_sha256"],
                "roster_digest": x["roster_digest"], "roster_cards": x["roster_cards"],
                "model_card_rows": self.vocab.sizes["cards"],
                "acquisition_restrictions": x["acquisition_restrictions"],
            }
        return out

    def close(self) -> None:
        self.worker.close()


@dataclass
class Step:
    """One worker response, decoded."""

    raw: dict
    status: str  # running / win / loss / truncated / error / terminal_other / unsupported
    obs: Obs | None  # encoded current decision state (None when there is nothing to decide)
    done: bool  # terminal: no bootstrap
    truncated: bool  # ended by a step/turn limit: bootstrap from obs
    turn: int
    error: str | None = None
    metrics: EpisodeMetrics | None = None

    @property
    def ended(self) -> bool:
        return self.status != "running"


def decode(ctx: Context, r: dict) -> Step:
    status = r["status"]
    obs_raw = r.get("obs")
    turn = obs_raw["turn"] if obs_raw else 0
    err = r.get("error")
    obs = None
    if status in ("running", "truncated") and obs_raw is not None:
        try:
            if ctx.cfg.effect_scope == "first-audited-v1":
                owners = [("player", obs_raw["player"]["buffs"], FIRST_PLAYER_EFFECTS)]
                owners += [(f"enemy {i}", e["buffs"], FIRST_ENEMY_EFFECTS) for i, e in enumerate(obs_raw["enemies"])]
                for owner, buffs, allowed in owners:
                    for bid, _ in buffs:
                        name = ctx.vocab.buff_names[bid] if 0 <= bid < len(ctx.vocab.buff_names) else str(bid)
                        if name not in allowed:
                            raise UnsupportedState(f"{owner} effect {name} is outside the audited first-combat scope")
            obs = ctx.adapter.encode(obs_raw, r["entry_hp"])
        except UnsupportedState as e:
            status, err = "unsupported", str(e)
    if status != "running" and status != "error" and r.get("foreign_rng_streams", 0):
        status, err = "error", f"foreign RNG streams {r['foreign_rng_streams']}"
    if r.get("purity_violations", 0):
        status, err = "error", f"observer purity violations {r['purity_violations']}"
    done = status in DONE_STATUSES
    st = Step(raw=r, status=status, obs=obs, done=done, truncated=status == "truncated", turn=turn, error=err)
    if status != "running":
        p = obs_raw["player"] if obs_raw else {"hp": 0, "max_hp": r["entry_max_hp"]}
        relics = [x[0] for x in obs_raw["relics"]] if obs_raw else []
        st.metrics = episode_metrics(ctx.reward, status, r["entry_hp"], r["entry_max_hp"], p["hp"], p["max_hp"], relics)
    return st


class CombatEnv:
    """Single fight on a shared Context (env slot ``slot`` of its worker)."""

    def __init__(self, ctx: Context, slot: int = 0):
        self.ctx, self.slot = ctx, slot
        self.last: Step | None = None

    def reset(self, template_id: str, env_seed: int) -> Step:
        c = self.ctx.cfg
        r = self.ctx.worker.reset(self.slot, template_id, env_seed, max_steps=c.max_steps,
                                  max_turns=c.max_turns, rng_mode=c.rng_mode, purity_check=c.purity_check)
        self.last = decode(self.ctx, r)
        return self.last

    def legal(self) -> list[dict]:
        return self.last.raw["obs"]["legal"] if self.last and self.last.raw.get("obs") else []

    def step(self, option: int) -> tuple[Step, float]:
        """Execute legal option ``option``; returns (next step, reward)."""
        if self.last is None or self.last.ended:
            raise RuntimeError("episode is over; reset first")
        r = self.ctx.worker.step(self.slot, self.last.raw["decision"], option)
        self.last = decode(self.ctx, r)
        reward = training_reward(self.ctx.reward, self.last.metrics) if self.last.ended else 0.0
        return self.last, reward


@dataclass
class Transition:
    obs: Obs
    action: int
    reward: float
    next_obs: Obs | None  # None when done (terminal)
    done: bool
    delta_turn: int


@dataclass
class Episode:
    template_id: str
    env_seed: int
    status: str
    reason: str | None
    error: str | None
    metrics: EpisodeMetrics
    steps: int
    turns: int
    transitions: list[Transition] = field(default_factory=list)
    out_of_roster_card_states: int = 0
    trace: list | None = None

    @property
    def stratum(self) -> str:
        return self.status if self.status in ("win", "loss") else "other"

    def summary(self) -> dict:
        return {
            "template_id": self.template_id, "env_seed": self.env_seed, "status": self.status,
            "reason": self.reason, "error": self.error, "steps": self.steps, "turns": self.turns,
            "out_of_roster_card_states": self.out_of_roster_card_states, **self.metrics.as_dict(),
        }


@dataclass
class ActItem:
    slot: int
    obs: Obs
    raw: dict
    rng: np.random.Generator


Policy = Callable[[list[ActItem]], list[int]]


class Rollout:
    """Run (template, seed) jobs on ``n_parallel`` env slots with batched policy calls."""

    def __init__(self, ctx: Context, n_parallel: int = 16):
        self.ctx, self.n = ctx, n_parallel

    def run(
        self,
        jobs: Iterable[tuple[str, int]],
        policy: Policy,
        *,
        policy_seed: int = 0,
        keep_transitions: bool = False,
        trace: bool = False,
        on_episode: Callable[[Episode], None] | None = None,
        on_tick: Callable[[int], None] | None = None,
        trajectory_path: Path | None = None,
    ) -> list[Episode]:
        ctx, cfg = self.ctx, self.ctx.cfg
        jobs_iter = iter(jobs)
        slots: dict[int, dict] = {}
        done_eps: list[Episode] = []
        traj = gzip.open(trajectory_path, "at", encoding="utf-8") if trajectory_path else None

        def start(slot: int) -> bool:
            while True:
                try:
                    tid, seed = next(jobs_iter)
                except StopIteration:
                    return False
                r = ctx.worker.reset(slot, tid, seed, max_steps=cfg.max_steps, max_turns=cfg.max_turns,
                                     rng_mode=cfg.rng_mode, purity_check=cfg.purity_check)
                st = decode(ctx, r)
                rng = np.random.default_rng(paths.derive_seed(policy_seed, f"policy|{tid}", seed))
                s = {"tid": tid, "seed": seed, "rng": rng, "step": st, "trans": [], "oor": 0,
                     "trace": [] if (trace or traj) else None}
                slots[slot] = s
                if st.ended:  # terminal on entry, or unsupported initial state
                    finish(slot, st)
                    continue
                return True

        def finish(slot: int, st: Step) -> None:
            s = slots.pop(slot)
            ep = Episode(
                template_id=s["tid"], env_seed=s["seed"], status=st.status, reason=st.raw.get("reason"),
                error=st.error, metrics=st.metrics, steps=st.raw["steps"], turns=st.turn,
                transitions=s["trans"], out_of_roster_card_states=s["oor"], trace=s["trace"],
            )
            if traj is not None:
                traj.write(json.dumps({**ep.summary(), "trace": s["trace"]}, separators=(",", ":")) + "\n")
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
                n_opt = it.obs.n_options
                if not 0 <= a < n_opt:
                    raise ValueError(f"policy chose {a} of {n_opt} options")
                req.append((it.slot, it.raw["decision"], int(a)))
            results = ctx.worker.step_many(req)
            for (slot, _, a), r in zip(req, results):
                s = slots[slot]
                prev: Step = s["step"]
                st = decode(ctx, r)
                if s["trace"] is not None:
                    o = prev.raw["obs"]["legal"][a]
                    p = prev.raw["obs"]["player"]
                    s["trace"].append([prev.turn, o["kind"], o["action"], o["target"], len(prev.raw["obs"]["legal"]),
                                       p["hp"], p["block"], p["energy"]])
                s["oor"] += int(prev.obs.out_of_roster_cards > 0)
                if keep_transitions or on_episode:
                    reward = training_reward(ctx.reward, st.metrics) if st.ended and st.status != "unsupported" else 0.0
                    s["trans"].append(Transition(
                        obs=prev.obs, action=a, reward=reward,
                        next_obs=None if st.done else st.obs, done=st.done,
                        delta_turn=max(0, st.turn - prev.turn) if st.obs is not None or st.done else 0,
                    ))
                s["step"] = st
                if st.ended:
                    finish(slot, st)
                    start(slot)
            if on_tick:
                on_tick(len(req))
        if traj is not None:
            traj.close()
        return done_eps


def episode_jobs(template_ids: list[str], n_episodes: int, base_seed: int, tag: str) -> list[tuple[str, int]]:
    """Round-robin templates with fresh derived env seeds: job i -> (templates[i % T], seed_i)."""
    return [(template_ids[i % len(template_ids)], paths.derive_seed(base_seed, tag, i)) for i in range(n_episodes)]
