"""A service opened by the game ends when its parent game process ends."""
from contextlib import contextmanager
from pathlib import Path
import subprocess
import sys
import threading

import psutil
import pytest


@contextmanager
def sleeping_process():
    process = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
    try:
        yield process
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=5)


def test_game_parent_detection_does_not_claim_terminal_process(monkeypatch):
    from sts2_autoplay import lifetime
    class Parent:
        def __init__(self, name):self.process_name = name
        def name(self):return self.process_name
    for name in ('Slay the Spire 2', 'SlayTheSpire2', 'SlayTheSpire2.exe'):
        parent = Parent(name)
        monkeypatch.setattr(lifetime.psutil, 'Process', lambda pid: parent)
        assert lifetime.game_parent() is parent
    for name in ('zsh', 'Terminal', 'python', 'cmd.exe'):
        monkeypatch.setattr(lifetime.psutil, 'Process', lambda pid: Parent(name))
        assert lifetime.game_parent() is None


def test_watcher_stops_when_owned_parent_exits():
    from sts2_autoplay.lifetime import ParentLifetime
    stop = threading.Event()
    with sleeping_process() as child:
        lifetime = ParentLifetime(stop, psutil.Process(child.pid), poll_seconds=.02)
        lifetime.start()
        assert not stop.wait(.05)
        child.terminate();child.wait(timeout=5)
        assert stop.wait(2), 'game exit must stop the service'
        assert lifetime.parent_exited.is_set()
        lifetime.close()


def test_terminal_mode_needs_explicit_stop():
    from sts2_autoplay.lifetime import ParentLifetime
    stop = threading.Event()
    lifetime = ParentLifetime(stop, None, poll_seconds=.01)
    lifetime.start()
    assert not stop.wait(.03) and not lifetime.parent_exited.is_set()
    stop.set()
    lifetime.close()


def test_game_exit_closes_journal_and_releases_service_lease(tmp_path, monkeypatch):
    from sts2_autoplay import __main__ as cli
    from sts2_autoplay.service_lease import ServiceLease
    import json
    local = tmp_path / 'state'
    out = tmp_path / 'journal'
    monkeypatch.setattr(cli, 'LOCAL', local)
    monkeypatch.setattr(cli.signal, 'signal', lambda *args: None)
    controls = []
    class Transport:
        endpoint = 'http://127.0.0.1:15539/api/v1/agent'
        def __init__(self, *args, **kwargs):pass
        def control(self, **kwargs):controls.append(kwargs);return {}
    class Policies:
        identities = {'strategy': {'model_id': 'fixture'}, 'battle': {'model_id': 'fixture'}}
        label = 'fixture'
        closed = False
        def __init__(self, *args, **kwargs):pass
        def check_contract(self, *args):pass
        def close(self):Policies.closed = True
    monkeypatch.setattr(cli, 'AgentTransport', Transport)
    monkeypatch.setattr(cli, 'FrozenPolicies', Policies)
    # Keep the real Controller.run/close, journal and lease; replace only its tick
    # with an idle decision so this test never contacts a game or loads weights.
    monkeypatch.setattr(cli.Controller, 'tick', lambda self: {'status': 'waiting'})
    with sleeping_process() as child:
        monkeypatch.setattr(cli, 'game_parent', lambda: psutil.Process(child.pid))
        timer = threading.Timer(.1, child.terminate)
        timer.start()
        try:
            assert cli.main(['--out', str(out), '--endpoint', Transport.endpoint, '--poll-seconds', '.01']) == 0
        finally:
            timer.join(timeout=2)
    closed = json.loads((out / 'SERVICE_CLOSED.json').read_text())
    assert closed['reason'] == 'parent game exited'
    assert controls[-1]['mode'] == 'paused' and Policies.closed
    lease = ServiceLease(local, Transport.endpoint)
    lease.acquire();lease.close()
