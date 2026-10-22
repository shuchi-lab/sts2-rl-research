# v1.0.0 result evidence

The released pair achieved **68 victories in 256 full simulator runs (26.5625%, reported as 26.6%)** with **Ironclad at Ascension 2**, across all three acts. This is the **best observed validation result in a completed checkpoint screen**, selected from 30 Choice checkpoints paired with 42 Battle checkpoints. It is not an independent held-out test estimate or a measured native-game win rate.

The result was recorded on 13 October 2026. The historical evidence above is unchanged; release packaging reruns are reported separately in [VERIFICATION.md](VERIFICATION.md).

## Exact model pair

| Agent | Released checkpoint | Training boundary |
|---|---|---|
| Choice | `models/choice.pt` | N=1, original reward, 90,000 resolved training attempts; trained with the frozen Blue E90k Battle agent |
| Battle | `models/battle.pt` | `enemy_wide_deep`, 929,653 parameters; 177,500 cumulative online fights, including 32,500 Act 3 fine-tuning fights |

Both are the original serialized checkpoints. Their identities, byte lengths and SHA-256 hashes are in [model_identities.json](../results/model_identities.json). Release preparation checked that each original checkpoint matches its staged evaluation copy byte for byte. The Battle file hash was computed for this release; the historical evaluation recorded its canonical identity and size without a published file hash.

## Outcome accounting

Every planned validation seed has exactly one admission and one retained outcome. Limit endings remain in the win-rate denominator.

| Outcome | Runs |
|---|---:|
| Full-run victory | 68 |
| Natural death in combat | 184 |
| Combat turn limit | 4 |
| Unsupported state | 0 |
| Runtime error | 0 |
| Missing or unresolved outcome | 0 |
| **Total** | **256** |

The four limit endings reached the configured 100-turn combat cap. The 68 run victories agree with 68 Act 3 boss victories in the underlying battle records. The pair reached Act 2 in 195 runs, Act 3 in 133 runs and the Act 3 boss in 118 runs.

The mean recorded Choice return was **6.354 over 252 natural endings**. Return is undefined for the four limit endings and is retained as `null`; it is not imputed. The reward pays combat victories by act, as specified in [evaluation_config.json](../results/evaluation_config.json). It is a separate descriptive measure from the win rate.

Remaining HP is taken from the last closed battle, because the raw run-level HP is zero after victorious run completion. Across the 68 wins, mean terminal battle HP was **24.01** and median HP was **22.5**; the mean HP/max-HP fraction was **26.95%**. Across all 252 natural endings, including deaths at zero HP, mean terminal battle HP was **6.48**. The four truncated final battles have no terminal HP and remain `null`. Both the raw run-level HP and the useful final-battle HP are preserved in the packaged rows.

## Evaluation scope and selection

The environment was the frozen build08 RunEngine simulator through FullRunWorker, with all content unlocked under the configured progression setting. Both agents acted greedily in evaluation mode with gradients disabled. [evaluation_config.json](../results/evaluation_config.json) records the limits, decision-flow and feature schema, reward definition, cohort and frozen bridge/build identities.

The same 256 validation seeds were reused across all 1,260 pairings. The completed screen contains 322,560 retained table outcomes, including 60 reused pairings and 1,200 newly evaluated pairings. The released pair was the unique observed maximum in that screen. Those 322,560 outcomes are checkpoint comparisons on a common cohort, not independent runs supporting the selected pair's 26.6% estimate.

Selection from many pairings and repeated use of the validation cohort can inflate the observed best score. Reserved final-test seeds were not used in this result. This release supplies no independent final-test estimate, no cross-host action-parity result and no native reference-game replays. A fresh, frozen evaluation cohort is needed for an independent performance claim.

## Packaged evidence

- [summary.json](../results/summary.json): recomputed outcome counts, progression, reward and HP summaries, and selection context.
- [runs.jsonl](../results/runs.jsonl): one sanitized record for every planned seed, in seed-manifest order. `seed_index` is zero-based. `won` is true only for a natural full-run victory. A limit row is a non-win with a null return and null last-battle terminal HP.
- [validation_seeds.json](../results/validation_seeds.json): the exact 256 validation seeds in their original order. Decimal strings prevent loss of integer precision in JSON readers. The engine receives `RS1V` followed by the seed formatted as 16 uppercase hexadecimal digits.
- [evaluation_config.json](../results/evaluation_config.json): the evaluation contract and scoring conventions.
- [model_identities.json](../results/model_identities.json): both checkpoint identities and exact byte hashes.
- [SOURCE_PROVENANCE.json](../results/SOURCE_PROVENANCE.json): repository-relative references and SHA-256 hashes for original evidence, plus hashes of the packaged result files. The original evidence was left untouched. Provenance references locate the research inputs; those source files are not required to verify the packaged arithmetic.

The compact per-run projection omits full battle histories, action logs, absolute paths, machine details and process-control data. Its source-file hashes preserve the connection to the original evidence. Aggregate counts were independently recomputed from the original outcomes, joined against the admission ledger and exact seed manifest, and checked against the completed study table before packaging.

## Verification without gameplay

From the release directory, verify the packaged evidence with the Python standard library:

```sh
python scripts/verify_release.py --evidence-only
```

The checks are:

1. Verify every `packaged_files` SHA-256 and byte length in `SOURCE_PROVENANCE.json`.
2. With the default full verifier (`python scripts/verify_release.py`), also verify the release checksum manifest and each model's SHA-256 and byte length. The `--evidence-only` mode skips model and runtime file verification.
3. Read the seed manifest and JSONL rows. Require 256 unique seeds, exactly one row per seed, matching order and zero-based `seed_index`, and the stated hexadecimal `engine_seed` conversion.
4. Count 68 natural victories, 184 natural combat deaths and four battle-turn-limit endings. Require zero missing, unsupported or error rows, and compute `68 / 256 == 0.265625`.
5. Recompute the progression counts, natural-only return mean, and HP summaries from the packaged fields, comparing them to `summary.json`. Require `won == boss3_won` for every row. Limit rows must remain in the fixed denominator.

This verifies the released bytes, seed coverage and result arithmetic. It does not independently replay the simulator, validate every original action, or establish generalization beyond the selected validation cohort.
