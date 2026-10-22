"""Versioned, immutable pilot configuration (Task 1). Every learning-contract value is pinned: a
diagnostic config may shrink budgets/seed counts, never the reward, learner, replay, model or limits.

v3: a config names its LEARNING PROFILE, the frozen contract it is validated against. Profiles:

* ``repaired-flow-n5-uniform-legacy``: the repaired-flow (v2 decision flow) contract of the v2 config,
  kept explicitly (5-decision returns, uniform recent replay, seed 2026101201, six-hour window);
* ``n5-uniform`` / ``n10-uniform`` / ``n10-tiered``: the matched quality-replay experiment (seed
  2026101301, 5,000 attempts, 128 validation seeds every 500). The three arms run CONCURRENTLY under one
  root session owner: each admits for six hours from the shared session start (RS1_OWNER_STARTED_EPOCH,
  optionally clipped by an absolute ``session_deadline_epoch``) with 2 workers x 4 runs and a 3 GiB host
  reserve (the user's 9 GiB-per-owner plus 3 GiB spare reservation). They differ ONLY in the n-step window
  and the replay policy; ``n10-tiered`` draws its 128 rows 40/32/24/16/16 from nested quality tiers.
* ``n5-uniform-20k`` / ``n10-uniform-20k`` / ``n10-tiered-20k``: the same three arms extended to 20,000
  attempts with 256 validation seeds every 5,000 (the seed streams extend the historical ones). They are
  TARGET-CONTROLLED: admissions end only at ``max_training_attempts`` or a stop, never at a clock.
  ``max_elapsed_hours`` and ``session_deadline_epoch`` are None (JSON null), and no other value is
  accepted. Every other profile keeps a finite ``max_elapsed_hours`` and never accepts None.

A pilot may reduce its workers / runs per worker, never raise either, and may raise its host reserve,
never lower it below its profile's (4 GiB legacy, 3 GiB for the three quality arms).
"""
from __future__ import annotations

import dataclasses
import json
import math
from dataclasses import asdict, dataclass, field
from pathlib import Path

from .contracts import DECISION_FLOW

# v2: the decision flow is part of the learning contract (a v1 config named no flow and ran the v1 menu flow).
# v3: the learning profile and the replay policy are part of it too (a v2 config named neither).
CONFIG_SCHEMA = "rs1/pilot_config/v3"
OBSOLETE_SCHEMAS = {"rs1/pilot_config/v1": "ran the v1 menu decision flow",
                    "rs1/pilot_config/v2": "names no learning profile or replay policy"}
# A future run of the v2 decision flow. The completed 2026-10-05 pilot "rs1-online-e-fixed-s2026101201" ran the
# v1 menu flow: its config/checkpoints are historical evidence of that flow and never load as this one.
PILOT_ID = "rs1-online-e-fixed-flow2-s2026101201"
PILOT_SEED = 2026101201
REWARD_ID = "run-strategy-act-win-v1"
QUALITY_SEED = 2026101301

UNIFORM_REPLAY = "uniform-recent/v1"  # the whole batch uniformly from the recent-row ring
TIERED_REPLAY = "quality-tiers/v1"  # fixed per-tier quotas over nested retrospective quality tiers
REPLAY_POLICIES = (UNIFORM_REPLAY, TIERED_REPLAY)
TIER_NAMES = ("general", "act2", "act3", "act3_boss", "run_victory")
LEARNING_CONTRACT_SCHEMA = "rs1/learning_contract/v1"


@dataclass(frozen=True)
class LearnerConfig:
    gamma: float = 1.0
    n_step: int = 5
    replay_capacity: int = 100_000
    gradient_gate: int = 2000  # valid committed ONLINE strategy transitions before the first update
    batch_size: int = 128
    lr: float = 0.00025
    huber_delta: float = 1.0
    grad_clip: float = 10.0
    target_sync: int = 1000  # hard sync every N accepted updates
    update_every: int = 16  # one update per N newly committed valid transitions (accumulated credit)
    adam_eps: float = 1e-8


