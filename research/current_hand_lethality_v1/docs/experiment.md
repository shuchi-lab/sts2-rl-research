# Matched experiment: HP masking with and without lethality features

This is a derived summary of a completed local experiment and its retained
analysis, using **one matched training seed**. All eight regular checkpoints
per arm are shown. The accompanying [checkpoint JSON](../results/checkpoints.json)
contains exact model hashes, counts, outcome categories, reward/HP summaries,
potion usage, and per-act/room battle results. [SOURCE.json](../SOURCE.json)
identifies the source files by hash. Raw trajectories and model files are not
included, so this extract alone cannot reproduce the evaluations.
Provenance paths identify locations relative to the original STS2_ML archive;
they are not file links into this public package.

These are combat experiments, distinct from the repository's released
three-act, two-agent **68/256** benchmark. Their percentages have different
denominators and must not replace that result.

## Comparison and training

| Property | Both arms |
| --- | --- |
| Architecture | C64-D128-L2; 1,658,346 parameters |
| Training seed | 2026102201; matched fresh initial weights |
| Enemy inputs | `max_hp` and `hp_fraction` masked |
| Dense widths | Enemy 32, legal-action 10 |
| Schedule | 2,000 warmup fights, 2,000 initial mixed updates, 20,000 stage-one fights |
| Stage two | None |
| Other settings | Optimized log runtime; approved low-HP schedules; batch 128; 32 slots; 100-turn cap |
| Reward | Existing all-boss-free potion profile; terminal turn-limit reward −10 |

`masked_baseline` fills the two added enemy and four added action columns with
zeros. `masked_lethality` receives the bounded native search flags. The main
contrast therefore concerns the added signals under the common HP mask; this
pair cannot establish a benefit from masking HP itself. No policy override or
new reward was introduced.

The schedule fixes fights, not exact accepted optimizer updates. The retained
update counters below include the initial mixed updates and differ between
arms. All regular stage-one checkpoints from 2.5k to 20k are included; warmup,
pretraining, an off-grid phase-boundary save, and duplicate final saves are
excluded from the comparison curve.

The original training process also ran a fixed 450-case validation panel at
each checkpoint (150 pooled bosses), with checkpoint selection specified by
pooled boss wins. The 5,000-case results below are a separate retained analysis
of every regular checkpoint. An observed peak on that panel is descriptive,
not a new held-out estimate after selection.

## Evaluation panels

- **Broad behavior:** 1,632 whole-turn cases: 192 legacy, 640 regression, and
  800 primary. The primary score below includes tied optima.
- **Ordering signals:** 2,752 cases, including 480 core ordering-required lethal
  cases and 480 core high-HP damage-ordering cases. Those two core groups are
  shown separately below.
- **Safety and targets:** 512 native cases crossing four hands, enemy contexts,
  HP values, player HP, hand order, and one/two enemies. Of these, 118 permit a
  surviving victory and 306 permit survival. These subset denominators answer
  different questions; they are not all 512 cases.
- **Full fights:** reuse 5,000 frozen case/seed attempts per checkpoint, including
  1,800 bosses. Every checkpoint uses the same jobs; snapshots are correlated.

The behavior analysis completed 78,336 planned whole-turn attempts across
16 checkpoints with zero errors. It reused 80,000 previously recorded full
battles. Behavior inference used float32 CPU, epsilon 0, and MHA fastpath off.
The original Windows battle evaluator did not explicitly retain its MHA
fastpath setting, so cross-panel numerical equivalence is not claimed.

## All checkpoint pairs: behavior

B = masked baseline; F = masked + lethality features. Entries are success
counts, with denominators in column headings. High-HP correctness measures
damage ordering in states without positive kill hints.

| Stage-one fights | Lethal B /480 | Lethal F /480 | Primary B /800 | Primary F /800 | High-HP B /480 | High-HP F /480 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2500 | 64 | 442 | 463 | 536 | 248 | 236 |
| 5000 | 204 | 350 | 457 | 520 | 318 | 212 |
| 7500 | 212 | 368 | 455 | 456 | 388 | 226 |
| 10000 | 174 | 370 | 500 | 602 | 324 | 298 |
| 12500 | 146 | 304 | 511 | 648 | 350 | 320 |
| 15000 | 138 | 328 | 487 | 586 | 326 | 278 |
| 17500 | 150 | 376 | 513 | 569 | 328 | 294 |
| 20000 | 136 | 424 | 464 | 620 | 336 | 376 |

## All checkpoint pairs: full fights and updates

Wins use all attempts as the denominator; losses, turn limits, and truncations
remain included. These are individual combat wins, not whole-game wins.

| Stage-one fights | All B /5000 | All F /5000 | Boss B /1800 | Boss F /1800 | Updates B | Updates F |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2500 | 3369 | 3465 | 529 | 590 | 4479 | 4477 |
| 5000 | 3602 | 3495 | 701 | 622 | 7311 | 7310 |
| 7500 | 3545 | 3820 | 674 | 869 | 10519 | 10567 |
| 10000 | 3746 | 3527 | 783 | 627 | 13890 | 13948 |
| 12500 | 3710 | 3777 | 774 | 851 | 17471 | 17536 |
| 15000 | 3633 | 3515 | 705 | 683 | 21210 | 21318 |
| 17500 | 3761 | 3931 | 821 | 956 | 25058 | 25213 |
| 20000 | 3798 | 3847 | 861 | 921 | 28935 | 29085 |

## Findings and limits

At the final 20k checkpoint, ordering-required kill execution increases from
**136/480 (28.3%) to 424/480 (88.3%)**. The feature learner leads on this measure
at all eight points. Primary whole-turn correctness rises from **464/800 (58.0%)
to 620/800 (77.5%)**, but individual behavior families differ.

In the safety panel, final available-win conversion is **84/118 versus 112/118**.
The feature learner has no avoidable deaths among the 306 survivable cases at
any checkpoint. This does not mean no deaths: the panel deliberately includes
states where survival is impossible. The JSON retains both avoidable and total
death counts.

General ordering remains uneven. The feature learner trails baseline on the
core high-HP damage cases at seven of eight points, even though its final score
is higher (376/480 versus 336/480). A kill-specific signal does not supply a
general turn-value or damage-ordering solution.

Boss wins favor the feature learner at five of eight checkpoints. At 20k the
counts are **861/1,800 (47.83%) versus 921/1,800 (51.17%)**. The best observed
feature boss count on this panel is 956/1,800 (53.11%) at 17.5k, and its 5k,
10k, and 15k results are below baseline. Full curves matter more than a selected
peak. At the final point, baseline has 12 turn-limit outcomes among all 5,000
fights; the feature arm has 3 turn limits and 1 step truncation. All remain
nonwins.

The JSON's `mean_reward` uses the original fight reward. `mean_hp_ratio` is
terminal HP divided by starting HP, averaged where defined across outcomes;
healing can make it exceed one. `potion_uses` is the total number of uses, and
`zero_action_starts` counts fights with no policy decision. Full outcome counts
and act/room cells are retained rather than hiding truncations behind win rates.

These observations are conditional on one pair of trained runs. Thousands of
evaluation cases do not substitute for independent training seeds. The behavior
suites are diagnostic and exposed, not a new held-out generalization claim.
The native search runs during training and evaluation, so improvements concern
the model plus those computed inputs. Neither general superiority, a causal
benefit from HP masking, nor a better released full-game benchmark is established
by this snapshot.
