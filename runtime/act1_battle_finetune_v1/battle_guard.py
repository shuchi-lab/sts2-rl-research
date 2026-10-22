"""Isolated-battle guard for act1ft: the GuardedBattleWorker facade and its Python client.

    python -m act1_battle_finetune_v1.battle_guard build      # compile + native self-test + certificate
    python -m act1_battle_finetune_v1.battle_guard verify --certificate-sha256 SHA

Isolated fine-tuning, validation, benchmark, reproduction, robustness and heldout battles run through
the ORIGINAL prebuilt build08 ``RlCombatWorker.dll``. Its ``step`` / ``step_many`` go straight to
``Session.Step``, without the reviewed class-A/class-B ``AutoplayLoopGuard`` that the full-run bridge
applies. ``worker/GuardedBattleWorker`` is a facade over the unmodified original assembly. It checks
each chosen legal option against the exact live ``CombatState`` before the original ``Session.Step``
runs, and refuses a guarded play as a visible ``unsupported`` episode with reason
``limit:battle_unbounded_autoplay_loop``. A refusal makes no engine call and uses no RNG, and the
whole episode never reaches replay. Allowed steps go through the original implementation unchanged.

Identity is kept separate. The hello fields written by the original ``Program.Hello`` still name the
original assemblies, so vocab, adapter and old provenance are unchanged. The facade's own identity
(wrapper sha, guard sha, reflection contract) arrives under ``hello["battle_guard"]``. It is checked
against an explicit guard certificate, whose own sha256 the caller must pin.

``install_guarded_worker(ctx, ...)`` replaces the worker of a freshly constructed original Context
before any reset. ``GuardedWorker.call`` is bounded: a timeout fails visibly with retained evidence,
never restarts, and never sends another request on the same process.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import subprocess
import sys
import threading
import time
from collections import deque
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from crl import paths
from crl import worker as crl_worker
from crl.worker import WorkerError

PACKAGE = Path(__file__).resolve().parent
WRAPPER_DIR = PACKAGE / "worker" / "GuardedBattleWorker"
PROJECT = WRAPPER_DIR / "GuardedBattleWorker.csproj"
BUILD_OUT = WRAPPER_DIR / "_build" / "out"
WRAPPER_DLL = BUILD_OUT / "GuardedBattleWorker.dll"
CERTIFICATE = WRAPPER_DIR / "GUARD_CERTIFICATE.json"
SOURCE_FILES = ("AutoplayLoopGuard.cs", "GuardedProgram.cs", "SelfTest.cs", "GuardedBattleWorker.csproj",
                "Directory.Build.props")

CERTIFICATE_SCHEMA = "act1ft/battle_guard_certificate/v1"
FACADE_SCHEMA = "act1ft/guarded_battle_worker/v1"
EVIDENCE_SCHEMA = "act1ft/battle_guard_install/v1"
LIMIT_REASON = "battle_unbounded_autoplay_loop"
REFUSAL_STATUS = "unsupported"
REFUSAL_REASON = "limit:" + LIMIT_REASON

# The reviewed guard (ROOT_STRATEGY_AND_GUARD_RECEIPT.json): class A + class B, byte-identical source.
REVIEWED_GUARD_SHA256 = "8a43e794432ebf2d0e8aad88d3150596be15bb523f9bfd1f5b9e3c4c49c64546"
REVIEWED_GUARD_CERTIFICATE_SHA256 = "6a84fd9e40ca8550dfa9a51befafba464faa2c72bf138decf4bf3d2d34d423f5"
REVIEWED_GUARD_CERTIFICATE = (paths.ROOT / "experiments/trials/run-strategy-quality-n4-n5-20k-v1/cpu_utilization_amendment_v1/"
                              "debug_worker_timeout_20261007_v1/GUARD_REPAIR_CERTIFICATE.json")
# The frozen build08 assemblies that frozen E's provenance names (= run_strategy_v1.build.EXPECTED_DEPENDENCY_SHA256).
ORIGINAL_DEPENDENCY_SHA256 = {
    "CombatWorker.dll": "ab408a807b1837c5da2e17b6785ca7ce18e49a78452271538bf33a1cbbe7cfb9",
    "RlCombatWorker.dll": "b49a635d51cfc68c5e0997342e96ee2a21fb2076e5f406c150194198ff018369",
    "Sts2Emulator.dll": "11d614a31ea796f5631cf163d876db56b5129c38b9a5e98e7ee7f1632f6b2b9b",
    "System.IO.Hashing.dll": "fbe8c2fcb4adc01da5c3c8ed0d727903b8fda1e6b84da3c447c9794615f30664",
}
DEPENDENCY_DIR = paths.ROOT / "experiments/trials/run-strategy-e-fixed-v1/frozen_e_dependencies/worker_build08"
# What the facade binds by exact reflection signature in the original RlCombatWorker (printed by --describe).
REFLECTION_CONTRACT = (
    "type RlCombatWorker.Program public static",
    "type RlCombatWorker.Session internal sealed",
    "type RlCombatWorker.StepInfo internal sealed",
    "method RlCombatWorker.Program::Dispatch private static System.Void (System.String, System.Text.Json.JsonElement, "
    "System.Text.Json.Utf8JsonWriter)",
    "field RlCombatWorker.Program::Sessions private static readonly System.Collections.Generic.Dictionary`2<System.Int32, "
    "RlCombatWorker.Session>",
    "field RlCombatWorker.Session::_status private System.String",
    "field RlCombatWorker.Session::_reason private System.String",
    "field RlCombatWorker.Session::_error private System.String",
    "field RlCombatWorker.Session::_legal private System.Collections.Generic.List`1<CombatWorker.Option>",
    "field RlCombatWorker.Session::_decision private System.Int32",
    "field RlCombatWorker.Session::_steps private System.Int32",
    "property RlCombatWorker.Session::Fight public Sts2Emulator.Training.TrainingCombat",
    "property Sts2Emulator.Training.TrainingCombat::State public Sts2Emulator.Core.CombatState",
    "method RlCombatWorker.Session::Step public RlCombatWorker.StepInfo (System.Int32, System.Int32)",
    "method RlCombatWorker.Session::WriteResponse public System.Void (System.Text.Json.Utf8JsonWriter, System.String, "
    "RlCombatWorker.StepInfo)",
    "ctor RlCombatWorker.StepInfo(System.Int32 Decision, System.Int32 Option, System.String Kind, System.Int32 Action, "
    "System.Int32 Target, System.Int32 TurnBefore, System.Int32 TurnAfter, System.Boolean Terminal, "
    "System.Boolean PlayerWon, System.Single EngineReward)",
    "method FullRunWorker.AutoplayLoopGuard::Check public static System.String (Sts2Emulator.Core.CombatState, "
    "CombatWorker.Option)",
)
REFUSAL_CONTRACT = {"status": REFUSAL_STATUS, "reason": REFUSAL_REASON, "limit_reason": LIMIT_REASON,
                    "engine_call": False, "rng_use": False, "mask_or_option_changed": False,
                    "substitute_action": False, "session_steps_incremented": False}
# The production watchdog the reviewed guard certificate names (production_worker_timeout_seconds).
DEFAULT_RPC_TIMEOUT_S = 900.0
QUIT_GRACE_S = 10.0
# Hello keys that legitimately differ between two processes of the same build.
PROCESS_LOCAL_HELLO_KEYS = ("pid",)
FRESH_SLOT_PROBE = 256


class BattleWorkerTimeout(WorkerError):
    """A request outlived the client deadline. The process may still be running; nothing is retried."""

    def __init__(self, evidence: dict):
        self.evidence = evidence
        super().__init__(f"guarded battle worker: {evidence['cmd']} exceeded {evidence['timeout_s']} s "
                         f"(process running: {evidence['process_running_at_timeout']}); no retry, no restart")


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256_file(path: Path | str) -> str:
    return paths.sha256_file(path)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def contract_sha256(lines=REFLECTION_CONTRACT) -> str:
    return sha256_bytes("\n".join(lines).encode())


def is_guard_refusal(reason: str | None) -> bool:
    """True for an episode/step the facade refused (``Episode.reason`` / reply ``reason``)."""
    return reason == REFUSAL_REASON


# ---------------------------------------------------------------------------------------------- build

def _dotnet(args: list[str], cwd: Path | None = None, timeout: float = 600) -> subprocess.CompletedProcess:
    return subprocess.run([str(paths.DOTNET), *args], env=paths.worker_env(), capture_output=True, text=True,
                          cwd=cwd, timeout=timeout)


def build_files(out: Path = BUILD_OUT) -> dict[str, str]:
    return {p.name: sha256_file(p) for p in sorted(out.iterdir()) if p.is_file()}


def build(verbose: bool = False) -> dict:
    """Compile the facade into ``_build/out``, run its native self-test, write the certificate."""
    guard = WRAPPER_DIR / "AutoplayLoopGuard.cs"
    if sha256_file(guard) != REVIEWED_GUARD_SHA256:
        raise WorkerError(f"{guard} is not the reviewed guard {REVIEWED_GUARD_SHA256}")
    for name, want in ORIGINAL_DEPENDENCY_SHA256.items():
        if sha256_file(DEPENDENCY_DIR / name) != want:
            raise WorkerError(f"{DEPENDENCY_DIR / name} is not the pinned original {name}")
    cmd = ["build", str(PROJECT), "-c", "Release", "-nodeReuse:false", "-p:UseSharedCompilation=false",
           f"-p:Act1ftFrozenDependencies={DEPENDENCY_DIR}", "-o", str(BUILD_OUT)]
    proc = _dotnet(cmd)
    if verbose or proc.returncode != 0:
        print(proc.stdout[-4000:], proc.stderr[-4000:])
    if proc.returncode != 0:
        raise WorkerError("dotnet build of GuardedBattleWorker failed")
    selftest = _dotnet([str(WRAPPER_DLL), "--selftest"])
    st = json.loads(selftest.stdout)
    if selftest.returncode != 0 or st["failed"] or not st["passed"]:
        raise WorkerError(f"native self-test failed: {[c for c in st['cases'] if not c['pass']]}")
    describe = _dotnet([str(WRAPPER_DLL), "--describe"])
    if describe.returncode != 0:
        raise WorkerError(f"--describe refused: {describe.stderr[-2000:]}")
    ident = json.loads(describe.stdout)
    if tuple(ident["reflection_contract"]) != REFLECTION_CONTRACT:
        raise WorkerError("compiled facade binds a different reflection contract than battle_guard.REFLECTION_CONTRACT")
    files = build_files()
    if ident["facade_assembly_sha256"] != files["GuardedBattleWorker.dll"]:
        raise WorkerError("--describe facade sha differs from the built file")
    sdk = _dotnet(["--version"])
    cert = {
        "schema": CERTIFICATE_SCHEMA,
        "created_utc": utc_now(),
        "purpose": "isolated-battle guard facade over the original prebuilt build08 RlCombatWorker.dll (act1ft)",
        "command": ["dotnet", *cmd],
        "dotnet_sdk_version": sdk.stdout.strip(),
        "sources": {name: sha256_file(WRAPPER_DIR / name) for name in SOURCE_FILES},
        "reviewed_guard": {
            "source_sha256": REVIEWED_GUARD_SHA256,
            "certificate_sha256": REVIEWED_GUARD_CERTIFICATE_SHA256,
            "certificate_present_at_build": REVIEWED_GUARD_CERTIFICATE.is_file()
            and sha256_file(REVIEWED_GUARD_CERTIFICATE) == REVIEWED_GUARD_CERTIFICATE_SHA256,
            "classes": ident["guard_classes"],
        },
        "original_dependencies_sha256": dict(sorted(ORIGINAL_DEPENDENCY_SHA256.items())),
        "build_files": files,
        "facade_assembly_sha256": files["GuardedBattleWorker.dll"],
        "reflection_contract": list(REFLECTION_CONTRACT),
        "reflection_contract_sha256": contract_sha256(),
        "original_il_sha256": ident["original_il_sha256"],
        "rl_worker_assembly_mvid": ident["rl_worker_assembly_mvid"],
        "refusal_contract": REFUSAL_CONTRACT,
        "native_selftest": {"passed": st["passed"], "failed": st["failed"], "gameplay": st["gameplay"],
                            "output_sha256": sha256_bytes(selftest.stdout.encode())},
        "deployment_rule": "deploy these exact build_files bytes (do not rebuild on the target host)",
        "gameplay_performed": False,
        "production_go": False,
    }
    paths.write_json(CERTIFICATE, cert)
    return cert


# ---------------------------------------------------------------------------------------------- certificate

def verify_certificate(certificate_path: Path | str = CERTIFICATE, certificate_sha256: str | None = None,
                       build_out: Path | str | None = None) -> dict:
    """The explicit guard certificate, checked against its pinned sha256 and the bytes on disk."""
    if not certificate_sha256:
        raise WorkerError("a guard certificate sha256 pin is required (no implicit trust)")
    path = Path(certificate_path)
    got = sha256_file(path)
    if got != certificate_sha256:
        raise WorkerError(f"guard certificate {path} sha256 {got} != pinned {certificate_sha256}")
    cert = json.loads(path.read_text(encoding="utf-8"))
    problems = []
    if cert.get("schema") != CERTIFICATE_SCHEMA:
        problems.append(f"schema {cert.get('schema')!r}")
    if cert.get("reviewed_guard", {}).get("source_sha256") != REVIEWED_GUARD_SHA256:
        problems.append("reviewed guard source sha")
    if cert.get("reviewed_guard", {}).get("certificate_sha256") != REVIEWED_GUARD_CERTIFICATE_SHA256:
        problems.append("reviewed guard certificate sha")
    if cert.get("original_dependencies_sha256") != ORIGINAL_DEPENDENCY_SHA256:
        problems.append("original dependency pins")
    if tuple(cert.get("reflection_contract", ())) != REFLECTION_CONTRACT \
            or cert.get("reflection_contract_sha256") != contract_sha256():
        problems.append("reflection contract")
    if cert.get("refusal_contract") != REFUSAL_CONTRACT:
        problems.append("refusal contract")
    files = cert.get("build_files", {})
    for name, want in ORIGINAL_DEPENDENCY_SHA256.items():
        if files.get(name) != want:
            problems.append(f"certified {name} is not the pinned original")
    if files.get("GuardedBattleWorker.dll") != cert.get("facade_assembly_sha256"):
        problems.append("facade sha")
    out = Path(build_out) if build_out else BUILD_OUT
    if out.is_dir():
        now = build_files(out)
        if set(now) != set(files):
            problems.append(f"build file set differs: {sorted(set(now) ^ set(files))}")
        problems += [f"{n} bytes differ from the certificate" for n, h in files.items() if now.get(n) not in (None, h)]
    else:
        problems.append(f"{out} missing")
    if problems:
        raise WorkerError(f"guard certificate {path} refused: {problems}")
    return {**cert, "_path": str(path.resolve()), "_sha256": got, "_build_out": str(out.resolve())}


def check_guard_hello(hello: dict, cert: dict) -> None:
    """The running facade is the certified build, over the original assemblies."""
    g = hello.get("battle_guard")
    if not isinstance(g, dict):
        raise WorkerError("hello has no battle_guard block: not the GuardedBattleWorker facade")
    want = {
        "schema": FACADE_SCHEMA,
        "facade_assembly_sha256": cert["facade_assembly_sha256"],
        "guard_source_sha256": REVIEWED_GUARD_SHA256,
        "dependencies_sha256": ORIGINAL_DEPENDENCY_SHA256,
        "reflection_contract": list(REFLECTION_CONTRACT),
        "reflection_contract_sha256": contract_sha256(),
        "original_il_sha256": cert["original_il_sha256"],
        "refusal": REFUSAL_CONTRACT,
        "guarded_commands": ["step", "step_many"],
    }
    bad = [k for k, v in want.items() if g.get(k) != v]
    if bad:
        raise WorkerError(f"battle_guard hello differs from the certificate: {bad}")
    if hello.get("protocol") != crl_worker.PROTOCOL or hello.get("observation_schema") != crl_worker.OBSERVATION_SCHEMA:
        raise WorkerError(f"original protocol {hello.get('protocol')} / {hello.get('observation_schema')}")
    for key, dll in crl_worker.OWNED_HELLO_OUTPUTS.items():
        if hello.get(key) != ORIGINAL_DEPENDENCY_SHA256[dll]:
            raise WorkerError(f"original hello {key} {str(hello.get(key))[:12]} is not the pinned {dll}")
    if hello.get("engine_line") != paths.BUILD08_ENGINE_VERSION:
        raise WorkerError(f"original engine_line {hello.get('engine_line')!r}")


# ---------------------------------------------------------------------------------------------- client

class GuardedWorker:
    """Drop-in for ``crl.worker.Worker`` on the certified facade, with a bounded RPC.

    ``self.hello`` is the original-equivalent hello (``battle_guard`` removed), so vocab/adapter and
    ``Context.provenance()`` see exactly the original worker's fields. ``self.guard_hello`` is the
    facade's separate identity.
    """

    def __init__(self, cert: dict, *, rpc_timeout_s: float = DEFAULT_RPC_TIMEOUT_S, argv: list[str] | None = None):
        if not cert.get("_sha256"):
            raise WorkerError("GuardedWorker needs a certificate returned by verify_certificate")
        if not rpc_timeout_s or rpc_timeout_s <= 0:
            raise ValueError("a positive rpc_timeout_s is required")
        self.cert = cert
        self.rpc_timeout_s = float(rpc_timeout_s)
        self.templates: set[str] = set()
        self.argv = argv or [str(paths.DOTNET), str(Path(cert["_build_out"]) / "GuardedBattleWorker.dll")]
        self.proc = subprocess.Popen(self.argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     env=paths.worker_env(), text=True, encoding="utf-8", bufsize=1 << 20)
        self.pid = self.proc.pid
        self._lock = threading.Lock()  # pending queue + stdin
        self._call_lock = threading.Lock()  # one request in flight per caller turn
        self._pending: deque[dict] = deque()
        self._stderr: deque[str] = deque(maxlen=400)
        self._dead: str | None = None
        self.failure: dict | None = None  # set once on timeout; the worker then never takes another request
        self.late_replies: list[dict] = []
        self.close_record: dict | None = None
        self._reader = threading.Thread(target=self._read, name=f"act1ft-guard-{self.pid}-reader", daemon=True)
        self._reader.start()
        threading.Thread(target=self._drain_stderr, name=f"act1ft-guard-{self.pid}-stderr", daemon=True).start()
        try:
            full = self.call({"cmd": "hello"})
            check_guard_hello(full, cert)
            self.guard_hello = full.pop("battle_guard")
            self.hello = full
        except BaseException:
            self.close()
            raise

    # ------------------------------------------------------------------ transport
    def _read(self) -> None:
        try:
            for line in self.proc.stdout:
                with self._lock:
                    item = self._pending.popleft() if self._pending else None
                if item is None:
                    self._dead = f"unsolicited worker output: {line[:200]}"
                    break
                if item["abandoned"]:  # answered after the client deadline: retained, never delivered
                    self.late_replies.append({"cmd": item["cmd"], "received_utc": utc_now(),
                                              "reply_sha256": sha256_bytes(line.encode()), "reply_head": line[:300]})
                    continue
                try:
                    item["future"].set_result(json.loads(line))
                except Exception as e:  # malformed line
                    item["future"].set_exception(WorkerError(f"malformed reply: {e}: {line[:200]}"))
        finally:
            self._dead = self._dead or f"worker exited (code {self.proc.poll()}): {self.stderr_tail()[-2000:]}"
            with self._lock:
                while self._pending:
                    item = self._pending.popleft()
                    if not item["future"].done():
                        item["future"].set_exception(WorkerError(self._dead))

    def _drain_stderr(self) -> None:
        for line in self.proc.stderr:
            self._stderr.append(line)

    def stderr_tail(self) -> str:
        return "".join(self._stderr)

    def call(self, cmd: dict) -> dict:
        with self._call_lock:
            if self.failure is not None:  # a timed-out process never receives another request
                raise BattleWorkerTimeout(self.failure)
            line = json.dumps(cmd, separators=(",", ":"))
            item = {"cmd": cmd.get("cmd"), "future": concurrent.futures.Future(), "abandoned": False}
            with self._lock:
                if self._dead:
                    raise WorkerError(self._dead)
                self._pending.append(item)
                t0 = time.monotonic()
                started = utc_now()
                try:
                    self.proc.stdin.write(line + "\n")
                    self.proc.stdin.flush()
                except (BrokenPipeError, OSError) as e:
                    self._pending.remove(item)
                    raise WorkerError(f"worker pipe closed: {e}: {self.stderr_tail()[-2000:]}") from e
            try:
                r = item["future"].result(timeout=self.rpc_timeout_s)
            except concurrent.futures.TimeoutError:
                with self._lock:
                    item["abandoned"] = True
                    if item["future"].done():  # answered in the same instant: retained, still not delivered
                        self.late_replies.append({"cmd": item["cmd"], "received_utc": utc_now(), "race": True})
                rc = self.proc.poll()
                self.failure = {
                    "schema": "act1ft/battle_worker_timeout/v1", "cmd": item["cmd"], "timeout_s": self.rpc_timeout_s,
                    "started_utc": started, "timed_out_utc": utc_now(), "elapsed_s": round(time.monotonic() - t0, 3),
                    "request_sha256": sha256_bytes(line.encode()), "request_bytes": len(line),
                    "request": line if len(line) <= 200_000 else line[:200_000],
                    "request_truncated": len(line) > 200_000,
                    "pid": self.pid, "process_running_at_timeout": rc is None, "returncode_at_timeout": rc,
                    "stderr_tail": self.stderr_tail()[-20_000:],
                    "note": "client deadline only: not evidence that the process stopped; no retry, no restart",
                }
                raise BattleWorkerTimeout(self.failure) from None
        if not r.get("ok"):
            raise WorkerError(f"{r.get('cmd')}: {r.get('error_type')}: {r.get('message')}\n{r.get('stack')}")
        return r

    @property
    def alive(self) -> bool:
        return self._dead is None and self.failure is None and self.proc.poll() is None

    # ------------------------------------------------------------------ crl.worker.Worker API
    def add_template(self, template_id: str, engine_input: dict) -> None:
        if template_id not in self.templates:
            self.call({"cmd": "add_template", "template_id": template_id, "engine_input": engine_input})
            self.templates.add(template_id)

    def reset(self, env: int, template_id: str, env_seed: int, *, max_steps: int, max_turns: int,
              rng_mode: str = "training", purity_check: bool = False, allow_potions: bool = False) -> dict:
        cmd = {"cmd": "reset", "env": env, "template_id": template_id, "env_seed": env_seed,
               "max_steps": max_steps, "max_turns": max_turns, "rng_mode": rng_mode, "purity_check": purity_check}
        if allow_potions:
            if "potion_actions" not in self.hello:
                raise WorkerError("allow_potions requested but this worker build has no potion_actions contract")
            cmd["allow_potions"] = True
        r = self.call(cmd)
        if allow_potions and r.get("allow_potions") is not True:
            raise WorkerError(f"env {env}: worker did not confirm allow_potions (a build that ignores the flag)")
        return r

    def step(self, env: int, decision: int, option: int) -> dict:
        return self.call({"cmd": "step", "env": env, "decision": decision, "option": option})

    def step_many(self, steps: list[tuple[int, int, int]]) -> list[dict]:
        r = self.call({"cmd": "step_many", "steps": [{"env": e, "decision": d, "option": o} for e, d, o in steps]})
        return r["results"]

    def engine_dump(self, env: int) -> dict:
        return self.call({"cmd": "engine_dump", "env": env})["engine"]

    def close(self) -> dict:
        """Quit lifecycle. After a timeout the native step may still be running, so quit is sent and
        given ``QUIT_GRACE_S``; only then is this client's own child process killed. Recorded either way."""
        if self.close_record is not None:
            return self.close_record
        rec: dict[str, Any] = {"pid": self.pid, "after_timeout": self.failure is not None, "utc": utc_now()}
        rc = self.proc.poll()
        rec["returncode_before_close"] = rc
        if rc is None:
            try:
                with self._lock:
                    self.proc.stdin.write('{"cmd":"quit"}\n')
                    self.proc.stdin.flush()
                rec["quit_sent"] = True
            except (BrokenPipeError, OSError) as e:
                rec["quit_sent"] = False
                rec["quit_error"] = str(e)
            try:
                self.proc.wait(timeout=QUIT_GRACE_S)
                rec["exited_after_quit"] = True
            except subprocess.TimeoutExpired:
                rec["exited_after_quit"] = False
                self.proc.kill()  # this client's own child only
                self.proc.wait(timeout=QUIT_GRACE_S)
                rec["killed_own_child"] = True
        rec["returncode"] = self.proc.returncode
        rec["late_replies"] = len(self.late_replies)
        self.close_record = rec
        if self.failure is not None:
            self.failure["close"] = rec
        return rec

    def evidence(self) -> dict:
        return {"pid": self.pid, "argv": self.argv, "certificate_sha256": self.cert["_sha256"],
                "rpc_timeout_s": self.rpc_timeout_s, "guard_hello": getattr(self, "guard_hello", None),
                "failure": self.failure, "late_replies": self.late_replies, "close": self.close_record}

    def __enter__(self) -> "GuardedWorker":
        return self

    def __exit__(self, *exc) -> None:
        self.close()