@dataclass(frozen=True)
class ReplayConfig:
    """Batch composition. ``uniform-recent/v1``: no counts or capacities (the whole batch from the ring).
    ``quality-tiers/v1``: five per-update counts (tier 0 general ring, then act2+/act3+/act3 boss/won) that
    sum to the batch size, and the four quality tiers' retained-row capacities (whole trajectories)."""
    policy: str = UNIFORM_REPLAY
    batch_counts: tuple[int, ...] = ()
    tier_capacities: tuple[int, ...] = ()

    @staticmethod
    def from_dict(d: dict) -> "ReplayConfig":
        if set(d) != {"policy", "batch_counts", "tier_capacities"}:
            raise ValueError(f"replay config fields {sorted(d)} are not policy/batch_counts/tier_capacities")
        return ReplayConfig(d["policy"], tuple(d["batch_counts"]), tuple(d["tier_capacities"]))


@dataclass(frozen=True)
class ExplorationConfig:
    epsilon_start: float = 0.6
    epsilon_end: float = 0.05
    decay_attempts: int = 3000  # resolved training-run attempts
    random_before_gate: bool = True  # legal uniform random strategy before the gradient gate


@dataclass(frozen=True)
class ModelConfig:
    state_dim: int = 256
    candidate_dim: int = 64
    head_hidden: int = 128  # head 320 -> 128 -> 1
    map_dim: int = 64
    map_passes: int = 2
    card_embed: int = 32
    card_dim: int = 64
    relic_embed: int = 16
    potion_embed: int = 16
    kind_embed: int = 16
    event_embed: int = 16


@dataclass(frozen=True)
class LimitsConfig:
    battle_turns: int = 100
    worker_max_turns: int = 99  # raw zero-based turn 99 is the last allowed decision
    battle_steps: int = 2000
    strategic_decisions: int = 1000
    auto_actions: int = 20_000
    repeated_public_state: int = 25


@dataclass(frozen=True)
class AbortConfig:
    undefined_rate: float = 0.05  # unsupported/error/limit share among resolved attempts
    min_resolved: int = 100


@dataclass(frozen=True)
class PilotConfig:
    schema: str = CONFIG_SCHEMA
    pilot: bool = True
    pilot_id: str = PILOT_ID
    seed: int = PILOT_SEED
    character: str = "Ironclad"
    ascension: int = 2
    progression: str = "all_unlocked"
    reward_id: str = REWARD_ID
    decision_flow: str = DECISION_FLOW
    max_training_attempts: int = 5000
    max_elapsed_hours: float | None = 6.0  # None only for a target-controlled profile (no admission clock)
    validation_seeds: int = 128
    validation_every: int = 500
    diagnostic_seeds: int = 12
    reserved_test_seeds: int = 512
    workers: int = 6
    runs_per_worker: int = 4
    host_reserve_gib: float = 4.0
    device: str = "auto"  # strategy learner device (frozen after the preflight benchmark)
    battle_device: str = "auto"  # frozen E inference device
    torch_threads: int = 1
    batch_deadline_ms: int = 5
    purity_check: bool = False
    # Origin of the admission window (owner start). RS1_OWNER_STARTED_EPOCH overrides it; with neither, the
    # trainer process's own start is used. Never reset at a train call or validation round.
    admission_start_epoch: float | None = None
    # Absolute session admission deadline (epoch seconds) set by a root session owner: an arm admits until
    # min(owner start + max_elapsed_hours, session_deadline_epoch). None: the arm window alone.
    session_deadline_epoch: float | None = None
    learning_profile: str = "repaired-flow-n5-uniform-legacy"
    learner: LearnerConfig = field(default_factory=LearnerConfig)
    replay: ReplayConfig = field(default_factory=ReplayConfig)
    exploration: ExplorationConfig = field(default_factory=ExplorationConfig)
    model: ModelConfig = field(default_factory=ModelConfig)
    limits: LimitsConfig = field(default_factory=LimitsConfig)
    abort: AbortConfig = field(default_factory=AbortConfig)


