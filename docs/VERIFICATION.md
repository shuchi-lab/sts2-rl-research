# Release verification

Prepared and checked on 21 October 2026 (America/Chicago). These checks concern
the packaged release and the original retained validation cohort. They are not
a new held-out evaluation.

## Recorded evidence and identities

- Recomputed the historical result from all 256 packaged seed records: **68
  victories, 184 deaths, four battle-turn limits**, and no errors, unsupported
  states or missing outcomes. Limits remain in the denominator.
- Checked seed uniqueness, ordering, hexadecimal engine-seed conversion,
  progression counts, natural-only reward, and terminal battle HP statistics.
- Verified the two original checkpoint byte hashes against their evaluation
  copies. No weights were rewritten or updated.
- Matched emulator and worker source/binary files against the recorded
  simulator manifests. Source build instructions are supplied but were not
  executed; no bit-for-bit source rebuild is claimed.
- Exercised the evidence verifier on an intact copy and a deliberately changed
  copy. The intact evidence passed; the changed file was rejected.

## Running the package from a separate directory

Copied the release into a temporary directory outside the research workspace,
cleared `PYTHONPATH`, and invoked the packaged evaluation script there. The test
used an existing .NET installation and the dedicated Python environment; it did
not install a fresh environment from the internet.

Environment: macOS arm64, Python 3.13.15, PyTorch 2.12.0, NumPy 2.4.6 and psutil
7.2.2. The existing .NET 9 runtime executed the original pinned assemblies. CPU
inference used one PyTorch thread, one full-run worker and up to eight game slots.

The model/context preflight and a one-seed FullRunWorker smoke check passed. The
first seed matched the historical outcome, reward, floor/act and decision counts.

The complete 256-seed run finished in **113.6 seconds**, with **zero errors,
zero unsupported states and zero missing outcomes**. It produced:

| Outcome | Historical evaluation | macOS packaging run |
| --- | ---: | ---: |
| Victory | 68 | 69 |
| Death | 184 | 183 |
| Battle turn limit | 4 | 4 |
| Total | 256 | 256 |
| Win rate | 26.5625% | 26.953125% |

**Exact historical reproduction did not pass for every seed.** The full outcome
objects (including per-battle summaries), reward, policy label and decision
counts matched for **254 of 256 runs**. Win/loss flags matched for **255 of 256**.

| Seed | Historical result | macOS packaging result |
| --- | --- | --- |
| `74879731935361116` | Victory, 495 Battle decisions | Victory, 528 Battle decisions |
| `6658505373016834173` | Death on floor 48, 470 Battle decisions | Victory, 446 Battle decisions |

The released headline remains the original **68/256** selected validation result.
The packaging check establishes that the standalone runtime completes the
benchmark; it does not establish exact historical or cross-platform replay.
No policy was tuned to remove the differences.

A bounded follow-up ran the original study evaluator and frozen source runtime
on the same Mac for exactly these two seeds. Both original-runtime outcomes
matched the packaged run, including full battle summaries after normalizing run
IDs. Twelve core policy/model/encoder/dispatcher files were byte-identical;
eight extracted active definitions were syntax-tree-identical. These probes
found no packaging-induced inference discrepancy. The specific cause of the
historical cross-environment differences remains unproven; see the
[runtime comparison](runtime_comparison.json).

Machine-readable evidence: [comparison report](reproduction_check.json) and
[all packaging-run outcomes](reproduction_runs.jsonl). The original historical
evidence remains unchanged in `results/`. The comparison covers recorded battle
summaries, not an action-by-action trajectory replay.

## Scope of the checks

The package was exercised on the reported macOS CPU environment. Windows/Linux
execution, a fresh dependency installation, native-game play and a full source
rebuild were not tested during packaging. The recorded original evaluation and
its provenance remain available independently of this reproduction check.

`python scripts/verify_release.py` verifies the final file inventory, model
identities, result-file provenance and historical arithmetic. The archive's
separate `.sha256` file identifies the downloadable ZIP. Hash checks detect byte
changes relative to the supplied manifests; they do not independently certify
game mechanics or benchmark generalization.
