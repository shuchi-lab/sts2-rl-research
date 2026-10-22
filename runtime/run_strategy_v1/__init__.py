"""run_strategy_v1 (rs1): an online full-run strategy learner around frozen battle policy E.

Frozen E (battle_width_scan_v1 configuration E, round 14 / 35,000 fights) plays every combat and
combat-owned selection through an owned C# bridge (worker/FullRunWorker) that drives the unchanged
build08 RunEngine for a whole persistent three-act Ironclad run. One masked candidate Double DQN
learns every noncombat decision from natural-complete online trajectories, rewarded by
``run-strategy-act-win-v1``. See README.md.

Before any ``crl`` import the frozen-input bootstrap must run (:mod:`run_strategy_v1.bootstrap`).
"""

VERSION = "rs1/v1"