# ---------------------------------------------------------------------------------------------- install

def original_hello_view(hello: dict) -> dict:
    return {k: v for k, v in hello.items() if k not in PROCESS_LOCAL_HELLO_KEYS and k != "battle_guard"}


def assert_no_sessions(worker, slots: int = FRESH_SLOT_PROBE) -> None:
    """Read-only freshness probe on the original worker: no env slot below ``slots`` holds a session
    (the original answers ``engine_dump`` for an unknown env with "env N not reset" and changes nothing)."""
    for env in range(slots):
        try:
            worker.call({"cmd": "engine_dump", "env": env})
        except WorkerError as e:
            if f"env {env} not reset" in str(e):
                continue
            raise
        raise WorkerError(f"original worker already holds env {env}: install the guard before any reset")


def install_guarded_worker(ctx, *, certificate_path: Path | str = CERTIFICATE, certificate_sha256: str,
                           rpc_timeout_s: float = DEFAULT_RPC_TIMEOUT_S,
                           worker_factory: Callable[[dict, float], GuardedWorker] | None = None) -> dict:
    """Replace a freshly constructed original Context's worker by the certified guard facade.

    Must run right after the Context is constructed, before any reset, replay, update, baseline
    evaluation or native gameplay. Vocab, adapter, templates and ``ctx.provenance()`` are kept
    unchanged (checked). The separate guard evidence goes to ``ctx.battle_guard``. The old worker
    has played no game; it is closed cleanly, and a nonzero exit code fails the install.
    """
    old = ctx.worker
    if getattr(ctx, "battle_guard", None) is not None or isinstance(old, GuardedWorker):
        raise WorkerError("this Context already has a guarded worker")
    if not isinstance(old, crl_worker.Worker) or old.proc.poll() is not None:
        raise WorkerError(f"expected a live original crl.worker.Worker, got {type(old).__name__}")
    if set(old.templates) != set(ctx.templates):
        raise WorkerError("original worker templates differ from ctx.templates (not a fresh Context)")
    assert_no_sessions(old)
    cert = verify_certificate(certificate_path, certificate_sha256)
    prov_before = ctx.provenance()
    new = (worker_factory or (lambda c, t: GuardedWorker(c, rpc_timeout_s=t)))(cert, rpc_timeout_s)
    try:
        if original_hello_view(new.hello) != original_hello_view(old.hello):
            diff = sorted(k for k in set(new.hello) | set(old.hello) if k not in PROCESS_LOCAL_HELLO_KEYS
                          and new.hello.get(k) != old.hello.get(k))
            raise WorkerError(f"guarded worker's original hello differs from the original worker's: {diff}")
        crl_worker.check_owned_hello(new.hello)
        for tid, t in ctx.templates.items():
            new.add_template(tid, t["engine_input"])
        if new.templates != set(ctx.templates):
            raise WorkerError("not every original template reached the guarded worker")
        ctx.worker = new
        prov_after = ctx.provenance()
        if paths.canonical(prov_after) != paths.canonical(prov_before):
            ctx.worker = old
            raise WorkerError("ctx.provenance() changed when the guarded worker was installed")
    except BaseException:
        if ctx.worker is new:
            ctx.worker = old
        new.close()
        raise
    old_pid = old.proc.pid
    old.close()
    if old.proc.returncode != 0:
        raise WorkerError(f"original no-games worker pid {old_pid} exited {old.proc.returncode} on quit")
    evidence = {
        "schema": EVIDENCE_SCHEMA, "installed_utc": utc_now(),
        "certificate_path": cert["_path"], "certificate_sha256": cert["_sha256"],
        "facade_assembly_sha256": cert["facade_assembly_sha256"], "guard_source_sha256": REVIEWED_GUARD_SHA256,
        "reviewed_guard_certificate_sha256": REVIEWED_GUARD_CERTIFICATE_SHA256,
        "reflection_contract_sha256": contract_sha256(), "refusal_contract": REFUSAL_CONTRACT,
        "guard_hello": new.guard_hello, "rpc_timeout_s": new.rpc_timeout_s, "guarded_pid": new.pid,
        "original_hello_digest": paths.digest(original_hello_view(old.hello)),
        "replaced_worker": {"pid": old_pid, "returncode": old.proc.returncode, "games_played": 0,
                            "fresh_slot_probe": FRESH_SLOT_PROBE},
        "templates": len(ctx.templates), "provenance_digest_unchanged": paths.digest(prov_before),
        "battle_guard_module_sha256": sha256_file(__file__),
    }
    ctx.battle_guard = evidence
    return evidence


