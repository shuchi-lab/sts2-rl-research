"""Persistent JSON-lines client of worker/FullRunWorker (Task 2).

One process hosts many runs; commands are answered in order. A reader thread resolves one
:class:`concurrent.futures.Future` per command in FIFO order, so a dispatcher can keep several
workers busy and drain replies asynchronously. Startup verifies the NEW bridge identity (its own
build manifest) and, separately, the ORIGINAL worker hello the bridge nests from the prebuilt
RlCombatWorker.dll: the two are different evidence and never substituted for each other.
"""
from __future__ import annotations

import json
import os
import subprocess
import threading
from collections import deque
from concurrent.futures import Future
from pathlib import Path

from . import build
from .config import LimitsConfig
from .contracts import DECISION_FLOW, OBSERVATION_SCHEMA, WORKER_PROTOCOL, ContractError, StepReply

ORIGINAL_KEYS = ("engine_assembly_sha256", "cds_worker_assembly_sha256", "rl_worker_assembly_sha256")
EXPECTED_ORIGINAL = {
    "protocol": "crl.worker_protocol/v1",
    "observation_schema": "crl.raw_observation/v3",
    "engine_assembly_sha256": build.EXPECTED_DEPENDENCY_SHA256["Sts2Emulator.dll"],
    "cds_worker_assembly_sha256": build.EXPECTED_DEPENDENCY_SHA256["CombatWorker.dll"],
    "rl_worker_assembly_sha256": build.EXPECTED_DEPENDENCY_SHA256["RlCombatWorker.dll"],
}


class WorkerError(RuntimeError):
    pass


class StaleRequest(ContractError):
    pass


def check_hello(hello: dict) -> None:
    if hello.get("protocol") != WORKER_PROTOCOL:
        raise WorkerError(f"bridge protocol {hello.get('protocol')!r} is not {WORKER_PROTOCOL}")
    if hello.get("decision_flow") != DECISION_FLOW:
        raise WorkerError(f"bridge decision flow {hello.get('decision_flow')!r} is not {DECISION_FLOW}")
    if hello.get("observation_schema") != OBSERVATION_SCHEMA:
        raise WorkerError(f"bridge observation schema {hello.get('observation_schema')!r} is not {OBSERVATION_SCHEMA}")
    deps = hello["dependencies"]
    for name, want in build.EXPECTED_DEPENDENCY_SHA256.items():
        if deps.get(name) != want:
            raise WorkerError(f"bridge loaded {name} {deps.get(name)} instead of the frozen {want}")
    orig = hello["original_hello"]
    for k, v in EXPECTED_ORIGINAL.items():
        if orig.get(k) != v:
            raise WorkerError(f"nested original hello {k}={orig.get(k)!r}, expected {v!r}")
    forbidden = set(hello["bridge"]["forbidden_members"])
    for m in hello["bridge"]["reflected_members"]:
        if any(m.startswith(f) for f in forbidden):
            raise WorkerError(f"bridge reflects a forbidden member: {m}")
    if build.BUILD_MANIFEST.is_file():
        manifest = json.loads(build.BUILD_MANIFEST.read_text(encoding="utf-8"))
        if hello["bridge"]["assembly_sha256"] != manifest["bridge_assembly_sha256"]:
            raise WorkerError("running FullRunWorker.dll differs from its build manifest")


