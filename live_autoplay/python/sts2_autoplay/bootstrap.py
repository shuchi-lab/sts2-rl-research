"""Resolve the released inference assets relative to this repository."""
from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import shutil
import subprocess
import sys

DEFAULT_CHOICE_PRESET = 'published-v1'
CHOICE_PRESETS = {
    'published-v1': {
        'label': 'Choice90k N1', 'model_id': 'n1-half-blue90-original-mac_a090000',
        'resolved_attempts': 90000, 'n_step': 1, 'updates': 225057,
        'pilot_id': 'rs1-mac-blue90000-n1half-s2026101012',
        'learning_profile': 'n1-half-tiered-blue90000-mac100k', 'release': '.',
    },
}


@dataclass(frozen=True)
class Assets:
    project: Path
    runtime: Path
    choice_manifest: Path
    choice_config: Path
    choice_checkpoint: Path
    battle_checkpoint: Path
    choice_preset: str = DEFAULT_CHOICE_PRESET


def discover_dotnet(explicit: str | None = None) -> Path:
    executable = 'dotnet.exe' if os.name == 'nt' else 'dotnet'
    if explicit:
        candidates = [explicit]
    else:
        candidates = [os.environ.get('RS1_DOTNET'), shutil.which(executable)]
        candidates += [str(Path(root) / executable) for root in
                       (os.environ.get('DOTNET_ROOT'), str(Path.home() / '.dotnet'),
                        '/usr/local/share/dotnet', '/usr/share/dotnet',
                        str(Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'dotnet')) if root]
    for candidate in candidates:
        if not candidate:
            continue
        path = Path(candidate).expanduser().resolve()
        if not path.is_file():
            continue
        try:
            result = subprocess.run([str(path), '--list-runtimes'], capture_output=True,
                                    text=True, timeout=20)
        except (OSError, subprocess.SubprocessError):
            continue
        if result.returncode == 0 and any(line.startswith('Microsoft.NETCore.App 9.')
                                         for line in result.stdout.splitlines()):
            return path
    raise ValueError('A .NET 9 runtime is required. Install it, add dotnet to PATH, '
                     'or pass --dotnet /absolute/path/to/dotnet.')


def configure_runtime() -> Path:
    dotnet = discover_dotnet(os.environ.get('RS1_DOTNET'))
    os.environ.update(RS1_DOTNET=str(dotnet), STS2_RL_DOTNET_ROOT=str(dotnet.parent),
                      PYTHONDONTWRITEBYTECODE='1', OMP_NUM_THREADS='1', MKL_NUM_THREADS='1',
                      OPENBLAS_NUM_THREADS='1', OMP_WAIT_POLICY='PASSIVE', KMP_BLOCKTIME='0')
    return dotnet


def initialize(project_root: Path, choice_preset: str = DEFAULT_CHOICE_PRESET) -> Assets:
    if choice_preset not in CHOICE_PRESETS:
        raise ValueError(f'unknown Choice preset {choice_preset!r}')
    project = Path(project_root).expanduser().resolve()
    runtime = project / 'runtime'
    assets = Assets(project, runtime, runtime / 'metadata/choice_source_manifest.json',
                    runtime / 'metadata/choice_config.json', project / 'models/choice.pt',
                    project / 'models/battle.pt', choice_preset)
    required = [runtime / 'crl/features.py', runtime / 'run_strategy_v1/model.py',
                runtime / 'data/frozen_three_act_214_inputs/catalog.json',
                project / 'simulator/bin/worker_build08/RlCombatWorker.dll',
                project / 'results/summary.json', project / 'results/model_identities.json',
                assets.choice_manifest, assets.choice_config,
                assets.choice_checkpoint, assets.battle_checkpoint]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise ValueError('missing frozen asset(s): ' + ', '.join(missing))
    for name in ('crl', 'run_strategy_v1', 'act1_battle_finetune_v1', 'battle_enemy_capacity_v1'):
        module = sys.modules.get(name)
        if module is not None and runtime not in Path(module.__file__).resolve().parents:
            raise ValueError(f'{name} already imported from a different runtime; use a fresh process')
    for key, expected in [('STS2_RL_ENGINE_LINE', 'build08'),
                          ('STS2_RL_INPUT_BUNDLE', str(runtime / 'data/frozen_three_act_214_inputs'))]:
        actual = os.environ.get(key)
        if actual is not None and actual != expected:
            raise ValueError(f'{key} differs from the pinned inference runtime')
        os.environ[key] = expected
    if str(runtime) not in sys.path:
        sys.path.insert(0, str(runtime))
    sys.dont_write_bytecode = True
    return assets
