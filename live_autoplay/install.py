#!/usr/bin/env python3
"""Install the native mod and bind it to this clone's Python agent service."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

PACKAGE = Path(__file__).resolve().parent
LOCAL = PACKAGE / '.local'
sys.dont_write_bytecode = True
sys.path.insert(0, str(PACKAGE / 'python'))


def prepare_install(game_dir: Path, python: Path, dotnet: Path, port: int | None = None) -> dict:
    game = game_dir.expanduser().resolve()
    if game.name == 'SlayTheSpire2.app':
        game = game.parent
    layouts = [
        ('macOS arm64', game / 'SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64',
         game / 'SlayTheSpire2.app/Contents/MacOS/mods/STS2_MCP'),
        ('Windows x86_64 (unverified)', game / 'data_sts2_windows_x86_64', game / 'mods/STS2_MCP'),
        ('Linux x86_64 (unverified)', game / 'data_sts2_linuxbsd_x86_64', game / 'mods/STS2_MCP'),
    ]
    matches = [(label, data, mod) for label, data, mod in layouts
               if all((data / name).is_file() for name in ('sts2.dll', 'GodotSharp.dll', '0Harmony.dll'))]
    if len(matches) != 1:
        raise ValueError('Cannot identify the game installation. Pass --game-dir with its Steam installation folder.')
    platform, _, mod_dir = matches[0]
    if (mod_dir.parent / 'STS2_MCP.dll').exists():
        raise ValueError('A loose STS2_MCP.dll already exists in mods/. Remove that duplicate installation first.')
    # Preserve a venv's executable path; resolving its symlink would select the base Python.
    python = python.expanduser().absolute()
    dotnet = dotnet.expanduser().absolute()
    if not python.is_file() or not dotnet.is_file():
        raise ValueError('Python and dotnet must be existing executable files.')
    config_path = mod_dir / 'STS2_MCP.conf'
    config = json.loads(config_path.read_text()) if config_path.exists() else {}
    if not isinstance(config, dict):
        raise ValueError('Existing STS2_MCP.conf must contain a JSON object.')
    port = config.get('port', 15526) if port is None else port
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError('port must be an integer from 1 to 65535')
    config.update(port=port, agent_python=str(python), agent_service=str(PACKAGE / 'run.py'),
                  agent_auto_start=True)
    sources = {'STS2_MCP.dll': PACKAGE / 'bin/STS2_MCP.dll',
               'mod_manifest.json': PACKAGE / 'mod/mod_manifest.json',
               'agent_contract.json': PACKAGE / 'agent_contract.json'}
    for path in sources.values():
        if not path.is_file():
            raise ValueError(f'Missing packaged mod file: {path}')
    return {'game_dir': game, 'platform': platform, 'mod_dir': mod_dir,
            'python': python, 'dotnet': dotnet, 'config': config, 'sources': sources}


def assert_game_closed(game_dir: Path) -> None:
    import psutil
    for process in psutil.process_iter(['name', 'exe']):
        try:
            name = (process.info['name'] or '').lower().replace(' ', '')
            executable = process.info['exe']
            if name in ('slaythespire2', 'slaythespire2.exe') or (
                    executable and Path(executable).resolve().is_relative_to(game_dir)):
                raise RuntimeError('Close the game before installing, then run this command again.')
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue


def write_atomic(path: Path, content: bytes) -> None:
    temporary = path.with_name('.' + path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_bytes(content)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def apply_install(plan: dict) -> dict:
    assert_game_closed(plan['game_dir'])
    mod = plan['mod_dir']
    contents = {name: path.read_bytes() for name, path in plan['sources'].items()}
    contents['STS2_MCP.conf'] = (json.dumps(plan['config'], indent=2) + '\n').encode()
    before = {name: (mod / name).read_bytes() if (mod / name).exists() else None for name in contents}
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    backup = LOCAL / 'backups' / stamp
    backup.mkdir(parents=True, exist_ok=False)
    for name, content in before.items():
        if content is not None:
            (backup / name).write_bytes(content)
    (backup / 'destination.txt').write_text(str(mod) + '\n')
    settings = LOCAL / 'settings.json'
    previous_settings = settings.read_bytes() if settings.exists() else None
    mod.mkdir(parents=True, exist_ok=True)
    written = []
    try:
        for name, content in contents.items():
            write_atomic(mod / name, content)
            written.append(name)
        write_atomic(settings, (json.dumps({'dotnet': str(plan['dotnet'])}, indent=2) + '\n').encode())
    except Exception:
        for name in reversed(written):
            if before[name] is None:
                (mod / name).unlink(missing_ok=True)
            else:
                write_atomic(mod / name, before[name])
        if previous_settings is None:
            settings.unlink(missing_ok=True)
        else:
            write_atomic(settings, previous_settings)
        raise
    return {'installed': str(mod), 'backup': str(backup), 'platform': plan['platform'],
            'files': list(contents), 'python': str(plan['python']), 'dotnet': str(plan['dotnet'])}


def main(argv=None) -> int:
    from sts2_autoplay.bootstrap import discover_dotnet
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-dir', type=Path, required=True, help='Steam game installation folder')
    parser.add_argument('--python', type=Path, default=Path(sys.executable), help='Python with the repository requirements installed')
    parser.add_argument('--dotnet', help='Path to dotnet with .NET 9 installed')
    parser.add_argument('--port', type=int, help='Local bridge port (default: existing config or 15526)')
    parser.add_argument('--dry-run', action='store_true', help='Print the installation plan without writing files or contacting the game')
    args = parser.parse_args(argv)
    try:
        dotnet = discover_dotnet(args.dotnet)
        plan = prepare_install(args.game_dir, args.python, dotnet, args.port)
        if args.dry_run:
            print(json.dumps({'dry_run': True, 'platform': plan['platform'], 'destination': str(plan['mod_dir']),
                              'files': [*plan['sources'], 'STS2_MCP.conf'],
                              'config': plan['config'], 'dotnet': str(dotnet)}, indent=2))
            return 0
        assert_game_closed(plan['game_dir'])
        # Prove both policies load with the chosen interpreter before modifying the game.
        check = subprocess.run([str(plan['python']), '-B', str(PACKAGE / 'run.py'), '--check',
                                '--dotnet', str(dotnet)], capture_output=True, text=True, timeout=120)
        if check.returncode:
            raise RuntimeError('Agent preflight failed; game files were not changed.\n' + check.stdout + check.stderr)
        print(json.dumps(apply_install(plan), indent=2))
        print('Installed. Start the game, enable the mod, then use its Open/Step/Auto controls.')
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f'Installation failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
