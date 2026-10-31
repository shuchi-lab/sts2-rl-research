"""Portable entry points and installation behavior; no real game writes."""
import json
import os
from pathlib import Path
import subprocess
import sys

import pytest

PACKAGE = Path(__file__).resolve().parents[1]
REPOSITORY = PACKAGE.parent


def fake_game(tmp_path):
    game = tmp_path / 'Steam Library' / 'Slay the Spire 2'
    data = game / 'SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64'
    data.mkdir(parents=True)
    for name in ('sts2.dll', 'GodotSharp.dll', '0Harmony.dll'):
        (data / name).write_bytes(b'fixture game assembly')
    return game


def test_launcher_help_works_outside_repository(tmp_path):
    before = sorted(str(p) for p in (PACKAGE / '.local').rglob('*'))
    result = subprocess.run([sys.executable, '-B', str(PACKAGE / 'run.py'), '--help'],
                            cwd=tmp_path, capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    assert '--check' in result.stdout and '--dotnet' in result.stdout
    assert sorted(str(p) for p in (PACKAGE / '.local').rglob('*')) == before


def test_bootstrap_uses_only_repository_assets(monkeypatch):
    from sts2_autoplay import bootstrap
    monkeypatch.delenv('STS2_RL_INPUT_BUNDLE', raising=False)
    assets = bootstrap.initialize(REPOSITORY)
    assert assets.choice_checkpoint == REPOSITORY / 'models/choice.pt'
    assert assets.battle_checkpoint == REPOSITORY / 'models/battle.pt'
    assert assets.choice_manifest == REPOSITORY / 'runtime/metadata/choice_source_manifest.json'
    assert assets.choice_config == REPOSITORY / 'runtime/metadata/choice_config.json'
    assert assets.choice_preset == 'published-v1'
    assert assets.runtime == REPOSITORY / 'runtime'


def test_wrong_runtime_and_missing_assets_are_rejected(tmp_path, monkeypatch):
    from sts2_autoplay import bootstrap
    with pytest.raises(ValueError, match='missing frozen asset'):
        bootstrap.initialize(tmp_path)
    monkeypatch.setenv('STS2_RL_INPUT_BUNDLE', str(tmp_path))
    with pytest.raises(ValueError, match='STS2_RL_INPUT_BUNDLE'):
        bootstrap.initialize(REPOSITORY)


def test_install_preview_writes_nothing(tmp_path, monkeypatch):
    import install
    game = fake_game(tmp_path)
    local = tmp_path / 'package-local'
    monkeypatch.setattr(install, 'LOCAL', local)
    plan = install.prepare_install(game, Path(sys.executable), Path(sys.executable), port=15527)
    assert plan['mod_dir'] == game / 'SlayTheSpire2.app/Contents/MacOS/mods/STS2_MCP'
    assert plan['config']['port'] == 15527
    assert plan['config']['agent_service'] == str(PACKAGE / 'run.py')
    assert not plan['mod_dir'].exists() and not local.exists()


def test_install_preserves_unrelated_files_and_backs_up_replaced_files(tmp_path, monkeypatch):
    import install
    game = fake_game(tmp_path)
    local = tmp_path / 'package-local'
    monkeypatch.setattr(install, 'LOCAL', local)
    monkeypatch.setattr(install, 'assert_game_closed', lambda *args: None)
    plan = install.prepare_install(game, Path(sys.executable), Path(sys.executable))
    mod = plan['mod_dir']
    mod.mkdir(parents=True)
    (mod / 'STS2_MCP.dll').write_bytes(b'old mod')
    (mod / 'STS2_MCP.conf').write_text('{"port":15528,"custom_setting":"keep"}')
    (mod / 'history.json').write_text('keep history')
    plan = install.prepare_install(game, Path(sys.executable), Path(sys.executable))
    receipt = install.apply_install(plan)
    config = json.loads((mod / 'STS2_MCP.conf').read_text())
    assert config['port'] == 15528 and config['custom_setting'] == 'keep'
    assert config['agent_auto_start'] is True
    assert (mod / 'history.json').read_text() == 'keep history'
    assert (Path(receipt['backup']) / 'STS2_MCP.dll').read_bytes() == b'old mod'
    assert (mod / 'STS2_MCP.dll').stat().st_size > 100_000
    assert json.loads((local / 'settings.json').read_text())['dotnet'] == str(Path(sys.executable).absolute())


def test_install_refuses_invalid_game_and_running_game(tmp_path, monkeypatch):
    import install
    with pytest.raises(ValueError, match='game'):
        install.prepare_install(tmp_path, Path(sys.executable), Path(sys.executable))
    plan = install.prepare_install(fake_game(tmp_path), Path(sys.executable), Path(sys.executable))
    def running(*args):
        raise RuntimeError('Close the game before installing')
    monkeypatch.setattr(install, 'assert_game_closed', running)
    with pytest.raises(RuntimeError, match='Close the game'):
        install.apply_install(plan)
    assert not plan['mod_dir'].exists()


def test_install_keeps_virtualenv_python_symlink(tmp_path):
    import install
    python = tmp_path / 'venv/bin/python'
    python.parent.mkdir(parents=True)
    python.symlink_to(sys.executable)
    plan = install.prepare_install(fake_game(tmp_path), python, Path(sys.executable))
    assert plan['config']['agent_python'] == str(python.absolute())


def test_failed_install_restores_previous_mod(tmp_path, monkeypatch):
    import install
    monkeypatch.setattr(install, 'LOCAL', tmp_path / 'local')
    monkeypatch.setattr(install, 'assert_game_closed', lambda *args: None)
    plan = install.prepare_install(fake_game(tmp_path), Path(sys.executable), Path(sys.executable))
    mod = plan['mod_dir']
    mod.mkdir(parents=True)
    (mod / 'STS2_MCP.dll').write_bytes(b'working old mod')
    write = install.write_atomic
    failed = False
    def fail_once(path, content):
        nonlocal failed
        if path.name == 'agent_contract.json' and not failed:
            failed = True
            raise OSError('fixture disk failure')
        write(path, content)
    monkeypatch.setattr(install, 'write_atomic', fail_once)
    with pytest.raises(OSError, match='fixture disk failure'):
        install.apply_install(plan)
    assert (mod / 'STS2_MCP.dll').read_bytes() == b'working old mod'
    assert not (mod / 'mod_manifest.json').exists()
    assert not (mod / 'agent_contract.json').exists()
    assert not (install.LOCAL / 'settings.json').exists()


def test_contract_matches_published_models_and_has_no_private_paths():
    contract = json.loads((PACKAGE / 'agent_contract.json').read_text())
    summary = json.loads((REPOSITORY / 'results/summary.json').read_text())
    assert contract['models']['strategy']['model_id'] == summary['choice_model_id']
    assert contract['models']['battle']['model_id'] == summary['battle_model_id']
    assert contract['models']['strategy']['checkpoint'] == 'models/choice.pt'
    assert contract['models']['battle']['checkpoint'] == 'models/battle.pt'
    assert '/Volumes/' not in json.dumps(contract) and '/Users/' not in json.dumps(contract)
