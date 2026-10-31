"""Fresh append-only public evidence for one inference service and its live runs."""
from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
import os
import subprocess
from pathlib import Path
import platform
import re
import time

def process_stamp(pid):
    try:
        value=subprocess.check_output(['ps','-p',str(pid),'-o','lstart='],text=True,stderr=subprocess.DEVNULL,
                                      env={**os.environ,'LC_ALL':'C','TZ':'UTC'},timeout=2)
        return ' '.join(value.split())
    except (OSError,subprocess.SubprocessError):return None


class Journal:
    def __init__(self, out: Path, *, identities: dict, endpoint: str):
        self.out = Path(out).resolve()
        self.out.mkdir(parents=True, exist_ok=False)
        self.closed, self.sequence = False, 0
        self.stream = open(self.out / 'events.jsonl', 'x', encoding='utf-8')
        self.runs = {}
        manifest = {'schema': 'sts2.live_journal/v1', 'pid': os.getpid(), 'process_started':process_stamp(os.getpid()),'created_utc': self._utc(),
                    'endpoint': endpoint, 'models': identities, 'python': platform.python_version(),
                    'recorded_state': 'public snapshots and actual request lifecycle; no hidden game state'}
        (self.out / 'manifest.json').write_text(json.dumps(manifest, indent=2, allow_nan=False) + '\n')
        self.manifest = manifest
        self.event('service_start', manifest, durable=True)

    def link_native_recording(self, directory: Path):
        """Link a recorder root started by the user/root; never start or alter it."""
        directory = Path(directory).resolve()
        if not directory.is_dir():raise ValueError('native recorder directory does not exist')
        self.manifest['native_recording_directory'] = str(directory)
        temporary = self.out / 'manifest.json.tmp'
        temporary.write_text(json.dumps(self.manifest, indent=2, allow_nan=False) + '\n')
        temporary.replace(self.out / 'manifest.json')
        self.event('native_recorder_reference', {'directory': str(directory),
                   'role': 'native raw UI/per-stage action/save/history evidence; independent of model inputs'}, durable=True)

    @staticmethod
    def _utc():
        return datetime.now(timezone.utc).isoformat()

    def event(self, name: str, data: dict, *, run_id=None, durable=False):
        if self.closed:
            raise RuntimeError('live journal already closed')
        self.sequence += 1
        record = {'schema': 'sts2.live_event/v1', 'sequence': self.sequence, 'utc': self._utc(),
                  'monotonic': time.monotonic(), 'event': name, 'run_id': run_id, 'data': data}
        line = json.dumps(record, allow_nan=False, separators=(',', ':')) + '\n'
        self.stream.write(line);self.stream.flush()
        if durable:os.fsync(self.stream.fileno())
        if run_id is not None:
            if run_id not in self.runs:
                slug = re.sub(r'[^A-Za-z0-9_-]', '_', str(run_id))[:60] or 'run'
                suffix = hashlib.sha256(str(run_id).encode()).hexdigest()[:12]
                directory = self.out / 'runs' / f'{slug}-{suffix}'
                directory.mkdir(parents=True, exist_ok=False)
                (directory / 'identity.json').write_text(json.dumps({'run_id': run_id}, indent=2) + '\n')
                self.runs[run_id] = open(directory / 'events.jsonl', 'x', encoding='utf-8')
            stream = self.runs[run_id]
            stream.write(line);stream.flush()
            if durable:os.fsync(stream.fileno())
        return record

    def close(self, exit_code=0, reason='closed', *, pending=None):
        if self.closed:return
        # The actual OS exit belongs to the caller's process monitor. This file
        # records the exit requested by the service after its finally block ran.
        result = {'requested_exit_code': exit_code, 'reason': reason, 'pending': pending,
                  'closed_utc': self._utc(), 'actual_os_exit_requires_parent_observation': True}
        self.event('service_exit', result, durable=True)
        (self.out / 'SERVICE_CLOSED.json').write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
        for stream in self.runs.values():stream.close()
        self.stream.close();self.closed = True
