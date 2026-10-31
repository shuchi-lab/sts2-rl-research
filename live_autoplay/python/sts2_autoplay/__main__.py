"""Start frozen inference and public journaling; game-side controls authorize moves."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import signal
import threading

from .bootstrap import CHOICE_PRESETS, DEFAULT_CHOICE_PRESET
from .controller import Controller
from .lifetime import ParentLifetime, game_parent
from .policies import FrozenPolicies
from .recording import Journal
from .service_lease import ServiceAlreadyRunning, ServiceLease
from .transport import AgentTransport

PACKAGE = Path(__file__).resolve().parents[2]
LOCAL = PACKAGE / '.local'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project-root', type=Path, default=PACKAGE.parent, help='Repository containing models/ and runtime/')
    parser.add_argument('--endpoint', default='http://127.0.0.1:15526/api/v1/agent')
    parser.add_argument('--out', type=Path, help='New journal directory (default: live_autoplay/.local/runs/<timestamp>)')
    parser.add_argument('--dotnet', help='Path to dotnet with the .NET 9 runtime; saved by the installer')
    parser.add_argument('--check', action='store_true', help='Load both models and check the contract offline; no connection to the game')
    parser.add_argument('--poll-seconds', type=float, default=.15)
    parser.add_argument('--timeout-seconds', type=float, default=2.0)
    parser.add_argument('--native-recording-dir', type=Path,
                        help='Existing recorder directory started separately, retained as a read-only evidence reference')
    parser.add_argument('--choice-preset', choices=sorted(CHOICE_PRESETS), default=DEFAULT_CHOICE_PRESET,
                        help='The bundled v1.0.0 Choice90k N1 and Wide+deep Battle177.5k pair')
    args = parser.parse_args(argv)
    if args.poll_seconds <= 0:parser.error('--poll-seconds must be positive')
    if args.timeout_seconds <= 0:parser.error('--timeout-seconds must be positive')
    if args.dotnet:
        os.environ['RS1_DOTNET'] = str(Path(args.dotnet).expanduser().resolve())
    elif not os.environ.get('RS1_DOTNET') and (LOCAL / 'settings.json').is_file():
        os.environ['RS1_DOTNET'] = json.loads((LOCAL / 'settings.json').read_text())['dotnet']
    if args.check:
        policies = None
        try:
            policies = FrozenPolicies(args.project_root, choice_preset=args.choice_preset)
            policies.check_contract(PACKAGE / 'agent_contract.json')
            print(json.dumps({'check': 'passed', 'game_connections': 0, 'game_actions': 0,
                              'models': policies.identities}, indent=2), flush=True)
            return 0
        except Exception as error:
            print(f'Offline check failed: {type(error).__name__}: {error}', flush=True)
            return 1
        finally:
            if policies is not None:policies.close()
    args.out = args.out or LOCAL / 'runs' / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    args.out=args.out.expanduser().resolve()
    if args.out.exists():parser.error('--out must be a new journal directory')
    if args.native_recording_dir is not None:
        args.native_recording_dir=args.native_recording_dir.expanduser().resolve()
        if not args.native_recording_dir.is_dir():parser.error('--native-recording-dir must be an existing directory')
    stop, received = threading.Event(), []
    def handle_signal(number, frame):
        received.append(number);stop.set()
    for sig in (signal.SIGINT, signal.SIGTERM):signal.signal(sig, handle_signal)
    lease = ServiceLease(LOCAL, args.endpoint)
    try:
        lease.acquire()
    except ServiceAlreadyRunning:
        print('Frozen inference service is already running; no second controller started.', flush=True)
        return 0
    transport = policies = journal = controller = None
    lifetime = ParentLifetime(stop, game_parent())
    code, reason = 0, 'finished'
    try:
        lifetime.start()
        transport = AgentTransport(args.endpoint, timeout=args.timeout_seconds)
        policies = (FrozenPolicies(args.project_root) if args.choice_preset == DEFAULT_CHOICE_PRESET
                    else FrozenPolicies(args.project_root, choice_preset=args.choice_preset))
        policies.check_contract(PACKAGE / 'agent_contract.json')
        journal = Journal(args.out, identities=policies.identities, endpoint=transport.endpoint)
        if args.native_recording_dir is not None:journal.link_native_recording(args.native_recording_dir)
        controller = Controller(transport, policies, journal)
        try:
            pointer=LOCAL/'service-latest.json'
            pointer.parent.mkdir(parents=True,exist_ok=True)
            temporary=pointer.with_suffix('.json.tmp')
            temporary.write_text(json.dumps({'directory':str(journal.out)}))
            temporary.replace(pointer)
        except OSError as error:
            journal.event('ratings_pointer_unavailable',{'message':str(error)})
        choice = CHOICE_PRESETS[args.choice_preset]
        print(f"Frozen {choice['label']} ({choice['model_id']}) and Wide+deep Battle177.5k (enemy_wide_deep +32.5k) loaded. "
              f'Journal: {journal.out}', flush=True)
        print('Starting in Advice. Use Step or Autoplay inside the game to authorize actions.', flush=True)
        controller.run(stop, poll_seconds=args.poll_seconds)
        if received:code, reason = 128 + received[-1], f'signal {received[-1]}'
        elif lifetime.parent_exited.is_set():reason = 'parent game exited'
    except Exception as e:
        code, reason = 1, f'{type(e).__name__}: {e}'
        print(reason, flush=True)
        if controller is None and transport is not None:
            try:transport.control(mode='paused', message='Frozen service initialization failed: ' + reason)
            except Exception:pass
        if journal is not None:journal.event('fatal_error', {'message': reason}, durable=True)
    finally:
        lifetime.close()
        if controller is not None:controller.close(code, reason)
        elif journal is not None:journal.close(code, reason)
        if policies is not None:policies.close()
        lease.close()
    return code


if __name__ == '__main__':
    raise SystemExit(main())