class RunWorker:
    def __init__(self, dll: Path = build.WORKER_DLL, compatibility: dict | None = None,
                 limits: LimitsConfig | None = None, *, purity_check: bool = False, name: str = "w0",
                 dotnet: Path = build.DOTNET, debug: bool = False):
        dll = Path(dll)
        if not dll.is_file():
            raise WorkerError(f"{dll} missing; run `python -m run_strategy_v1.build`")
        if compatibility is None:
            raise ValueError("a worker needs the resolved compatibility manifest")
        self.name = name
        env = build.dotnet_env()
        env.pop("RS1_WORKER_DEBUG", None)
        if debug:  # test fixtures only; production workers must report debug_enabled false
            env["RS1_WORKER_DEBUG"] = "1"
        self.debug = debug
        self.proc = subprocess.Popen([str(dotnet), str(dll)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, env=env, text=True, encoding="utf-8", bufsize=1 << 20)
        self._pending: deque[Future] = deque()
        self._lock = threading.Lock()
        self._dead: str | None = None
        self._stderr: list[str] = []
        self._reader = threading.Thread(target=self._read, name=f"rs1-{name}-reader", daemon=True)
        self._reader.start()
        threading.Thread(target=self._drain_stderr, daemon=True).start()
        try:
            self.hello = self.call({"cmd": "hello"})
            check_hello(self.hello)
            if bool(self.hello.get("debug_enabled")) != debug:
                raise WorkerError(f"worker debug_enabled={self.hello.get('debug_enabled')} but debug={debug}")
            lim = limits or LimitsConfig()
            self.configured = self.call({"cmd": "configure", "compatibility": compatibility, "limits": {
                "max_battle_steps": lim.battle_steps, "max_turns": lim.worker_max_turns,
                "max_strategic_decisions": lim.strategic_decisions, "max_auto_actions": lim.auto_actions,
                "max_repeated_public_state": lim.repeated_public_state, "purity_check": purity_check}})
            if self.configured.get("decision_flow") != DECISION_FLOW:
                raise WorkerError(f"configured decision flow {self.configured.get('decision_flow')!r}")
        except BaseException:
            self.close()
            raise
        self.pid = self.proc.pid

    # ------------------------------------------------------------------ transport
    def _read(self) -> None:
        try:
            for line in self.proc.stdout:
                with self._lock:
                    fut = self._pending.popleft() if self._pending else None
                if fut is None:
                    self._dead = f"unsolicited worker output: {line[:200]}"
                    break
                try:
                    fut.set_result(json.loads(line))
                except Exception as e:  # malformed line
                    fut.set_exception(WorkerError(f"malformed reply: {e}: {line[:200]}"))
        finally:
            self._dead = self._dead or f"worker exited (code {self.proc.poll()}): {''.join(self._stderr)[-2000:]}"
            with self._lock:
                while self._pending:
                    self._pending.popleft().set_exception(WorkerError(self._dead))

    def _drain_stderr(self) -> None:
        for line in self.proc.stderr:
            self._stderr.append(line)
            del self._stderr[:-200]

    def submit(self, cmd: dict) -> Future:
        fut: Future = Future()
        with self._lock:
            if self._dead:
                fut.set_exception(WorkerError(self._dead))
                return fut
            self._pending.append(fut)
            try:
                self.proc.stdin.write(json.dumps(cmd, separators=(",", ":")) + "\n")
                self.proc.stdin.flush()
            except (BrokenPipeError, OSError) as e:
                self._pending.remove(fut)
                fut.set_exception(WorkerError(f"worker pipe closed: {e}"))
        return fut

    def call(self, cmd: dict, timeout: float | None = 600.0) -> dict:
        r = self.submit(cmd).result(timeout=timeout)
        if not r.get("ok"):
            if r.get("type") == "stale_request":
                raise StaleRequest(f"{r.get('cmd')}: {r.get('message')}")
            raise WorkerError(f"{r.get('cmd')}: {r.get('error_type')}: {r.get('message')}\n{r.get('stack')}")
        return r

    @property
    def alive(self) -> bool:
        return self._dead is None and self.proc.poll() is None

    # ------------------------------------------------------------------ run API
    def reset(self, run_id: str, seed: str, ascension: int = 2, *, slot: int = 0) -> StepReply:
        if not isinstance(seed, str) or not seed:
            raise ValueError("seed is the engine's opaque seed string (run_strategy_v1.seeds.seed_string)")
        return StepReply.parse(self.call({"cmd": "reset", "run_id": run_id, "slot": slot, "seed": seed,
                                          "ascension": ascension}))

    def step(self, run_id: str, decision_id: str, candidate_id: str) -> StepReply:
        return StepReply.parse(self.call(strategy_request(run_id, decision_id, candidate_id)))

    def step_battle(self, run_id: str, decision_id: str, battle_decision: int, ordinal: int) -> StepReply:
        return StepReply.parse(self.call(battle_request(run_id, decision_id, battle_decision, ordinal)))

    def step_many(self, requests: list[dict]) -> list[StepReply | Exception]:
        return parse_many(self.call({"cmd": "step_many", "steps": requests}))

    def abandon(self, run_id: str, kind: str, reason: str, detail: str | None = None) -> StepReply:
        return StepReply.parse(self.call({"cmd": "abandon", "run_id": run_id, "kind": kind, "reason": reason,
                                          "detail": detail}))

    def close_run(self, run_id: str) -> None:
        self.call({"cmd": "close", "run_id": run_id})

    def event_table(self) -> dict:
        return self.call({"cmd": "event_table"})["table"]

    def audit_facade(self) -> dict:
        return self.call({"cmd": "audit_facade"})

    # ------------------------------------------------------------------ test seams (debug workers only)
    def debug_mutate(self, run_id: str, op: str, **kw) -> StepReply:
        return StepReply.parse(self.call({"cmd": "debug_mutate", "run_id": run_id, "op": op, **kw}))

    def debug_reproject(self, run_id: str) -> dict:
        return self.call({"cmd": "debug_reproject", "run_id": run_id})

    def debug_resampled_clone(self, run_id: str, new_run_id: str, resample_seed: int) -> tuple[dict, StepReply]:
        r = self.call({"cmd": "debug_resampled_clone", "run_id": run_id, "new_run_id": new_run_id,
                       "resample_seed": resample_seed})
        return r, StepReply.parse(r["reply"])

    def parity_probe(self, engine_input: dict, env_seed: int, options: list[int], *, template_id: str = "parity",
                     max_steps: int = 2000, max_turns: int = 99, with_terminal: bool = False):
        r = self.call({"cmd": "parity_probe", "engine_input": engine_input, "env_seed": env_seed,
                       "options": options, "template_id": template_id, "max_steps": max_steps,
                       "max_turns": max_turns})
        return r["decisions"] if not with_terminal else (r["decisions"], r.get("terminal"), r["final_status"])

    def kill(self) -> None:
        """Terminate a hung/failed worker process (its pending futures then fail with WorkerError)."""
        if self.proc.poll() is None:
            self.proc.kill()
            self.proc.wait(timeout=10)

    def close(self) -> None:
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write('{"cmd":"quit"}\n')
                self.proc.stdin.flush()
                self.proc.wait(timeout=10)
            except Exception:
                self.proc.kill()
                self.proc.wait(timeout=10)

    def __enter__(self) -> "RunWorker":
        return self

    def __exit__(self, *exc) -> None:
        self.close()


def strategy_request(run_id: str, decision_id: str, candidate_id: str) -> dict:
    return {"cmd": "step", "run_id": run_id, "decision_id": decision_id, "owner": "strategy",
            "candidate_id": candidate_id}


def battle_request(run_id: str, decision_id: str, battle_decision: int, ordinal: int) -> dict:
    return {"cmd": "step", "run_id": run_id, "decision_id": decision_id, "owner": "battle",
            "battle_decision": int(battle_decision), "option": int(ordinal)}


def parse_many(r: dict) -> list[StepReply | Exception]:
    out: list[StepReply | Exception] = []
    for x in r["results"]:
        if not x.get("ok"):
            cls = StaleRequest if x.get("type") == "stale_request" else WorkerError
            out.append(cls(f"{x.get('error_type')}: {x.get('message')}"))
        else:
            out.append(StepReply.parse(x))
    return out
