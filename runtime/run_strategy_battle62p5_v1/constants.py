"""Fixed contract of the four-arm Choice training trial with frozen Battle E62.5k (stdlib only; no torch / crl).

Every value is copied from the human-authorized request (``AUTHORIZED_REQUEST.json`` / ``SPEC.md`` of the
trial) or read once, read-only, from the two retained input files. Nothing in ``run_strategy_v1``,
``act1_battle_finetune_v1`` or ``battle_mixed_finetune_v1`` is changed; this package names its own profiles,
seeds, labels and schemas.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent
SCHEMA_PREFIX = "rs62"
TRIAL_ID = "run-strategy-battle62p5-four-arm-60k-20261008-v1"
TRIAL_RELATIVE = f"experiments/trials/{TRIAL_ID}"

# ---------------------------------------------------------------------------------------------- budget
NEW_ATTEMPTS = 60_000  # exactly this many NEW training attempts per arm (ids t00000..t59999 once each)
CHECKPOINT_EVERY = 5_000  # NEW resolved attempts between drained weight/state checkpoints
CHECKPOINTS_PER_ARM = NEW_ATTEMPTS // CHECKPOINT_EVERY  # 12
KEEP_REPLAY_SNAPSHOTS = 3  # latest three replay snapshots per arm; older ones are rotated explicitly
MASTER_SEED = 2026100809  # fresh matched training seed stream shared by the four arms

# ---------------------------------------------------------------------------------------------- arms
UNIFORM, TIERED = "uniform-recent/v1", "quality-tiers/v1"
TIER_CAPACITIES = (20_000, 30_000, 40_000, 50_000)  # original nested whole-trajectory FIFO row capacities
STANDARD_COUNTS = (40, 32, 24, 16, 16)
HALF_COUNTS = (84, 16, 12, 8, 8)  # every quality quota halved; the 44 freed rows go to general tier 0
UNIFORM_COUNTS = (128, 0, 0, 0, 0)  # the request's notation: the whole batch from the recent ring

ARMS = {
    "n5-uniform": {"n_step": 5, "replay": UNIFORM, "batch_counts": UNIFORM_COUNTS, "start": 0,
                   "source": None, "exploration": "fresh"},
    "n5-tiered": {"n_step": 5, "replay": TIERED, "batch_counts": STANDARD_COUNTS, "start": 0,
                  "source": None, "exploration": "fresh"},
    "n5-half-tiered": {"n_step": 5, "replay": TIERED, "batch_counts": HALF_COUNTS, "start": 0,
                       "source": None, "exploration": "fresh"},
    "n4-tiered-from20k": {"n_step": 4, "replay": TIERED, "batch_counts": STANDARD_COUNTS, "start": 20_000,
                          "source": "choice20k", "exploration": "finetune"},
}
ARM_ORDER = ("n5-uniform", "n5-tiered", "n5-half-tiered", "n4-tiered-from20k")

# Exploration. fresh: the original 0.6 -> 0.05 over 3,000 NEW resolved attempts, uniform legal random before the
# 2,000-row gradient gate. finetune: 0.05 from the first game, the trained policy acts before the new gate.
EXPLORATION = {
    "fresh": {"epsilon_start": 0.6, "epsilon_end": 0.05, "decay_attempts": 3000, "random_before_gate": True},
    "finetune": {"epsilon_start": 0.05, "epsilon_end": 0.05, "decay_attempts": 3000, "random_before_gate": False},
}

# Topology (measured four-arm CPU-actor / CUDA-learner design).
LEARNER_DEVICE = "cuda"
BATTLE_DEVICE = "cpu"
LEARNER_TORCH_THREADS = 4  # the measured n4/n5 cpu60k configs' optimizer scope
ACTOR_TORCH_THREADS = 1
WORKERS_PER_ARM = 2
RUNS_PER_WORKER = 4
HOST_RESERVE_GIB = 3.0
ACTOR_RPC_TIMEOUT_S = 900.0  # equals the native worker timeout; a timeout fails the arm, it is never retried

# Resources (SPEC): startup >= 33 GiB available RAM AND commit, >= 20 GiB free disk; below 3 GiB RAM or commit
# headroom the controller closes NEW admissions in all four arms; admitted healthy games drain.
START_MIN_RAM_GIB = 33.0
START_MIN_COMMIT_GIB = 33.0
START_MIN_DISK_GIB = 20.0
LOW_GIB = 3.0
TELEMETRY_INTERVAL_S = 20.0
GATE_MAX_AGE_S = 15 * 60

HOST_PYTHON = "C:/Users/Chi/anaconda3/envs/sts2-rl/python.exe"
WINDOWS_WORKSPACE = "C:/Users/Chi/Projects/STS2_ML/combat_rl_choice_battle62p5_four_arm_20261008_v1"
FAST_RUNTIME_SOURCE = "C:/Users/Chi/Projects/STS2_ML/combat_rl_cpu_actor_gpu_learner_feasibility_20261008_v1/runtime"
DOTNET_ROOT = "C:/Users/Chi/Projects/STS2_ML/combat_rl/.tools/dotnet"

# ---------------------------------------------------------------------------------------------- inputs
BATTLE_STAGED_RELATIVE = "inputs/battle/e062500.pt"
CHOICE20K_STAGED_RELATIVE = "inputs/choice/n4_tiered_choice20k_round005_model.pt"

# Frozen Battle: the ONLINE weights of e47p5_mix20 after 15,000 new mixed battles (E47.5k + 15k = E62.5k).
BATTLE = {
    "file_sha256": "593955d3872bd4e93af418d323eea76f80701391e5e37fae0ad08d5f9a25e35e",
    "schema": "crl.checkpoint/v1", "model_schema": "bw1/model/v1", "configuration": "E", "parameters": 597_365,
    "reward_id": "combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1",
    "meta": {"arm": "e47p5_mix20", "label": "e062500", "episodes": 62_500, "new_fights": 15_000,
             "updates": 23_545, "configuration": "E", "boundary": "drained",
             "reward_id": "combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1",
             "source_file_sha256": "39abf3e6ccc85acda52954c1819e284c5098eb30734652dde5e624796805115d"},
    "state": {"schema": "bmix/learner_state/v1", "arm": "e47p5_mix20", "boundary": "drained", "in_flight": 0,
              "next_schedule_row": 15_000, "completed": 15_000, "updates": 23_545},
    "cards": 214, "feature_schema": "crl.features/v3-potion-v2",
    "contract": "all-boss-free",
    "archive_relative": ("experiments/trials/battle-mixed-finetune-two-starts-20261008-v1/archives/"
                         "20261008T175604619055Z/files/bmix_run_v1/arms/e47p5_mix20/output/checkpoints/e062500.pt"),
}

# Choice20k: the stored ONLINE weights of the n4-tiered arm at its round-005 boundary (20,000 resolved).
CHOICE20K = {
    "file_sha256": "9b0d3e820361722312b6b48fc2112eeaec66d8caa9252e6c2ca11f6491291f82",
    "schema": "rs1/strategy_checkpoint/v2", "decision_flow": "rs1/decision_flow/v2-auto-reward",
    "encoding_schema": "rs1/strategy_features/v3", "model_schema": "rs1/strategy_qnet/v2", "parameters": 344_609,
    "meta": {"round": 5, "label": "scheduled", "train_resolved": 20_000, "updates": 36_670,
             "learning_profile": "n4-tiered-n4n5-cpu60k"},
    "n_step": 4,
    "weights_only": True,
    "archive_relative": ("experiments/trials/run-strategy-quality-n4-n5-20k-v1/cpu_utilization_amendment_v1/"
                         "continuation_attempt1/archives/20261007T154859531375Z/full_workspace/runs/"
                         "rs1-quality45-n4-tiered-s2026101301/val/round_005/model.pt"),
}
SOURCE_OFFSET = {"choice20k": CHOICE20K["meta"]["train_resolved"]}

# Prior seed streams the new matched stream must not reuse: (base, namespace, count).
PRIOR_SEED_STREAMS = (
    (2026101201, "train", 5_000), (2026101201, "validation", 128),
    (2026101301, "train", 60_000), (2026101301, "validation", 256),
    (2026101301, "diagnostic", 12), (2026101301, "reserved_test", 512),
)

# ---------------------------------------------------------------------------------------------- schemas
CHECKPOINT_META_SCHEMA = "rs62/boundary_checkpoint_meta/v1"
CONTROLLER_CLAIM_SCHEMA = "rs62/controller_claim/v1"
CONTROLLER_EXIT_SCHEMA = "rs62/controller_exit/v1"
OWNER_EXIT_SCHEMA = "rs62/owner_exit/v1"
GATE_SCHEMA = "rs62/root_gate/v1"
RUN_CONFIG_SCHEMA = "rs62/run_config/v1"
SUMMARY_SCHEMA = "rs62/train_summary/v1"
ARMS_SUMMARY_SCHEMA = "rs62/arms_summary/v1"
REVIEW_PROFILE, REVIEW_MODEL = "PROFILE2", "claude-opus-5-5"

STOP_NAME = "STOP_NEW_ADMISSIONS.json"  # the controller's shared stop (rs1/session-stop-admissions/v1 record)
EXIT_FOR_STATUS = {"complete": 0, "failed": 1, "partial": 2, "identities_unresolved": 3}


def profile_name(arm: str) -> str:
    return f"rs62-{arm}-60k"


def pilot_id(arm: str) -> str:
    return f"rs62-{arm}-s{MASTER_SEED}"


def boundaries(arm: str) -> list[int]:
    """NEW resolved-attempt boundaries 5,000 .. 60,000 (drained weight/state checkpoints)."""
    if arm not in ARMS:
        raise ValueError(f"unknown arm {arm!r}")
    return [CHECKPOINT_EVERY * k for k in range(1, CHECKPOINTS_PER_ARM + 1)]


def cumulative(arm: str, new: int) -> int:
    return ARMS[arm]["start"] + int(new)


def label(arm: str, new: int) -> str:
    """Checkpoint label by CUMULATIVE attempts (the n4 arm includes its 20k source offset)."""
    return f"a{cumulative(arm, new):06d}"


def labels(arm: str) -> list[str]:
    return [label(arm, b) for b in boundaries(arm)]


def final_label(arm: str) -> int:
    return cumulative(arm, NEW_ATTEMPTS)


def sha256_file(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _check() -> None:
    for arm, a in ARMS.items():
        if sum(a["batch_counts"]) != 128 or len(a["batch_counts"]) != 5:
            raise ValueError(f"{arm}: batch counts {a['batch_counts']} are not five tiers of 128")
    if [q // 2 for q in STANDARD_COUNTS[1:]] != list(HALF_COUNTS[1:]) or \
            HALF_COUNTS[0] != STANDARD_COUNTS[0] + sum(STANDARD_COUNTS[1:]) // 2:
        raise ValueError("half-tiered quotas are not every quality quota halved with the rest in tier 0")
    if tuple(ARMS) != ARM_ORDER:
        raise ValueError("arm order")


_check()