LEGACY_PROFILE = "repaired-flow-n5-uniform-legacy"
# three concurrent owners (user amendment): a shared six-hour window, 2 workers x 4 runs, a 3 GiB host reserve
_QUALITY_ARM = dict(seed=QUALITY_SEED, max_training_attempts=5000, max_elapsed_hours=6.0, validation_seeds=128,
                    validation_every=500, workers=2, runs_per_worker=4, host_reserve_gib=3.0)
TIERED_QUOTAS = ReplayConfig(TIERED_REPLAY, (40, 32, 24, 16, 16), (20_000, 30_000, 40_000, 50_000))
# the matched 20k extension (user request 2026-10-06): run until all three finish 20,000 attempts, no clock
_QUALITY20K_ARM = dict(_QUALITY_ARM, max_training_attempts=20_000, max_elapsed_hours=None, validation_seeds=256,
                       validation_every=5000)
# name -> (pilot fields, n_step, replay). Everything else is the shared PilotConfig default.
PROFILES = {
    LEGACY_PROFILE: (dict(pilot_id=PILOT_ID, seed=PILOT_SEED, max_elapsed_hours=6.0), 5, ReplayConfig()),
    "n5-uniform": (dict(pilot_id=f"rs1-quality-n5-uniform-s{QUALITY_SEED}", **_QUALITY_ARM), 5, ReplayConfig()),
    "n10-uniform": (dict(pilot_id=f"rs1-quality-n10-uniform-s{QUALITY_SEED}", **_QUALITY_ARM), 10, ReplayConfig()),
    "n10-tiered": (dict(pilot_id=f"rs1-quality-n10-tiered-s{QUALITY_SEED}", **_QUALITY_ARM), 10, TIERED_QUOTAS),
    "n5-uniform-20k": (dict(pilot_id=f"rs1-quality20k-n5-uniform-s{QUALITY_SEED}", **_QUALITY20K_ARM), 5,
                       ReplayConfig()),
    "n10-uniform-20k": (dict(pilot_id=f"rs1-quality20k-n10-uniform-s{QUALITY_SEED}", **_QUALITY20K_ARM), 10,
                        ReplayConfig()),
    "n10-tiered-20k": (dict(pilot_id=f"rs1-quality20k-n10-tiered-s{QUALITY_SEED}", **_QUALITY20K_ARM), 10,
                       TIERED_QUOTAS),
    # Fresh matched 2 x 2 requested after the preserved20k worker failure: four vs five decisions,
    # uniform vs tiered replay. Separate run identities, same initialization and seed streams.
    "n5-uniform-n4n5-20k": (dict(pilot_id=f"rs1-quality45-n5-uniform-s{QUALITY_SEED}", **_QUALITY20K_ARM), 5,
                            ReplayConfig()),
    "n5-tiered-n4n5-20k": (dict(pilot_id=f"rs1-quality45-n5-tiered-s{QUALITY_SEED}", **_QUALITY20K_ARM), 5,
                           TIERED_QUOTAS),
    "n4-uniform-n4n5-20k": (dict(pilot_id=f"rs1-quality45-n4-uniform-s{QUALITY_SEED}", **_QUALITY20K_ARM), 4,
                            ReplayConfig()),
    "n4-tiered-n4n5-20k": (dict(pilot_id=f"rs1-quality45-n4-tiered-s{QUALITY_SEED}", **_QUALITY20K_ARM), 4,
                           TIERED_QUOTAS),
}
# Profiles whose admissions end only at max_training_attempts (or a stop): no arm window, no session deadline.
TARGET_CONTROLLED_PROFILES = ("n5-uniform-20k", "n10-uniform-20k", "n10-tiered-20k",
                              "n5-uniform-n4n5-20k", "n5-tiered-n4n5-20k",
                              "n4-uniform-n4n5-20k", "n4-tiered-n4n5-20k")