def require_guarded(ctx) -> dict:
    """Runtime gate for every isolated battle stage: the context runs the live certified facade."""
    w = ctx.worker
    ev = getattr(ctx, "battle_guard", None)
    if not isinstance(w, GuardedWorker) or ev is None:
        raise WorkerError("isolated battles must run on the guarded worker (install_guarded_worker)")
    if not w.alive:
        raise WorkerError(f"guarded worker not alive: {w.failure or w._dead or w.proc.poll()}")
    if ev["certificate_sha256"] != w.cert["_sha256"]:
        raise WorkerError("ctx.battle_guard does not match the running worker's certificate")
    return ev


# ---------------------------------------------------------------------------------------------- CLI

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="python -m act1_battle_finetune_v1.battle_guard")
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("--verbose", action="store_true")
    v = sub.add_parser("verify")
    v.add_argument("--certificate", default=str(CERTIFICATE))
    v.add_argument("--certificate-sha256", required=True)
    a = ap.parse_args(argv)
    if a.cmd == "build":
        cert = build(a.verbose)
        print(json.dumps({"certificate": str(CERTIFICATE), "certificate_sha256": sha256_file(CERTIFICATE),
                          "facade_assembly_sha256": cert["facade_assembly_sha256"],
                          "native_selftest": cert["native_selftest"]}, indent=1))
        return 0
    cert = verify_certificate(a.certificate, a.certificate_sha256)
    with GuardedWorker(cert, rpc_timeout_s=120) as w:
        print(json.dumps({"verified": True, "certificate_sha256": cert["_sha256"], "guard_hello": w.guard_hello},
                         indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
