# Simulator used by v1.0.0

This directory preserves the C# simulator and worker runtime used by the selected
`choice_n1_n2_30kmax_act3_battle_crossplay_20261013_v1` study. It is the build08
training engine for game v0.111.0, plus the recorded training interface and
Juggernaut repair. It is not the later installed simulator or the separate
`sts2_env` Python implementation.

The release evaluates a restricted Ironclad, Ascension 2, three-act agent. That
agent's supported scope is narrower than the engine's content definitions. Engine
source alone does not establish support for every character, card, relic, or
interaction. The release's evaluation protocol and outcome records define the
reported result.

## Contents

| Path | Purpose |
| --- | --- |
| `source/engine/whole_game_build08/Sts2Emulator/` | Exact engine source named by the recorded worker build |
| `source/engine/whole_game_build08/SOURCE.json` | Original upstream, overlay, and file-hash record |
| `source/worker/CollectorWorker/` | Raw combat state and collector interface |
| `source/worker/RlWorkerBuild08/` | Combat reset/step interface |
| `source/worker/FullRunWorker/` | Persistent full-run bridge and autoplay guard |
| `source/worker/GuardedBattleWorker/` | Guard facade used by the inference context |
| `bin/worker_build08/` | Original pinned combat worker and dependencies |
| `bin/FullRunWorker/` | Original pinned full-run bridge and dependencies |
| `bin/GuardedBattleWorker/` | Original pinned guard facade and certified files |
| `provenance/` | Original build manifests, guard certificate, and release verification |
| `licenses/` | Preserved upstream and bundled dependency notices |

The source layout retains project references. Two `.cs.orig` files are preserved
because the original engine source manifest hashes them; they are historical
backups, not compiled inputs. The guard PDB is retained because its original
certificate includes that file. Other build intermediates and debug files are
excluded. The bundled `Sts2Emulator.dll` is this emulator's assembly, not the
commercial game's assembly. No decompiled game source, game assets, external
recordings, or commercial game assemblies are included here.

## Recorded identity and verification

The engine descends from the accepted
`output/abb69-79-parity-20261002/candidate-workspace/src/Sts2Emulator` source,
with `training_overlay.patch` and `combat_rl_sync.patch`. These patches and the
original `SOURCE.json` are included beside the source. The engine identity is:

```text
sts2emulator-whole-game-build08-v0.111.0+crl-training-overlay/v1+p2-juggernaut-await-kill
```

The release preparation checked all 73 engine-manifest files, 7 collector files,
2 RL-worker files, 14 full-run bridge files, and 5 guard files against their
original manifests. Every copied file matched. The engine source manifest itself
also matches the hash named by the recorded worker build. The full-run manifest
matches the selected study's trusted bridge manifest.

| Assembly | SHA-256 |
| --- | --- |
| `Sts2Emulator.dll` | `11d614a31ea796f5631cf163d876db56b5129c38b9a5e98e7ee7f1632f6b2b9b` |
| `CombatWorker.dll` | `ab408a807b1837c5da2e17b6785ca7ce18e49a78452271538bf33a1cbbe7cfb9` |
| `RlCombatWorker.dll` | `b49a635d51cfc68c5e0997342e96ee2a21fb2076e5f406c150194198ff018369` |
| `FullRunWorker.dll` | `7c25fc96002672e87dbfe5554ce24930c721cefe21b979f1a2cb298c84b6c0d6` |
| `GuardedBattleWorker.dll` | `556878cadf9461b30695eabecdeb82da10c8f2ad0a05ae4773638619a5dba3e4` |
| `System.IO.Hashing.dll` | `fbe8c2fcb4adc01da5c3c8ed0d727903b8fda1e6b84da3c447c9794615f30664` |

`provenance/RELEASE_PROVENANCE.json` records every copied path, source, size, and
hash. Original manifests are preserved byte for byte; their historical machine
paths identify the original builds and are not dependencies of this release.
No simulator rebuild was performed during preparation. Source correspondence is
verified against the recorded manifests, rather than established by a fresh
bit-for-bit rebuild.

## Running and rebuilding

Use the release's evaluation entry point with the prebuilt files above. These
are managed .NET 9 assemblies and require an installed .NET 9 runtime. The runtime
is not bundled. The accompanying `.deps.json` and `.runtimeconfig.json` files are
part of each worker's launch environment.

For source inspection and development, the original builds used .NET SDK
9.0.318. Building the combat worker also requires NuGet access or a cache containing
`System.IO.Hashing` 9.0.0. From the release root, the corresponding commands are:

```sh
dotnet build simulator/source/worker/RlWorkerBuild08/RlWorker.csproj \
  -c Release -m:2 -nodeReuse:false -p:UseSharedCompilation=false \
  -o simulator/rebuilt/worker_build08

dotnet build simulator/source/worker/FullRunWorker/FullRunWorker.csproj \
  -c Release -nodeReuse:false -p:UseSharedCompilation=false \
  -p:Rs1FrozenDependencies="$PWD/simulator/bin/worker_build08" \
  -o simulator/rebuilt/FullRunWorker

dotnet build simulator/source/worker/GuardedBattleWorker/GuardedBattleWorker.csproj \
  -c Release -nodeReuse:false -p:UseSharedCompilation=false \
  -p:Act1ftFrozenDependencies="$PWD/simulator/bin/worker_build08" \
  -o simulator/rebuilt/GuardedBattleWorker
```

These commands create development builds. Embedded paths and build environments
can change assembly bytes. The shipped checkpoints and guard certificate pin the
original bytes, so newly built files must not replace `bin/` when reproducing the
reported result. A changed build needs its own compatibility and evaluation
evidence. The build commands have not been rerun as part of this release.

See [third-party notices](../THIRD_PARTY_NOTICES.md) for source attribution and the
preserved license texts.