TARGET_MODE, FINITE_MODE = "target_controlled", "finite_deadline"

# The CPU-throughput continuation (user amendment 2026-10-07: "Just try to fully use CPU and then continue to do exp.
# I approve all of your design."): the same four matched learners, gracefully stopped by the user at an actual drained
# boundary R (different per arm), resumed by run_strategy_v1.continue_train from that verified state to 60,000
# cumulative attempts. Every field equals the source profile's except the attempt target and the torch intra-op
# thread count, which only this CPU performance amendment may set (one measured value for all four arms; the
# placeholder is the source's 1). The pilot id is kept, so training run ids continue at t{R:05d}.
CONTINUATION_TARGET = 60_000
ALLOWED_TORCH_THREADS = (1, 2, 3, 4)
CONTINUES = {f"{src[:-len('-20k')]}-cpu60k": src for src in ("n5-uniform-n4n5-20k", "n5-tiered-n4n5-20k",
                                                            "n4-uniform-n4n5-20k", "n4-tiered-n4n5-20k")}
for _new, _src in CONTINUES.items():
    _fields, _n_step, _replay = PROFILES[_src]
    PROFILES[_new] = (dict(_fields, max_training_attempts=CONTINUATION_TARGET), _n_step, _replay)
del _new, _src, _fields, _n_step, _replay
TARGET_CONTROLLED_PROFILES = (*TARGET_CONTROLLED_PROFILES, *CONTINUES)


def cpu_threads_reference(cfg: "PilotConfig", ref: "PilotConfig") -> "PilotConfig":
    """The approved reference for ``validate``: a CPU-amendment continuation profile must carry one of the allowed
    torch thread counts (a plain int; a bool is refused, though ``True == 1``); every other profile is compared with
    its unchanged reference."""
    t = cfg.torch_threads
    if cfg.learning_profile in CONTINUES:
        if type(t) is not int or t not in ALLOWED_TORCH_THREADS:
            raise ValueError(f"torch_threads {t!r} of {cfg.learning_profile} is not one of {ALLOWED_TORCH_THREADS}")
        return dataclasses.replace(ref, torch_threads=t)
    return ref


def profile_config(name: str = LEGACY_PROFILE) -> PilotConfig:
    """The exact frozen config of a named learning profile."""
    if name not in PROFILES:
        raise ValueError(f"unknown learning profile {name!r} (known: {sorted(PROFILES)})")
    fields_, n_step, replay = PROFILES[name]
    return PilotConfig(learning_profile=name, learner=LearnerConfig(n_step=n_step), replay=replay, **fields_)


def pilot_config(profile: str = LEGACY_PROFILE) -> PilotConfig:
    return profile_config(profile)


def target_controlled(cfg: "PilotConfig") -> bool:
    """True for a profile that admits until its attempt target with no admission clock."""
    return cfg.learning_profile in TARGET_CONTROLLED_PROFILES


def admission_mode(cfg: "PilotConfig") -> str:
    return TARGET_MODE if target_controlled(cfg) else FINITE_MODE


def admission_deadlines(cfg: "PilotConfig", start_epoch: float) -> tuple[float | None, float | None]:
    """(arm deadline, admission deadline) for an owner started at ``start_epoch``. Finite profiles: the arm
    window ``start + max_elapsed_hours``, clipped by the absolute session deadline. Target-controlled
    profiles: (None, None), for a finite start only."""
    if target_controlled(cfg):
        if isinstance(start_epoch, bool) or not isinstance(start_epoch, (int, float)) or not math.isfinite(start_epoch):
            raise ValueError(f"target-controlled owner start {start_epoch!r} is not a finite epoch")
        return None, None
    arm = start_epoch + cfg.max_elapsed_hours * 3600.0
    return arm, arm if cfg.session_deadline_epoch is None else min(arm, float(cfg.session_deadline_epoch))


