# STS2 Two-Agent Benchmark · v1.0.0

**26.6% best observed validation win rate for Ironclad at Ascension 2 across all three acts: 68 victories in 256 simulator runs.**

Two learned agents play together. The **Battle agent** chooses combat actions and targets. The **Choice agent** makes decisions between combats, including route, rewards, shops and events.

This release contains one selected agent pair, its recorded benchmark evidence, and the matching simulator runtime. It is intended to make the result easy to inspect and repeat.

## Result

| Condition or outcome | Value |
| --- | --- |
| Character / difficulty | Ironclad / Ascension 2 |
| Run length | Three acts |
| Victories | **68 / 256 (26.5625%)** |
| Deaths | 184 |
| Battle turn limits | 4, counted as nonwins |
| Errors / unsupported / missing outcomes | 0 / 0 / 0 |
| Choice checkpoint | N1, 90,000 training attempts |
| Battle checkpoint | Wide/deep, 177,500 cumulative training fights |
| Evaluation | Greedy frozen policies on 256 retained validation seeds |

The pair was selected from a **30 × 42 checkpoint comparison on the same validation seeds**. This is a selected validation result. It is not an independent held-out estimate or a result measured in the native game. No new training is included in v1.0.0.

See [the result report](docs/RESULTS.md), [per-run outcomes](results/runs.jsonl), [evaluation conditions](results/evaluation_config.json), and [checkpoint identities](results/model_identities.json).

**Reproduction note:** the packaging check on macOS arm64 completed all 256 seeds with zero errors and produced 69 wins (26.95%). Fully compared run records matched the historical evaluation for 254 seeds; one win/loss outcome changed. The 26.6% headline remains the original selected result. See [verification details](docs/VERIFICATION.md); identical results across platforms are not guaranteed.

## What is included

```text
models/       The two selected checkpoint files
runtime/      Frozen Python policy, features and inference dependencies
simulator/    Matching C# emulator/worker source and pinned assemblies
results/      Recorded outcomes, validation seeds, configuration and provenance
scripts/      Artifact verification and evaluation entry points
docs/         Results, execution instructions and verification record
```

The supported release scope is Ironclad. Shared simulator components may contain other character definitions required by the existing engine. This package does not claim evaluated support for those characters.

## Use

Start with [the running instructions](docs/RUNNING.md) for dependencies, one example run and the 256-seed benchmark. Frozen evidence lives in `results/`; new evaluations write to a separate output directory.

You can inspect the recorded outcomes and verify their counts without installing the machine-learning runtime:

```sh
python scripts/verify_release.py
```

The verifier checks release hashes, the two checkpoint identities, unique seed coverage and the 68/256 result. [The release verification record](docs/VERIFICATION.md) states what was actually exercised during packaging.

## Scope and limitations

- The simulator approximates the supported game version. Matching this benchmark is not certification of every game mechanic.
- A battle is limited to 100 turns. Those limit outcomes remain in the win-rate denominator.
- Only the selected pair is included. Training datasets, replay buffers, other experiments and cloud launch infrastructure are omitted.
- The original checkpoint bytes and pinned simulator assemblies are retained. Rebuilding source can change assembly identities; follow [the simulator notes](simulator/README.md).
- Checkpoint files are PyTorch artifacts. Load only copies whose hashes match this release.

Original release contributions are available under the [MIT license](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md) for upstream attribution and component licenses. Slay the Spire 2 and its associated names belong to their respective owners. This is an independent research project.
