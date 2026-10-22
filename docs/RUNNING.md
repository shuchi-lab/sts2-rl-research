# Run the frozen two-agent benchmark

This release runs the selected Ironclad Ascension 2, three-act policy pair on the retained validation seed list. Choice selects the public noncombat decisions; Battle selects combat actions. Both policies run greedily on CPU, with gradients disabled and optimizer construction prohibited. Nothing trains or updates checkpoints.

## Requirements

- Python 3.11 or newer; locally verified with Python 3.13.15.
- The Python packages in `requirements.txt`: NumPy 2.4.6, PyTorch 2.12.0, and psutil 7.2.2.
- An existing .NET 9 runtime. The release includes the exact managed simulator assemblies and their sources, but does not bundle .NET.

From the extracted release directory, create your own environment and install the pinned packages:

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
```

On Windows, use `.venv\Scripts\activate` for the activation step. A CPU-only PyTorch wheel may be selected for the local operating system; the evaluator always uses CPU.

## Commands

Check the checkpoints, vocabulary, feature layout, reward context, and certified isolated Battle worker without playing any runs. This check does not launch the FullRunWorker; the one-game example below verifies that part:

```sh
python scripts/evaluate.py --check --output-dir runs/check
```

Run one complete game using the first retained validation seed:

```sh
python scripts/evaluate.py --episodes 1 --output-dir runs/example
```

Run all 256 retained validation seeds:

```sh
python scripts/evaluate.py --all --output-dir runs/validation-256
```

`--episodes 256` is equivalent to `--all`; any count from 1 through 256 selects that prefix of the seed list. `--timeout-seconds 7200` is the default gameplay/admission deadline. Startup contract checks run before the gameplay event loop and are not bounded by this option. The evaluator uses one native worker, up to eight concurrent runs, and one PyTorch thread, matching the recorded evaluation settings.

The executable is discovered from `--dotnet`, `RS1_DOTNET`, `PATH`, `DOTNET_ROOT`, or standard installation locations. If automatic discovery fails, supply it explicitly:

```sh
python scripts/evaluate.py --episodes 1 --output-dir runs/example-explicit --dotnet /absolute/path/to/dotnet
```

The .NET installation must match the local operating system and processor. The bundled managed assemblies are used directly; evaluation does not compile simulator sources or require a .NET SDK. The command can be invoked from any working directory by using an absolute path to `scripts/evaluate.py`.

## Outputs and interpretation

Every invocation requires a new output directory and refuses writes inside the frozen `results/`, `models/`, `runtime/`, `simulator/`, `scripts/`, or `docs/` directories. Without `--output-dir`, the evaluator creates a timestamped directory under `runs/`.

- `run_manifest.json` records dependency versions, runtime location, model hashes, seed identities, limits, and execution settings.
- `summary.json` records the fixed attempted denominator, wins, unsupported outcomes, limits, operational errors, missing runs, act/boss progress, reward, policy audits, and timing. This exists for gameplay runs; `check.json` is produced by `--check`.
- `cells/<cell-id>/records.jsonl` contains individual run outcomes and battle histories, including terminal HP, decisions, reward, and the original integer seed.
- `cells/<cell-id>/admissions.jsonl`, `RUN_STATE.json`, and `RESOURCE_LOG.jsonl` retain execution evidence. `loaded_modules.json` records the local Python dependency closure.
- An exception produces `failure.json` and a nonzero process exit. Unsupported states and turn/step limits remain visible non-wins; they are never silently retried. A wall-time cutoff is reported as incomplete and also returns nonzero.

The original result is **68 wins out of 256 attempts (26.5625%)**. The macOS arm64 packaging run produced **69/256**, with two differing run records and one differing win/loss outcome; see [verification details](VERIFICATION.md). Replaying the retained validation seeds checks the released artifacts and runtime but does not provide an independent test estimate or guarantee exact historical outcomes across platforms. A one-game example is a functional check, not a win-rate estimate.

## Frozen contracts and minimal runtime

Checkpoint files are copied unchanged and their full SHA-256 values are checked before loading. The source checkpoint containers still contain original optimizer/target metadata; evaluation loads only the online model state into the policies. No replay snapshots or training datasets are included.

The packaged Python runtime contains the frozen model definitions, encoders, masks, greedy policies, full-run dispatcher, strict compatibility checks, and their required helpers. Unrelated training entry points, experiment orchestration, deployment, and historical report trees have been removed. A few shared replay types and training configuration fields remain because the unchanged reward/context modules import them or checkpoint compatibility checks require them. They are inactive during evaluation. Reward and guard files that hash their own implementation are preserved byte for byte.

Operational paths are resolved within the extracted release. Historical path strings inside checkpoint metadata and `runtime/metadata/` are retained as provenance values for strict identity comparison; those source locations are never opened at evaluation time. The bundled restriction manifest and original simulator build fingerprints remain enforced.