def _validate_clock(cfg: "PilotConfig") -> None:
    """The admission clock belongs to the profile. A diagnostic label never changes it."""
    h = cfg.max_elapsed_hours
    if target_controlled(cfg):
        if h is not None:
            raise ValueError(f"target-controlled profile {cfg.learning_profile} admits until max_training_attempts: "
                             f"max_elapsed_hours must be None (JSON null), not {h!r}")
        if cfg.session_deadline_epoch is not None:
            raise ValueError(f"target-controlled profile {cfg.learning_profile} accepts no session deadline "
                             f"({cfg.session_deadline_epoch!r})")
    elif h is None or isinstance(h, bool) or not isinstance(h, (int, float)) or not math.isfinite(h):
        raise ValueError(f"profile {cfg.learning_profile} has a finite admission clock: max_elapsed_hours {h!r} must "
                         f"be a finite number of hours (no-cap is only for {TARGET_CONTROLLED_PROFILES})")


def configured_counts(replay: ReplayConfig, batch_size: int) -> list[int]:
    """Per-update rows by tier: the explicit tiered counts, or the whole batch from tier 0."""
    return list(replay.batch_counts) if replay.policy == TIERED_REPLAY else [batch_size]


def validate_replay(replay: ReplayConfig, learner: LearnerConfig, limits: LimitsConfig) -> None:
    """A replay config every sampler/audit can interpret exactly; anything else is refused."""
    if replay.policy not in REPLAY_POLICIES:
        raise ValueError(f"replay policy {replay.policy!r} is not one of {REPLAY_POLICIES}")
    ints = lambda xs: isinstance(xs, tuple) and all(isinstance(x, int) and not isinstance(x, bool) for x in xs)  # noqa: E731
    if not ints(replay.batch_counts) or not ints(replay.tier_capacities):
        raise ValueError("replay batch_counts / tier_capacities must be tuples of integers")
    if replay.policy == UNIFORM_REPLAY:
        if replay.batch_counts or replay.tier_capacities:
            raise ValueError(f"{UNIFORM_REPLAY} takes no tier counts or capacities (the whole batch is uniform)")
        return
    if len(replay.batch_counts) != len(TIER_NAMES) or len(replay.tier_capacities) != len(TIER_NAMES) - 1:
        raise ValueError(f"{TIERED_REPLAY} needs {len(TIER_NAMES)} batch counts {TIER_NAMES} and "
                         f"{len(TIER_NAMES) - 1} quality-tier capacities")
    if min(replay.batch_counts) < 0 or sum(replay.batch_counts) != learner.batch_size:
        raise ValueError(f"tier batch counts {replay.batch_counts} must be nonnegative and sum to the batch "
                         f"size {learner.batch_size}")
    if min(replay.tier_capacities) < limits.strategic_decisions:
        raise ValueError(f"tier capacities {replay.tier_capacities} must each hold a whole trajectory "
                         f"(>= {limits.strategic_decisions} strategic decisions)")


def replay_row_bound(cfg: "PilotConfig", learner: LearnerConfig | None = None,
                     replay: ReplayConfig | None = None) -> int:
    """Worst-case DISTINCT retained rows: the ring plus every quality tier's capacity."""
    learner, replay = learner or cfg.learner, replay or cfg.replay
    return learner.replay_capacity + sum(replay.tier_capacities)


def learning_contract(profile: str, learner: LearnerConfig, replay: ReplayConfig) -> dict:
    """The n-step/replay contract a run learned under, named in manifest, startup, checkpoints and summary."""
    tiered = replay.policy == TIERED_REPLAY
    counts = configured_counts(replay, learner.batch_size)
    return {
        "schema": LEARNING_CONTRACT_SCHEMA, "profile": profile, "n_step": learner.n_step, "gamma": learner.gamma,
        "return": f"sum of rewards over up to {learner.n_step} real strategy decisions; bootstrap only at the next "
                  "real decision; stop at a natural terminal; undefined runs contribute no rows",
        "replay_policy": replay.policy, "batch_size": learner.batch_size, "batch_counts": counts,
        "tier_names": list(TIER_NAMES[:len(counts)]), "tier0_capacity_rows": learner.replay_capacity,
        "tier_capacities_rows": list(replay.tier_capacities),
        "distinct_row_bound": learner.replay_capacity + sum(replay.tier_capacities),
        "tiers": ("nested, from the resolved natural training outcome: 0 every valid trace; 1 actual act >= 2; "
                  "2 actual act 3; 3 an actual Act 3 room=boss combat entry; 4 native run victory") if tiered else None,
        "retention": ("tier 0 recent-row ring; tiers 1-4 whole-trajectory FIFO references within their row "
                      "capacities, oldest first") if tiered else "recent-row ring",
        "sampling": ("tier 0 uniform rows; each quality tier a uniform retained trajectory, then a uniform row; "
                     "with replacement; never consumed") if tiered else "uniform rows with replacement",
        "empty_tier_fallback": "an empty tier's quota moves one tier lower, recursively" if tiered else None,
        "objective": ("explicitly success-weighted Huber over the sampled mixture; no importance correction; "
                      "no TD-error priority") if tiered else "Huber over uniform recent replay",
        "update_credit": "each unique valid online row once; memberships and draws add no credit",
    }


_NESTED = {"learner": LearnerConfig, "exploration": ExplorationConfig, "model": ModelConfig,
           "limits": LimitsConfig, "abort": AbortConfig}
# Budget/scale fields a diagnostic run may change. Everything else is the learning contract.
DIAGNOSTIC_FIELDS = {"pilot", "pilot_id", "max_training_attempts", "max_elapsed_hours", "validation_seeds",
                     "validation_every", "diagnostic_seeds", "reserved_test_seeds", "workers", "runs_per_worker",
                     "device", "battle_device", "batch_deadline_ms", "purity_check", "seed", "host_reserve_gib",
                     "admission_start_epoch", "session_deadline_epoch"}
# Fields a PILOT config may still set (devices frozen by the preflight, scale, clocks).
PILOT_FREE_FIELDS = {"device", "battle_device", "workers", "runs_per_worker", "purity_check", "batch_deadline_ms",
                     "host_reserve_gib", "admission_start_epoch", "session_deadline_epoch"}


def validate(cfg: PilotConfig) -> None:
    if cfg.schema != CONFIG_SCHEMA:
        why = OBSOLETE_SCHEMAS.get(cfg.schema, "unknown schema")
        raise ValueError(f"config schema {cfg.schema!r} is not {CONFIG_SCHEMA} ({why})")
    ref = profile_config(cfg.learning_profile)  # ValueError for an unknown profile
    ref = cpu_threads_reference(cfg, ref)  # CPU amendment: the measured torch thread count only
    for f in dataclasses.fields(PilotConfig):
        if f.name in DIAGNOSTIC_FIELDS or f.name == "schema":
            continue
        if getattr(cfg, f.name) != getattr(ref, f.name):
            if f.name in _NESTED:
                diff = [k for k, v in asdict(getattr(cfg, f.name)).items() if asdict(getattr(ref, f.name))[k] != v]
                raise ValueError(f"{f.name}.{','.join(diff)} differs from the approved learning contract")
            raise ValueError(f"{f.name} {getattr(cfg, f.name)!r} differs from the approved {getattr(ref, f.name)!r}")
    validate_replay(cfg.replay, cfg.learner, cfg.limits)
    _validate_clock(cfg)
    for k in ("admission_start_epoch", "session_deadline_epoch"):
        v = getattr(cfg, k)
        if v is not None and (isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v)):
            raise ValueError(f"{k} {v!r} must be a finite epoch or None")
    if cfg.pilot:
        for k in DIAGNOSTIC_FIELDS - PILOT_FREE_FIELDS:
            if getattr(cfg, k) != getattr(ref, k):
                raise ValueError(f"pilot {k} {getattr(cfg, k)!r} differs from the approved {getattr(ref, k)!r}")
        if cfg.workers > ref.workers or cfg.runs_per_worker > ref.runs_per_worker:
            raise ValueError(f"pilot admission slots {cfg.workers}x{cfg.runs_per_worker} may only be reduced from "
                             f"the {cfg.learning_profile} profile's {ref.workers}x{ref.runs_per_worker}")
        if not cfg.host_reserve_gib >= ref.host_reserve_gib:
            raise ValueError(f"pilot host reserve {cfg.host_reserve_gib!r} GiB must stay at least the "
                             f"{cfg.learning_profile} profile's {ref.host_reserve_gib} GiB")
    if cfg.workers < 1 or cfg.runs_per_worker < 1 or cfg.validation_seeds < 1 or cfg.validation_every < 1:
        raise ValueError("workers/runs_per_worker/validation counts must be positive")


def to_dict(cfg: PilotConfig) -> dict:
    return asdict(cfg)


def from_dict(d: dict) -> PilotConfig:
    kw = dict(d)
    if kw.get("schema") != CONFIG_SCHEMA or "decision_flow" not in kw:
        raise ValueError(f"config schema {kw.get('schema')!r} / decision flow {kw.get('decision_flow')!r}: "
                         f"only {CONFIG_SCHEMA} configs naming their decision flow load")
    names = {f.name for f in dataclasses.fields(PilotConfig)}
    if set(kw) != names:  # a v3 config names every field: nothing is filled in silently
        raise ValueError(f"config fields differ from {CONFIG_SCHEMA}: missing {sorted(names - set(kw))}, "
                         f"unknown {sorted(set(kw) - names)}")
    for k, t in _NESTED.items():
        kw[k] = t(**kw[k])
    kw["replay"] = ReplayConfig.from_dict(kw["replay"])
    return PilotConfig(**kw)


def save_config(cfg: PilotConfig, path: Path) -> None:
    Path(path).write_text(json.dumps(to_dict(cfg), indent=1, sort_keys=True) + "\n", encoding="utf-8")


def load_config(path: Path, *, require_pilot: bool = False) -> PilotConfig:
    cfg = from_dict(json.loads(Path(path).read_text(encoding="utf-8")))
    validate(cfg)
    if require_pilot and not cfg.pilot:
        raise ValueError("this command needs the pilot config (pilot: true)")
    return cfg


def main(argv=None) -> int:
    """``python -m run_strategy_v1.config --emit PATH [--profile NAME] [--diagnostic]``: write a profile's exact config."""
    import argparse
    ap = argparse.ArgumentParser(prog="python -m run_strategy_v1.config")
    ap.add_argument("--emit", type=Path, required=True)
    ap.add_argument("--profile", choices=sorted(PROFILES), default=LEGACY_PROFILE)
    ap.add_argument("--diagnostic", action="store_true", help="a small nonpilot config (budgets only)")
    a = ap.parse_args(argv)
    cfg = profile_config(a.profile)
    if a.diagnostic:
        cfg = dataclasses.replace(cfg, pilot=False, pilot_id="rs1-diagnostic", max_training_attempts=40,
                                  max_elapsed_hours=None if target_controlled(cfg) else 0.5, validation_seeds=8,
                                  validation_every=20, workers=2, runs_per_worker=4)
    validate(cfg)
    save_config(cfg, a.emit)
    print(a.emit)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
